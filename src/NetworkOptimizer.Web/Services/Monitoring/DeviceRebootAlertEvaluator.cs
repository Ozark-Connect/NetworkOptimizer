using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Monitoring.RebootReason;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Publishes an alert when a device's reboot reason is established.
///
/// Severity follows the reason, not the reboot: a commanded restart or a firmware upgrade is
/// <see cref="AlertSeverity.Info"/> because someone meant it, while a power loss, hang, panic or
/// watchdog reset is <see cref="AlertSeverity.Warning"/> because nobody did. The seeded rule sets
/// its minimum severity to Warning, so out of the box only the reboots nobody asked for notify;
/// lowering the rule to Info turns every restart into a notification.
///
/// Only current boots fire. Reasons are also resolved for devices that have been up for weeks
/// (that backfill is what populates the fleet without waiting for restarts), and alerting on those
/// would mean a burst of notifications about history on first run and after every server restart.
/// </summary>
public class DeviceRebootAlertEvaluator
{
    /// <summary>Event type the seeded alert rule matches.</summary>
    public const string RebootEventType = "device.rebooted";

    /// <summary>
    /// A boot older than this is history, not news: no alert. Wide enough that a reboot detected
    /// on the next poll cycle (and its SSH probe) still lands inside the window.
    /// </summary>
    public static readonly TimeSpan CurrentBootWindow = TimeSpan.FromMinutes(30);

    private readonly IAlertEventBus _eventBus;
    private readonly Firmware.RolloutSuppressionRegistry? _rolloutWindows;
    private readonly ILogger<DeviceRebootAlertEvaluator> _logger;
    private readonly string _siteSlug;
    private readonly string _siteSuffix;

    /// <param name="eventBus">Site-stamped alert bus.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="siteSlug">
    /// Site this instance evaluates for (one per site, owned by <see cref="MonitoringAlertRegistry"/>).
    /// Non-default sites get their slug appended to alert titles, and the rollout window lookup is
    /// keyed on it.
    /// </param>
    /// <param name="rolloutWindows">Devices inside their own firmware rollout window.</param>
    public DeviceRebootAlertEvaluator(
        IAlertEventBus eventBus,
        ILogger<DeviceRebootAlertEvaluator> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        Firmware.RolloutSuppressionRegistry? rolloutWindows = null)
    {
        _eventBus = eventBus;
        _rolloutWindows = rolloutWindows;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _siteSuffix = string.IsNullOrEmpty(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug
            ? "" : $" (site {siteSlug})";
    }

    /// <summary>
    /// Publish an alert for a freshly established reboot reason, if the boot is recent enough
    /// to be news.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="deviceName">Device display name.</param>
    /// <param name="deviceIp">Device IP, for the alert payload.</param>
    /// <param name="reason">The resolved reason.</param>
    /// <param name="bootedAt">When the boot started.</param>
    /// <param name="now">Current time, injectable for tests.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when an event was published.</returns>
    public async ValueTask<bool> EvaluateAsync(
        string deviceMac,
        string? deviceName,
        string? deviceIp,
        DeviceRebootReason reason,
        DateTime bootedAt,
        DateTime now,
        CancellationToken ct = default)
    {
        if (!reason.IsConclusive)
            return false;

        // A restart a rollout asked for is announced by the rollout, not here.
        if (_rolloutWindows?.IsInRolloutWindow(_siteSlug, deviceMac, now) == true)
        {
            _logger.LogDebug(
                "Not alerting on {Device} ({Mac}) restarting: a firmware rollout is upgrading it",
                deviceName ?? "unknown", deviceMac);
            return false;
        }

        var age = now.ToUniversalTime() - bootedAt.ToUniversalTime();
        if (age > CurrentBootWindow || age < -CurrentBootWindow)
        {
            _logger.LogDebug(
                "Not alerting on {Device} ({Mac}) reboot: boot was {Age} ago, outside the {Window} window",
                deviceName ?? "unknown", deviceMac, age, CurrentBootWindow);
            return false;
        }

        var severity = reason.IsUnexpected ? AlertSeverity.Warning : AlertSeverity.Info;
        var label = string.IsNullOrWhiteSpace(deviceName) ? deviceMac : deviceName;

        var message = string.IsNullOrWhiteSpace(reason.Detail)
            ? $"{label} restarted: {reason.Summary}."
            : $"{label} restarted: {reason.Summary}. {reason.Detail}.";

        await _eventBus.PublishAsync(new AlertEvent
        {
            EventType = RebootEventType,
            Source = "device",
            Severity = severity,
            Title = $"Device Restarted: {label}{_siteSuffix}",
            Message = message,
            DeviceId = deviceMac,
            DeviceName = deviceName,
            DeviceIp = deviceIp
        }, ct);

        _logger.LogInformation(
            "Published {Severity} reboot alert for {Device} ({Mac}): {Summary}",
            severity, deviceName ?? "unknown", deviceMac, reason.Summary);

        return true;
    }
}
