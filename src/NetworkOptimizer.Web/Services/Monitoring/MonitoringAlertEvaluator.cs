using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Watches latency-tier probe results for state transitions (up→down, down→up,
/// sustained packet loss) and publishes AlertEvents to the existing alert bus.
/// In-memory state only; on app restart we re-learn each target's state from the
/// next few probe cycles, which means a target that was down before restart will
/// re-emit a target_offline on the first failed probe after restart. That's the
/// right behavior - users want to be told if monitoring restarts and something is
/// still broken.
///
/// Thresholds are intentionally simple: 3 consecutive failures = offline, 3
/// consecutive successes after offline = recovered. Sustained-loss detection
/// looks for ≥30% loss across the trailing window. No flapping suppression
/// beyond the consecutive-failure threshold; AlertCooldownTracker upstream
/// already handles repeat-event suppression.
///
/// Per-target events are published for Fabric and Custom targets only. The WAN-facing
/// categories (access ISP, transit, internet, legacy WAN) fail together when the connection
/// does, so their per-target state feeds <see cref="WanOutageEvaluator"/> - which publishes
/// one per-WAN outage alert instead of one alert per target - while the state machine here
/// keeps running unchanged underneath for all types.
/// </summary>
public class MonitoringAlertEvaluator
{
    private const int FailuresToDeclareOffline = 3;
    private const int SuccessesToDeclareRecovered = 3;
    private const double SustainedLossThresholdPercent = 30.0;
    private const int LossWindowSize = 5;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<MonitoringAlertEvaluator> _logger;
    private readonly DeviceTransitionTracker _transitions;
    private readonly WanOutageEvaluator _wanOutages;
    private readonly ConcurrentDictionary<string, TargetAlertState> _states = new();
    private readonly string _siteSuffix;
    private readonly string _siteSlug;

    /// <param name="siteSlug">
    /// Site this instance evaluates for (one instance per site, owned by
    /// <see cref="MonitoringAlertRegistry"/> - target ids repeat across sites, so
    /// state must not be shared). Non-default sites get their slug appended to
    /// alert titles; the default site reads exactly as before.
    /// </param>
    public MonitoringAlertEvaluator(IAlertEventBus eventBus, ILogger<MonitoringAlertEvaluator> logger,
        DeviceTransitionTracker transitions, WanOutageEvaluator wanOutages,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _eventBus = eventBus;
        _logger = logger;
        _transitions = transitions;
        _wanOutages = wanOutages;
        _siteSlug = siteSlug ?? SiteManagementService.DefaultSiteSlug;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    public async ValueTask EvaluateAsync(MonitoringTarget target, PingProbeResult result, CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(target.TargetId, _ => new TargetAlertState());
        var publishPerTarget = !WanOutageEvaluator.CoversTargetType(target.TargetType);

        await EvaluatePerTargetAsync(target, result, state, publishPerTarget, ct);

        if (!publishPerTarget)
        {
            _wanOutages.RecordTargetState(target, state.IsOffline, state.IsLossy, state.ConsecutiveFailures);
            await _wanOutages.EvaluateAsync(ct);
        }
    }

    private async ValueTask EvaluatePerTargetAsync(MonitoringTarget target, PingProbeResult result,
        TargetAlertState state, bool publishPerTarget, CancellationToken ct)
    {
        if (result.Success)
        {
            state.ConsecutiveFailures = 0;
            state.ConsecutiveSuccesses++;
            state.TransitionSuppressionLogged = false;
            state.LossWindow.Enqueue(result.LossPercent);
            while (state.LossWindow.Count > LossWindowSize) state.LossWindow.Dequeue();

            if (state.IsOffline && state.ConsecutiveSuccesses >= SuccessesToDeclareRecovered)
            {
                state.IsOffline = false;
                if (publishPerTarget)
                    await _eventBus.PublishAsync(BuildRecoveredEvent(target, result), ct);
            }

            // Sustained-loss detection only matters while the target is nominally up.
            var avgLoss = state.LossWindow.Count > 0 ? state.LossWindow.Average() : 0;
            if (!state.IsOffline && state.LossWindow.Count >= LossWindowSize)
            {
                if (!state.IsLossy && avgLoss >= SustainedLossThresholdPercent)
                {
                    state.IsLossy = true;
                    if (publishPerTarget)
                        await _eventBus.PublishAsync(BuildSustainedLossEvent(target, avgLoss), ct);
                }
                else if (state.IsLossy && avgLoss < SustainedLossThresholdPercent / 2)
                {
                    // Hysteresis: only clear lossy state when loss drops well below the threshold
                    // so we don't flap on borderline averages.
                    state.IsLossy = false;
                }
            }
        }
        else
        {
            state.ConsecutiveSuccesses = 0;
            state.ConsecutiveFailures++;

            if (!state.IsOffline && state.ConsecutiveFailures >= FailuresToDeclareOffline)
            {
                // A device that UniFi reports as upgrading or provisioning stops answering because
                // someone asked it to. Stay silent for the transition, and deliberately leave
                // IsOffline unset: failures keep accruing, so the moment the device is no longer
                // in that state the next failed probe alerts immediately. Nothing is lost, and no
                // "recovered" event fires for an outage that was never announced.
                if (_transitions.IsInKnownTransition(_siteSlug, target.DeviceMac, DateTime.UtcNow))
                {
                    if (!state.TransitionSuppressionLogged)
                    {
                        state.TransitionSuppressionLogged = true;
                        _logger.LogDebug(
                            "Not declaring {Target} offline: UniFi reports its device ({Mac}) mid-transition (upgrade or provisioning)",
                            target.Name, target.DeviceMac);
                    }
                    return;
                }

                state.IsOffline = true;
                state.IsLossy = false; // offline supersedes lossy
                state.LossWindow.Clear();
                state.TransitionSuppressionLogged = false;
                if (publishPerTarget)
                    await _eventBus.PublishAsync(BuildOfflineEvent(target), ct);
            }
        }
    }

    private AlertEvent BuildOfflineEvent(MonitoringTarget target) => new()
    {
        EventType = "monitoring.target_offline",
        Source = "monitoring",
        Severity = TargetSeverity(target.TargetType, isOffline: true),
        Title = $"{target.Name} is offline{_siteSuffix}",
        Message = $"Monitoring target {target.Name} ({target.Address}) failed {FailuresToDeclareOffline} consecutive {target.ProbeMode.ToString().ToUpperInvariant()} probes.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        SourceUrl = TargetSourceUrl(target, DateTime.UtcNow),
        Tags = ["monitoring", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString(),
            ["probe_mode"] = target.ProbeMode.ToString()
        }
    };

    private AlertEvent BuildRecoveredEvent(MonitoringTarget target, PingProbeResult result) => new()
    {
        EventType = "monitoring.target_recovered",
        Source = "monitoring",
        Severity = AlertSeverity.Info,
        Title = $"{target.Name} is back online{_siteSuffix}",
        Message = $"Monitoring target {target.Name} ({target.Address}) recovered after {SuccessesToDeclareRecovered} consecutive successful probes. RTT {result.RttAvgMs:0.#} ms.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        MetricValue = result.RttAvgMs,
        SourceUrl = TargetSourceUrl(target, DateTime.UtcNow),
        Tags = ["monitoring", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString()
        }
    };

    private AlertEvent BuildSustainedLossEvent(MonitoringTarget target, double avgLossPercent) => new()
    {
        EventType = "monitoring.target_sustained_loss",
        Source = "monitoring",
        Severity = TargetSeverity(target.TargetType, isOffline: false),
        Title = $"{target.Name} packet loss{_siteSuffix}",
        Message = $"Monitoring target {target.Name} ({target.Address}) averaged {avgLossPercent:0.#}% packet loss over the last {LossWindowSize} probes.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        MetricValue = avgLossPercent,
        ThresholdValue = SustainedLossThresholdPercent,
        SourceUrl = TargetSourceUrl(target, DateTime.UtcNow),
        Tags = ["monitoring", "packet-loss", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString()
        }
    };

    /// <summary>
    /// Where the alert takes you: the Network Performance chart, on this target's own category
    /// and parked at the moment the alert fired, rather than the tab's default view of now. The
    /// analysis page reads all three from the link. WAN-scoped targets carry their WAN too, so a
    /// secondary WAN's alert does not open on the primary's chart.
    /// </summary>
    private static string TargetSourceUrl(MonitoringTarget target, DateTime firedAt)
    {
        var category = target.TargetType switch
        {
            MonitoringTargetType.Fabric => "Fabric",
            MonitoringTargetType.AccessIsp => "AccessIsp",
            MonitoringTargetType.Transit => "Transit",
            _ => "Custom"
        };
        var at = new DateTimeOffset(DateTime.SpecifyKind(firedAt, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
        var url = $"/monitoring?tab=performance&category={category}&at={at}";
        return string.IsNullOrEmpty(target.WanInterface)
            ? url
            : $"{url}&wan={Uri.EscapeDataString(target.WanInterface)}";
    }

    /// <summary>
    /// WAN/access-ISP/transit failures are user-impacting and rate as Critical. Fabric
    /// targets overlap with existing device-down detection, so Warning. Custom user
    /// targets default to Warning - the user opted in to monitor them, but we don't
    /// know how important they are.
    /// </summary>
    private static AlertSeverity TargetSeverity(MonitoringTargetType type, bool isOffline) => type switch
    {
        MonitoringTargetType.Wan => isOffline ? AlertSeverity.Critical : AlertSeverity.Warning,
        MonitoringTargetType.InternetService => isOffline ? AlertSeverity.Critical : AlertSeverity.Warning,
        MonitoringTargetType.AccessIsp => isOffline ? AlertSeverity.Critical : AlertSeverity.Warning,
        MonitoringTargetType.Transit => isOffline ? AlertSeverity.Warning : AlertSeverity.Info,
        MonitoringTargetType.Fabric => AlertSeverity.Warning,
        _ => AlertSeverity.Warning
    };

    private class TargetAlertState
    {
        public int ConsecutiveFailures;
        public int ConsecutiveSuccesses;
        public bool IsOffline;
        public bool IsLossy;

        /// <summary>Keeps the mid-transition explanation to one log line per outage, not one per probe.</summary>
        public bool TransitionSuppressionLogged;

        public Queue<double> LossWindow { get; } = new();
    }
}
