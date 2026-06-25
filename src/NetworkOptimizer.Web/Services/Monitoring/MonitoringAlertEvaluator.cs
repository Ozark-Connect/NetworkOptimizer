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
/// </summary>
public class MonitoringAlertEvaluator
{
    private const int FailuresToDeclareOffline = 3;
    private const int SuccessesToDeclareRecovered = 3;
    private const double SustainedLossThresholdPercent = 30.0;
    private const int LossWindowSize = 5;

    private readonly IAlertEventBus _eventBus;
    private readonly ILogger<MonitoringAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, TargetAlertState> _states = new();

    public MonitoringAlertEvaluator(IAlertEventBus eventBus, ILogger<MonitoringAlertEvaluator> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
    }

    public async ValueTask EvaluateAsync(MonitoringTarget target, PingProbeResult result, CancellationToken ct = default)
    {
        var state = _states.GetOrAdd(target.TargetId, _ => new TargetAlertState());

        if (result.Success)
        {
            state.ConsecutiveFailures = 0;
            state.ConsecutiveSuccesses++;
            state.LossWindow.Enqueue(result.LossPercent);
            while (state.LossWindow.Count > LossWindowSize) state.LossWindow.Dequeue();

            if (state.IsOffline && state.ConsecutiveSuccesses >= SuccessesToDeclareRecovered)
            {
                state.IsOffline = false;
                await _eventBus.PublishAsync(BuildRecoveredEvent(target, result), ct);
            }

            // Sustained-loss detection only matters while the target is nominally up.
            var avgLoss = state.LossWindow.Count > 0 ? state.LossWindow.Average() : 0;
            if (!state.IsOffline && state.LossWindow.Count >= LossWindowSize)
            {
                if (!state.IsLossy && avgLoss >= SustainedLossThresholdPercent)
                {
                    state.IsLossy = true;
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
                state.IsOffline = true;
                state.IsLossy = false; // offline supersedes lossy
                state.LossWindow.Clear();
                await _eventBus.PublishAsync(BuildOfflineEvent(target), ct);
            }
        }
    }

    private static AlertEvent BuildOfflineEvent(MonitoringTarget target) => new()
    {
        EventType = "monitoring.target_offline",
        Source = "monitoring",
        Severity = TargetSeverity(target.TargetType, isOffline: true),
        Title = $"{target.Name} is offline",
        Message = $"Monitoring target {target.Name} ({target.Address}) failed {FailuresToDeclareOffline} consecutive {target.ProbeMode.ToString().ToUpperInvariant()} probes.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        SourceUrl = "/monitoring?tab=performance",
        Tags = ["monitoring", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString(),
            ["probe_mode"] = target.ProbeMode.ToString()
        }
    };

    private static AlertEvent BuildRecoveredEvent(MonitoringTarget target, PingProbeResult result) => new()
    {
        EventType = "monitoring.target_recovered",
        Source = "monitoring",
        Severity = AlertSeverity.Info,
        Title = $"{target.Name} is back online",
        Message = $"Monitoring target {target.Name} ({target.Address}) recovered after {SuccessesToDeclareRecovered} consecutive successful probes. RTT {result.RttAvgMs:0.#} ms.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        MetricValue = result.RttAvgMs,
        SourceUrl = "/monitoring?tab=performance",
        Tags = ["monitoring", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString()
        }
    };

    private static AlertEvent BuildSustainedLossEvent(MonitoringTarget target, double avgLossPercent) => new()
    {
        EventType = "monitoring.target_sustained_loss",
        Source = "monitoring",
        Severity = TargetSeverity(target.TargetType, isOffline: false),
        Title = $"{target.Name} packet loss",
        Message = $"Monitoring target {target.Name} ({target.Address}) averaged {avgLossPercent:0.#}% packet loss over the last {LossWindowSize} probes.",
        DeviceId = target.DeviceMac,
        DeviceName = target.Name,
        DeviceIp = target.Address,
        MetricValue = avgLossPercent,
        ThresholdValue = SustainedLossThresholdPercent,
        SourceUrl = "/monitoring?tab=performance",
        Tags = ["monitoring", "packet-loss", target.TargetType.ToString().ToLowerInvariant()],
        Context = new Dictionary<string, string>
        {
            ["target_id"] = target.TargetId,
            ["target_type"] = target.TargetType.ToString()
        }
    };

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
        public Queue<double> LossWindow { get; } = new();
    }
}
