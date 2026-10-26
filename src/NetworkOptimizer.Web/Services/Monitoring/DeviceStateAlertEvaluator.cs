using System.Collections.Concurrent;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Publishes <c>device.offline</c> and <c>device.recovered</c> from what UniFi Network reports for
/// each device, as a paired offline/recovery cycle like the agent and monitoring-target alerts.
///
/// Crucially it stays silent for a restart that was asked for. UniFi distinguishes a device that
/// dropped from one that is upgrading, provisioning or being adopted - those map to
/// <see cref="DeviceStatusKind.Transitional"/> - so a firmware run does not read as an outage.
/// A device already announced offline that turns out to be upgrading is treated as recovered, since
/// the reason it stopped answering is now known and benign.
/// </summary>
public class DeviceStateAlertEvaluator
{
    /// <summary>Event type published when a device stops being reachable for reasons unknown.</summary>
    public const string OfflineEventType = "device.offline";

    /// <summary>Event type published when it comes back.</summary>
    public const string RecoveredEventType = "device.recovered";

    /// <summary>
    /// Consecutive offline observations before announcing it. UniFi can report a single offline
    /// sample during a config apply or a brief adoption blip, and the poll cadence means two
    /// samples still announce within about a minute.
    /// </summary>

    private const int OfflineObservationsToAnnounce = 2;

    private readonly IAlertEventBus _eventBus;
    private readonly DeviceTransitionTracker _transitions;
    private readonly DeviceOfflineDeduplicator _dedup;
    private readonly Firmware.RolloutSuppressionRegistry? _rolloutWindows;
    private readonly ILogger<DeviceStateAlertEvaluator> _logger;
    private readonly ConcurrentDictionary<string, DeviceAlertState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _siteSlug;
    private readonly string _siteSuffix;
    private readonly IServiceScopeFactory? _scopeFactory;
    // Devices whose device.offline was still open when this process started, by normalized MAC.
    private readonly HashSet<string> _openAtStart = new(StringComparer.Ordinal);
    private readonly object _seedLock = new();
    private Task? _seed;

    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="transitions">Which devices UniFi reports as mid-transition.</param>
    /// <param name="dedup">Shared de-dup tracker for device offline/recovered alerts.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">Site this instance evaluates for (one per site, owned by the registry).</param>
    /// <param name="rolloutWindows">
    /// Devices inside their own firmware rollout window. Separate from <paramref name="transitions"/>
    /// because UniFi flips a device to plain Offline partway through some upgrades, which is exactly
    /// the window where its own report cannot be relied on.
    /// </param>
    /// <param name="scopeFactory">
    /// Reads the site's open device.offline alerts once, so a device that went dark before a restart
    /// still gets its device.recovered. Without it, announced state starts empty.
    /// </param>
    public DeviceStateAlertEvaluator(
        IAlertEventBus eventBus,
        DeviceTransitionTracker transitions,
        DeviceOfflineDeduplicator dedup,
        ILogger<DeviceStateAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        Firmware.RolloutSuppressionRegistry? rolloutWindows = null,
        IServiceScopeFactory? scopeFactory = null)
    {
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _transitions = transitions;
        _dedup = dedup;
        _rolloutWindows = rolloutWindows;
        _logger = logger;
        _siteSlug = siteSlug ?? SiteManagementService.DefaultSiteSlug;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    private sealed class DeviceAlertState
    {
        public int ConsecutiveOffline;
        public bool Announced;
    }

    /// <summary>
    /// Feed one device's reported state.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="deviceName">Device name for the alert text.</param>
    /// <param name="deviceIp">Device address for the alert payload.</param>
    /// <param name="deviceType">Device type, for the alert context.</param>
    /// <param name="unifiState">The device's UniFi <c>state</c> value.</param>
    /// <param name="now">Current time.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask EvaluateAsync(
        string deviceMac,
        string? deviceName,
        string? deviceIp,
        DeviceType deviceType,
        int unifiState,
        DateTime now,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;

        await EnsureSeededAsync();
        var status = UniFiDeviceStateMap.ToStatus(unifiState);
        var state = _states.GetOrAdd(deviceMac, mac => new DeviceAlertState { Announced = _openAtStart.Contains(Normalize(mac)) });
        var label = string.IsNullOrWhiteSpace(deviceName) ? deviceMac : deviceName;

        // Upgrading, provisioning, adopting: someone or something asked for this. Never announce,
        // and close out an announcement already made - the silence now has a known cause.
        if (status.Kind == DeviceStatusKind.Transitional)
        {
            state.ConsecutiveOffline = 0;
            if (state.Announced)
            {
                state.Announced = false;
                await PublishRecoveredAsync(deviceMac, label, deviceIp, deviceType,
                    $"{label} is {status.Label.ToLowerInvariant()}, so its outage was an expected restart.", ct);
            }

            _logger.LogDebug(
                "Not alerting on {Device} ({Mac}): UniFi reports it {Status}, which is an initiated change",
                label, deviceMac, status.Label);
            return;
        }

        if (status.Kind == DeviceStatusKind.Offline)
        {
            // A transition observed moments ago still counts, because UniFi flips a device to
            // Offline partway through some upgrades before the new firmware checks back in.
            if (_transitions.IsInKnownTransition(_siteSlug, deviceMac, now))
            {
                state.ConsecutiveOffline = 0;
                _logger.LogDebug(
                    "Not alerting on {Device} ({Mac}) going offline: it was mid-transition moments ago",
                    label, deviceMac);
                return;
            }

            if (_rolloutWindows?.IsInRolloutWindow(_siteSlug, deviceMac, now) == true)
            {
                state.ConsecutiveOffline = 0;
                _logger.LogDebug(
                    "Not alerting on {Device} ({Mac}) going offline: a firmware rollout is upgrading it or a device between it and the console",
                    label, deviceMac);
                return;
            }

            state.ConsecutiveOffline++;
            if (state.Announced || state.ConsecutiveOffline < OfflineObservationsToAnnounce)
                return;

            state.Announced = true;

            if (!_dedup.TryClaimSlot(deviceMac, isRecovery: false, DateTime.UtcNow))
            {
                _logger.LogDebug(
                    "Suppressing device.offline for {Device} ({Mac}): monitoring target_offline already fired",
                    label, deviceMac);
                return;
            }

            await _eventBus.PublishAsync(new AlertEvent
            {
                EventType = OfflineEventType,
                Source = "device",
                Severity = AlertSeverity.Error,
                Title = $"Device Offline: {label}{_siteSuffix}",
                Message = $"{label} stopped reporting to UniFi Network and is not upgrading or provisioning.",
                DeviceId = deviceMac,
                DeviceName = deviceName,
                DeviceIp = deviceIp,
                SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
                Context = new Dictionary<string, string>
                {
                    ["device_type"] = deviceType.ToString(),
                    ["unifi_state"] = unifiState.ToString()
                }
            }, ct);

            _logger.LogInformation("Published device.offline for {Device} ({Mac})", label, deviceMac);
            return;
        }

        // Online (or any other non-offline, non-transitional state): clear and close the cycle.
        state.ConsecutiveOffline = 0;
        if (state.Announced)
        {
            state.Announced = false;
            await PublishRecoveredAsync(deviceMac, label, deviceIp, deviceType,
                $"{label} is reporting to UniFi Network again.", ct);
        }
    }

    private Task EnsureSeededAsync()
    {
        if (_scopeFactory == null) return Task.CompletedTask;
        lock (_seedLock) return _seed ??= SeedAsync();
    }

    /// <summary>
    /// Marks each device with an unresolved device.offline and no device.recovered after it as
    /// announced, so its recovery is published and a still-dark device is not announced twice.
    /// </summary>
    private async Task SeedAsync()
    {
        try
        {
            using var scope = _scopeFactory!.CreateScope();
            scope.ServiceProvider.GetRequiredService<IAlertSiteScope>().UseSite(_siteSlug);
            var alerts = await scope.ServiceProvider.GetRequiredService<IAlertRepository>().GetUnresolvedAlertsAsync();
            var recoveredAt = alerts
                .Where(a => a.EventType == RecoveredEventType && !string.IsNullOrWhiteSpace(a.DeviceId))
                .GroupBy(a => Normalize(a.DeviceId!))
                .ToDictionary(g => g.Key, g => g.Max(a => a.TriggeredAt));
            foreach (var offline in alerts.Where(a => a.EventType == OfflineEventType && !string.IsNullOrWhiteSpace(a.DeviceId)))
            {
                var mac = Normalize(offline.DeviceId!);
                if (!recoveredAt.TryGetValue(mac, out var at) || at < offline.TriggeredAt)
                    _openAtStart.Add(mac);
            }
            if (_openAtStart.Count > 0)
                _logger.LogInformation("{Count} device(s) were announced offline before start-up{Site}; their recovery will be published",
                    _openAtStart.Count, _siteSuffix);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read open device offline alerts{Site}; devices offline before start-up will not recover", _siteSuffix);
        }
    }

    private static string Normalize(string mac) => mac.Replace(":", "").Replace("-", "").ToLowerInvariant();

    private async ValueTask PublishRecoveredAsync(
        string deviceMac, string label, string? deviceIp, DeviceType deviceType, string message, CancellationToken ct)
    {
        if (!_dedup.TryClaimSlot(deviceMac, isRecovery: true, DateTime.UtcNow))
        {
            _logger.LogDebug(
                "Suppressing device.recovered for {Device} ({Mac}): monitoring target_recovered already fired",
                label, deviceMac);
            return;
        }

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RecoveredEventType,
            Source = "device",
            Severity = AlertSeverity.Info,
            Title = $"Device Recovered: {label}{_siteSuffix}",
            Message = message,
            DeviceId = deviceMac,
            DeviceName = label,
            DeviceIp = deviceIp,
            SourceUrl = MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.NowMs()),
            Context = new Dictionary<string, string> { ["device_type"] = deviceType.ToString() }
        }, ct);

        _logger.LogInformation("Published device.recovered for {Device} ({Mac})", label, deviceMac);
    }
}
