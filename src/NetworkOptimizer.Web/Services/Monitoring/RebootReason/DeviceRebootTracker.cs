using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// Watches device uptime, notices when a device has rebooted, and records why.
///
/// The rule is deliberately one rule rather than two: a device needs probing when there is no
/// stored reason for the boot it is currently running. A reboot satisfies that (new boot, nothing
/// stored yet), and so does a device that has simply never been probed, which is what backfills
/// the fleet on first run instead of waiting for every device to reboot once.
///
/// Boots are matched by their start instant with a tolerance, because uptime is sampled (SNMP or
/// the UniFi API) and the derived boot time jitters by a few seconds between polls.
/// </summary>
public class DeviceRebootTracker
{
    private readonly DeviceRebootProbe _probe;
    private readonly MonitoringInfluxClient _influx;
    private readonly DeviceRebootAlertEvaluator _alertEvaluator;
    private readonly ILogger<DeviceRebootTracker> _logger;

    /// <summary>Sampled uptime makes the derived boot instant wander; treat nearby values as one boot.</summary>
    private static readonly TimeSpan BootMatchTolerance = TimeSpan.FromMinutes(5);

    /// <summary>How far back to load existing records. Longer-running devices get re-probed.</summary>
    private static readonly TimeSpan HistoryLookback = TimeSpan.FromDays(365);

    /// <summary>Don't re-probe a device that just failed; SSH may be unconfigured or the box down.</summary>
    private static readonly TimeSpan ProbeRetryDelay = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, DeviceBootRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastProbeAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    private int _seeded;

    /// <summary>Creates the tracker for one site.</summary>
    public DeviceRebootTracker(
        DeviceRebootProbe probe,
        MonitoringInfluxClient influx,
        DeviceRebootAlertEvaluator alertEvaluator,
        ILogger<DeviceRebootTracker> logger)
    {
        _probe = probe;
        _influx = influx;
        _alertEvaluator = alertEvaluator;
        _logger = logger;
    }

    /// <summary>What is known about the boot a device is currently running.</summary>
    /// <param name="BootedAt">When the boot started.</param>
    /// <param name="Reason">The resolved reason, or null while it is still unknown.</param>
    /// <param name="FirmwareVersion">Firmware seen on this boot, used to spot an upgrade across the next one.</param>
    public record DeviceBootRecord(DateTime BootedAt, DeviceRebootReason? Reason, string? FirmwareVersion = null);

    /// <summary>
    /// The reason a device is running its current boot, or null when nothing is known yet.
    /// Served from memory so the dashboard costs nothing.
    /// </summary>
    public DeviceRebootReason? GetReason(string deviceMac)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return null;
        return _records.TryGetValue(Normalize(deviceMac), out var record) ? record.Reason : null;
    }

    /// <summary>When the device's current boot started, as last observed.</summary>
    public DateTime? GetBootedAt(string deviceMac)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return null;
        return _records.TryGetValue(Normalize(deviceMac), out var record) ? record.BootedAt : null;
    }

    /// <summary>
    /// Feed one uptime sample. Returns immediately; any probe runs in the background so the
    /// monitoring tier is never held up by SSH.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="deviceName">Device name, for logging.</param>
    /// <param name="deviceType">Device type; gateways are probed with the console credentials.</param>
    /// <param name="host">Device IP or hostname for SSH.</param>
    /// <param name="uptimeSeconds">Uptime reported by this sample.</param>
    /// <param name="firmwareVersion">Reported firmware, used to spot an upgrade across the boot.</param>
    /// <param name="observedAt">Sample time.</param>
    public void RecordUptimeSample(
        string deviceMac,
        string? deviceName,
        DeviceType deviceType,
        string? host,
        long? uptimeSeconds,
        string? firmwareVersion,
        DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceMac) || uptimeSeconds is null or <= 0)
            return;

        var mac = Normalize(deviceMac);
        var bootedAt = observedAt.ToUniversalTime().AddSeconds(-uptimeSeconds.Value);

        var known = _records.TryGetValue(mac, out var existing) ? existing : null;
        var sameBoot = known != null && WithinTolerance(known.BootedAt, bootedAt);

        if (sameBoot)
        {
            // Answered for this boot, nothing to do. An inconclusive answer (the UniFi Network
            // fallback saying only "restarted") does NOT count: it is displayed, but the probe
            // keeps retrying in case the device becomes reachable and can say what really happened.
            if (known!.Reason?.IsConclusive == true) return;
        }
        else if (known != null)
        {
            _logger.LogInformation(
                "Device {Device} ({Mac}) rebooted: uptime {Uptime}s puts boot at {BootedAt:u} (was {PreviousBoot:u})",
                deviceName ?? "unknown", mac, uptimeSeconds, bootedAt, known.BootedAt);
        }
        else
        {
            // First sighting: probing here is what backfills devices that have not rebooted
            // since the feature landed, rather than waiting for each one to restart.
            _logger.LogDebug(
                "First uptime sample for {Device} ({Mac}): up {Uptime}s, boot at {BootedAt:u}, no stored reason yet",
                deviceName ?? "unknown", mac, uptimeSeconds, bootedAt);
        }

        // A version change between the firmware we last recorded for this device and what the
        // UniFi device data reports now means the reboot was an upgrade - the signal that catches
        // switches, whose console ring shows an upgrade as an ordinary clean shutdown. It needs a
        // recorded baseline, which is why the firmware is persisted with the reboot record.
        var firmwareChanged = known != null && !sameBoot &&
            !string.IsNullOrWhiteSpace(firmwareVersion) &&
            !string.IsNullOrWhiteSpace(known.FirmwareVersion) &&
            !string.Equals(known.FirmwareVersion, firmwareVersion, StringComparison.OrdinalIgnoreCase);

        if (firmwareChanged)
        {
            _logger.LogInformation(
                "Device {Device} ({Mac}) changed firmware across the restart: {Old} -> {New}",
                deviceName ?? "unknown", mac, known!.FirmwareVersion, firmwareVersion);
        }

        if (!sameBoot)
        {
            // New boot: keep the boot instant, drop any reason belonging to the previous run.
            _records[mac] = new DeviceBootRecord(bootedAt, null, firmwareVersion);
            _lastProbeAttempt.TryRemove(mac, out _);
        }

        _ = ResolveInBackgroundAsync(mac, deviceName, deviceType, host, bootedAt, firmwareChanged);
    }

    /// <summary>
    /// Load existing reboot records so a server restart neither loses reasons nor re-probes
    /// every device on the site.
    /// </summary>
    public async Task SeedFromHistoryAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _seeded, 1) == 1) return;

        try
        {
            var stored = await _influx.QueryLatestDeviceRebootsAsync(HistoryLookback, ct);
            var staleRules = 0;
            foreach (var point in stored)
            {
                // Records classified by older rules are dropped so the device is probed again and
                // rewritten; otherwise a rules fix could never reach a boot already on file.
                if (point.ClassifierVersion < RebootClassifier.Version)
                {
                    staleRules++;
                    continue;
                }

                if (!Enum.TryParse<RebootCategory>(point.Category, ignoreCase: true, out var category))
                    category = RebootCategory.Unknown;
                if (!Enum.TryParse<RebootReasonSource>(point.Source, ignoreCase: true, out var source))
                    source = RebootReasonSource.UniFiEvent;

                var reason = new DeviceRebootReason(category, point.Summary, point.Detail, source);
                // Carrying the firmware back in is what lets a version change be spotted across a
                // server restart: without it every device looks like a first sighting and an
                // upgrade that happened while we were down goes unrecognised.
                _records[Normalize(point.DeviceMac)] =
                    new DeviceBootRecord(point.BootedAt, reason, point.FirmwareVersion);
            }

            _logger.LogDebug(
                "Seeded {Count} device reboot records from history ({Stale} written by older rules, queued for re-probe)",
                stored.Count - staleRules, staleRules);
        }
        catch (Exception ex)
        {
            // A cold or unreachable Influx just means the fleet gets probed again.
            _logger.LogDebug(ex, "Could not seed device reboot records from history");
            Interlocked.Exchange(ref _seeded, 0);
        }
    }

    /// <summary>
    /// Apply a UniFi Network event as the fallback reason for a device's current boot. Only used
    /// when the on-device probe found nothing, since these events are generic and often wrong.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="eventKey">UniFi event key, e.g. <c>EVT_SW_RestartedUnknown</c>.</param>
    /// <param name="adminName">Admin the console credited, when it named one.</param>
    public async Task ApplyUniFiEventFallbackAsync(string deviceMac, string? eventKey, string? adminName = null)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;

        var mac = Normalize(deviceMac);
        if (!_records.TryGetValue(mac, out var record) || record.Reason != null) return;

        var reason = RebootReasonParser.ParseUniFiEvent(eventKey, adminName);
        if (reason == null) return;

        await StoreAsync(mac, record.BootedAt, reason, DeviceType.Unknown);
    }

    private async Task ResolveInBackgroundAsync(
        string mac,
        string? deviceName,
        DeviceType deviceType,
        string? host,
        DateTime bootedAt,
        bool firmwareChanged)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogDebug(
                "Reboot reason for {Device} ({Mac}) not resolvable: the site reports no address to SSH to",
                deviceName ?? "unknown", mac);
            return;
        }

        // One probe per device at a time, and a cool-off after a miss. Devices whose SSH is not
        // set up land here on every health sample, so the skip is logged at Debug once per hour
        // rather than every 30 seconds.
        if (_lastProbeAttempt.TryGetValue(mac, out var last))
        {
            var since = DateTime.UtcNow - last;
            if (since < ProbeRetryDelay)
            {
                _logger.LogTrace(
                    "Reboot reason probe for {Device} ({Mac}) skipped: last attempt {Minutes:F0} min ago, retrying after {Retry}",
                    deviceName ?? "unknown", mac, since.TotalMinutes, ProbeRetryDelay);
                return;
            }
        }

        if (!_inFlight.TryAdd(mac, 0)) return;

        try
        {
            _lastProbeAttempt[mac] = DateTime.UtcNow;

            _logger.LogDebug(
                "Probing {Device} ({Mac}, {DeviceType}) at {Host} for the reason behind its boot at {BootedAt:u}",
                deviceName ?? "unknown", mac, deviceType, host, bootedAt);

            var reason = await _probe.ProbeAsync(host, deviceType, firmwareChanged);
            if (reason == null)
            {
                // Probe logs why (unreachable vs. reachable but no evidence); this line ties it
                // back to the device and says what the UI will show until the next attempt.
                _logger.LogDebug(
                    "No reboot reason established for {Device} ({Mac}); will retry in {Retry} and fall back to the UniFi Network event if one arrives",
                    deviceName ?? "unknown", mac, ProbeRetryDelay);
                return;
            }

            // The boot may have rolled over while the probe ran; only store against the boot we probed.
            if (!_records.TryGetValue(mac, out var current) || !WithinTolerance(current.BootedAt, bootedAt))
            {
                _logger.LogDebug(
                    "Discarding reboot reason for {Device} ({Mac}): the device rebooted again while probing",
                    deviceName ?? "unknown", mac);
                return;
            }

            await StoreAsync(mac, bootedAt, reason, deviceType, deviceName, host);

            _logger.LogInformation(
                "Reboot reason for {Device} ({Mac}): {Summary} [{Category}/{Source}] - {Detail}",
                deviceName ?? "unknown", mac, reason.Summary, reason.Category, reason.Source, reason.Detail);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reboot reason resolution failed for {Mac}", mac);
        }
        finally
        {
            _inFlight.TryRemove(mac, out _);
        }
    }

    private async Task StoreAsync(
        string mac,
        DateTime bootedAt,
        DeviceRebootReason reason,
        DeviceType deviceType,
        string? deviceName = null,
        string? deviceIp = null)
    {
        var firmware = _records.TryGetValue(mac, out var current) ? current.FirmwareVersion : null;
        _records[mac] = new DeviceBootRecord(bootedAt, reason, firmware);

        // Alerting only knows about reasons resolved live. Seeding from history writes the cache
        // directly and deliberately skips this, so a restart never re-alerts on every startup.
        try
        {
            await _alertEvaluator.EvaluateAsync(mac, deviceName, deviceIp, reason, bootedAt, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            // The reason is still recorded; only the notification was lost.
            _logger.LogWarning(ex, "Could not publish the reboot alert for {Mac}", mac);
        }

        _ = _influx.WriteDeviceRebootAsync(
            deviceMac: mac,
            deviceType: deviceType.ToString(),
            category: reason.Category.ToString(),
            summary: reason.Summary,
            detail: reason.Detail,
            source: reason.Source.ToString(),
            bootedAt: bootedAt,
            firmwareVersion: firmware,
            classifierVersion: RebootClassifier.Version);
    }

    private static bool WithinTolerance(DateTime a, DateTime b) =>
        (a - b).Duration() <= BootMatchTolerance;

    private static string Normalize(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
