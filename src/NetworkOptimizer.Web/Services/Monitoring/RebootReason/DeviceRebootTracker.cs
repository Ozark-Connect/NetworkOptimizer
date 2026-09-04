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

    /// <summary>
    /// Timestamp each device's stored record already occupies, including records dropped as stale.
    ///
    /// A record is one point per boot, keyed by the boot instant - but that instant is derived from
    /// sampled uptime and wanders a few seconds between polls. Writing a re-probe at a freshly
    /// derived instant therefore lands BESIDE the old point instead of replacing it, and since the
    /// read takes the latest _time (the latest boot instant, not the latest write), the older row can
    /// keep winning: the device then looks stale on every startup and re-probes forever. Reusing the
    /// timestamp already on file makes the rewrite an overwrite.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTime> _storedBootAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _lastProbeAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recent UniFi Network restart events, keyed by normalized MAC. When a device crashes
    /// during a commanded restart, the SSH probe sees an unclean shutdown and classifies it
    /// as unexpected. Checking this lets the probe result be corrected when UniFi Network
    /// says the restart was initiated, not spontaneous.
    /// </summary>
    private readonly ConcurrentDictionary<string, CommandedRestartEvent> _commandedRestarts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A restart event must sit within this window of the boot time to override an unexpected
    /// probe result.
    /// </summary>
    internal static readonly TimeSpan CommandedRestartWindow = TimeSpan.FromMinutes(2);

    private record CommandedRestartEvent(string EventKey, string? AdminName, DateTime ReceivedAt);

    /// <summary>
    /// Caps concurrent probes for this site. Uptime samples arrive for every device in one pass, so
    /// a first run - or any classifier bump, which re-probes the fleet - would otherwise open an SSH
    /// session to every device at once, frequently through a single agent tunnel. Probes are rare
    /// (once per boot per device) so a small gate costs nothing; queued probes still hold their
    /// per-device _inFlight claim, so nothing double-probes while waiting. Mirrors the SNMP tier's
    /// gate, and is per site so one busy site cannot starve another.
    /// </summary>
    private readonly SemaphoreSlim _probeGate = new(4);

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

    /// <summary>A device seen on a boot it was not on before.</summary>
    /// <param name="DeviceMac">The device's MAC.</param>
    /// <param name="DeviceName">Its name, when the sample carried one.</param>
    /// <param name="DeviceType">What kind of device it is.</param>
    /// <param name="Host">Its address, when the sample carried one.</param>
    /// <param name="BootedAt">When the new boot started.</param>
    public sealed record DeviceBootEvent(
        string DeviceMac, string? DeviceName, DeviceType DeviceType, string? Host, DateTime BootedAt);

    /// <summary>
    /// Raised once per device per new boot, for anything that has work to do when a device comes
    /// back - the AP Agent lives in tmpfs, so a reboot is what makes it need redeploying.
    ///
    /// Not raised on a first sighting: the server cannot tell a device that just rebooted from one
    /// it has simply never seen, and treating a restart of this server as a fleet-wide reboot would
    /// be exactly wrong. Subscribers must therefore carry their own periodic check rather than
    /// treating this as complete.
    /// </summary>
    public event Action<DeviceBootEvent>? DeviceRebooted;

    /// <summary>
    /// The reason behind the boot a device is reporting right now, or null while that boot has no
    /// reason yet. Served from memory so the dashboard costs nothing.
    ///
    /// It takes the reported uptime rather than answering from the MAC alone, deliberately. A
    /// record is only as fresh as the last uptime sample the tracker was fed, while a caller
    /// showing live uptime is reading the console directly - so a device that restarted since that
    /// sample would be handed the reason for its PREVIOUS run, which is how an AP that had been
    /// power cycled came to be labeled with a firmware upgrade from days earlier. Holding the
    /// reason back until the boot instants line up leaves the tooltip empty for as long as the new
    /// reason takes to resolve, which is the honest answer.
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="uptimeSeconds">Uptime the caller is displaying for the device.</param>
    /// <param name="observedAt">When that uptime was read.</param>
    public DeviceRebootReason? GetReasonForReportedUptime(string deviceMac, long? uptimeSeconds, DateTime observedAt)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return null;
        if (!_records.TryGetValue(Normalize(deviceMac), out var record) || record.Reason == null) return null;

        // Nothing to check against - an offline device reports no uptime - so the record stands.
        // That is an absence of evidence, not evidence of a restart we missed.
        if (uptimeSeconds is null or <= 0) return record.Reason;

        // Only a boot LATER than the record's is a restart the tracker has yet to account for. An
        // earlier one means the two uptime sources disagree (the monitoring tiers read
        // system-stats.uptime, the console the device's own field), which is not news and must not
        // silence a perfectly good reason.
        var reportedBootAt = observedAt.ToUniversalTime().AddSeconds(-uptimeSeconds.Value);
        return reportedBootAt - record.BootedAt > BootMatchTolerance ? null : record.Reason;
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
    /// <summary>
    /// Where an uptime sample came from. The monitoring tiers read system-stats.uptime; the console
    /// observer reads the device's own uptime field. The two do not always agree, so only one may
    /// speak for a site at a time - see <see cref="MonitoringIsFeeding"/>.
    /// </summary>
    public enum UptimeSource
    {
        /// <summary>A monitoring collection tier. The default, so existing callers are unchanged.</summary>
        Monitoring,

        /// <summary>DeviceRebootObserver, which only speaks when monitoring is not.</summary>
        Console,
    }

    private readonly ConcurrentDictionary<string, DateTime> _lastMonitoringSampleByMac = new();

    /// <summary>
    /// True when a monitoring tier has fed THIS DEVICE recently, which means the console observer
    /// must keep quiet about it: the two read different uptime fields, and letting both write would
    /// have the tracker see a new boot every time the source alternated.
    ///
    /// Per device rather than per site, because the tiers feed per device and skip the ones that
    /// answer no health data. A site-wide flag would silence the observer for exactly those devices
    /// - the ones nothing else is reporting - which is the case it exists to cover.
    /// </summary>
    public bool MonitoringIsFeeding(string deviceMac, TimeSpan within)
        => !string.IsNullOrWhiteSpace(deviceMac)
            && _lastMonitoringSampleByMac.TryGetValue(Normalize(deviceMac), out var last)
            && DateTime.UtcNow - last < within;

    public void RecordUptimeSample(
        string deviceMac,
        string? deviceName,
        DeviceType deviceType,
        string? host,
        long? uptimeSeconds,
        string? firmwareVersion,
        DateTime observedAt,
        UptimeSource source = UptimeSource.Monitoring,
        string? model = null)
    {
        if (string.IsNullOrWhiteSpace(deviceMac) || uptimeSeconds is null or <= 0)
            return;

        if (source == UptimeSource.Monitoring)
            _lastMonitoringSampleByMac[Normalize(deviceMac)] = DateTime.UtcNow;

        var mac = Normalize(deviceMac);
        var bootedAt = observedAt.ToUniversalTime().AddSeconds(-uptimeSeconds.Value);

        var known = _records.TryGetValue(mac, out var existing) ? existing : null;

        // A version change between the firmware we last recorded for this device and what the
        // UniFi device data reports now means the reboot was an upgrade - the signal that catches
        // switches, whose console ring shows an upgrade as an ordinary clean shutdown. It needs a
        // recorded baseline, which is why the firmware is persisted with the reboot record.
        var firmwareChanged = known != null &&
            RebootReasonParser.NamesADifferentImage(known.FirmwareVersion, firmwareVersion);

        var sameBoot = known != null && IsSameBoot(known.BootedAt, known.FirmwareVersion, bootedAt, firmwareVersion);

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

            RaiseRebooted(new DeviceBootEvent(mac, deviceName, deviceType, host, bootedAt));
        }
        else
        {
            // First sighting: probing here is what backfills devices that have not rebooted
            // since the feature landed, rather than waiting for each one to restart.
            _logger.LogDebug(
                "First uptime sample for {Device} ({Mac}): up {Uptime}s, boot at {BootedAt:u}, no stored reason yet",
                deviceName ?? "unknown", mac, uptimeSeconds, bootedAt);
        }

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

            // Release the previous boot's point. Reusing it is only ever right for a re-probe of
            // the same boot; on a new one it would overwrite the record the previous boot left.
            _storedBootAt.TryRemove(mac, out _);
        }

        _ = ResolveInBackgroundAsync(mac, deviceName, deviceType, host, bootedAt, firmwareChanged,
            previousFirmware: known?.FirmwareVersion, currentFirmware: firmwareVersion,
            model: model);
    }

    /// <summary>
    /// Notifies subscribers on the calling thread. A subscriber that throws must not take the
    /// uptime sample down with it, so the whole fan-out is contained.
    /// </summary>
    private void RaiseRebooted(DeviceBootEvent boot)
    {
        try
        {
            DeviceRebooted?.Invoke(boot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A reboot subscriber threw for {Mac}", boot.DeviceMac);
        }
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
                // Remember where this device's point sits even when its rules are stale, so the
                // re-probe overwrites that point rather than adding a sibling next to it.
                _storedBootAt[Normalize(point.DeviceMac)] = point.BootedAt;

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
    /// Record that UniFi Network issued a restart event for a device. Called as events arrive,
    /// before the SSH probe resolves. Two roles:
    /// <list type="number">
    /// <item>Fallback: if the probe finds nothing, the event becomes the reason.</item>
    /// <item>Override: if the probe classifies the boot as unexpected but a commanded restart
    /// event sits near the boot time, the classification is corrected to CommandedReboot. This
    /// catches devices that crash during a commanded shutdown sequence (the driver goes wrong,
    /// pstore shows no clean shutdown marker, but the restart was initiated).</item>
    /// </list>
    /// </summary>
    /// <param name="deviceMac">Device MAC.</param>
    /// <param name="eventKey">UniFi event key, e.g. <c>EVT_SW_RestartedUnknown</c>.</param>
    /// <param name="adminName">Admin the console credited, when it named one.</param>
    public Task ApplyUniFiEventFallbackAsync(string deviceMac, string? eventKey, string? adminName = null) =>
        ApplyUniFiEventFallbackAsync(deviceMac, eventKey, adminName, DateTime.UtcNow);

    internal async Task ApplyUniFiEventFallbackAsync(string deviceMac, string? eventKey, string? adminName, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return;

        var mac = Normalize(deviceMac);
        var parsedEvent = RebootReasonParser.ParseUniFiEvent(eventKey, adminName);
        if (parsedEvent == null) return;

        // Always record the event so the probe can check it when it resolves later.
        if (parsedEvent.Category == RebootCategory.CommandedReboot)
            _commandedRestarts[mac] = new CommandedRestartEvent(eventKey!, adminName, now);

        if (!_records.TryGetValue(mac, out var record)) return;

        // Role 1: fallback when probe has not resolved yet.
        if (record.Reason == null)
        {
            await StoreAsync(mac, record.BootedAt, parsedEvent, DeviceType.Unknown);
            return;
        }

        // Role 2: override an unexpected probe result with a commanded restart, but only
        // when the boot it describes is recent. Without this check, an AP that crashed days
        // ago then gets a routine restart command today would have its old crash record
        // rewritten - the event explains the next boot, not the recorded one.
        if (record.Reason.IsUnexpected && parsedEvent.Category == RebootCategory.CommandedReboot
            && (now - record.BootedAt).Duration() <= CommandedRestartWindow)
        {
            var overridden = OverrideWithCommandedRestart(record.Reason, adminName);
            _logger.LogInformation(
                "Overriding {OldCategory} with {NewCategory} for {Mac}: UniFi Network says the restart was commanded",
                record.Reason.Category, overridden.Category, mac);
            await StoreAsync(mac, record.BootedAt, overridden, DeviceType.Unknown);
        }
    }

    /// <summary>
    /// Check whether a recently recorded UniFi restart event explains this boot, and if so
    /// override an unexpected probe result with CommandedReboot. Called from the probe path
    /// after it resolves.
    /// </summary>
    private DeviceRebootReason? TryOverrideFromCommandedRestart(
        string mac, DeviceRebootReason probeResult, DateTime bootedAt)
    {
        if (!probeResult.IsUnexpected) return null;
        if (!_commandedRestarts.TryGetValue(mac, out var evt)) return null;

        if ((evt.ReceivedAt - bootedAt).Duration() > CommandedRestartWindow)
            return null;

        _commandedRestarts.TryRemove(mac, out _);
        return OverrideWithCommandedRestart(probeResult, evt.AdminName);
    }

    private static DeviceRebootReason OverrideWithCommandedRestart(
        DeviceRebootReason original, string? adminName)
    {
        var by = string.IsNullOrWhiteSpace(adminName)
            ? "Restarted via UniFi Network"
            : $"Restarted by {adminName}";
        return new DeviceRebootReason(
            RebootCategory.CommandedReboot,
            "Restarted",
            $"{by} (shutdown was not clean: {original.Summary.ToLowerInvariant()})",
            RebootReasonSource.UniFiEvent);
    }

    private async Task ResolveInBackgroundAsync(
        string mac,
        string? deviceName,
        DeviceType deviceType,
        string? host,
        DateTime bootedAt,
        bool firmwareChanged,
        string? previousFirmware,
        string? currentFirmware,
        string? model = null)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogDebug(
                "Reboot reason for {Device} ({Mac}) not resolvable: the site reports no address to SSH to",
                deviceName ?? "unknown", mac);
            return;
        }

        if (!UniFi.UniFiProductDatabase.HasSsh(model, null))
            return;

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

        var gateHeld = false;
        try
        {
            // Acquired inside the try so the per-device claim is always released, but tracked so a
            // failed wait never releases a permit this call does not hold.
            await _probeGate.WaitAsync();
            gateHeld = true;

            _lastProbeAttempt[mac] = DateTime.UtcNow;

            _logger.LogDebug(
                "Probing {Device} ({Mac}, {DeviceType}) at {Host} for the reason behind its boot at {BootedAt:u}",
                deviceName ?? "unknown", mac, deviceType, host, bootedAt);

            var probed = await _probe.ProbeAsync(host, deviceType, firmwareChanged);

            // Name the versions from the UniFi device data. An AP's console ring proves a flash
            // happened but never says which image, and that detail is what the tooltip shows.
            var reason = probed == null
                ? null
                : RebootReasonParser.WithFirmwareVersions(probed, previousFirmware, currentFirmware);

            if (reason == null)
            {
                _logger.LogDebug(
                    "No reboot reason established for {Device} ({Mac}); will retry in {Retry} and fall back to the UniFi Network event if one arrives",
                    deviceName ?? "unknown", mac, ProbeRetryDelay);
                return;
            }

            // A device that crashed during a commanded restart (the driver went wrong during
            // shutdown) reads as unexpected from pstore alone. If UniFi Network says it was
            // told to restart, the classification is corrected.
            var overridden = TryOverrideFromCommandedRestart(mac, reason, bootedAt);
            if (overridden != null)
            {
                _logger.LogInformation(
                    "Overriding {OldCategory} with CommandedReboot for {Device} ({Mac}): UniFi Network event within window",
                    reason.Category, deviceName ?? "unknown", mac);
                reason = overridden;
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
            if (gateHeld) _probeGate.Release();
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

        // Reuse the point already on file for this boot so the write replaces it instead of
        // creating a second point a few seconds away; otherwise the read can keep returning the
        // older row and the device re-probes on every startup.
        var writeAt = _storedBootAt.TryGetValue(mac, out var stored) && WithinTolerance(stored, bootedAt)
            ? stored
            : bootedAt;
        _storedBootAt[mac] = writeAt;

        _ = _influx.WriteDeviceRebootAsync(
            deviceMac: mac,
            deviceType: deviceType.ToString(),
            category: reason.Category.ToString(),
            summary: reason.Summary,
            detail: reason.Detail,
            source: reason.Source.ToString(),
            bootedAt: writeAt,
            firmwareVersion: firmware,
            classifierVersion: RebootClassifier.Version);
    }

    /// <summary>
    /// Whether a sampled boot instant still describes the boot on record.
    ///
    /// A device cannot swap images without restarting, so a firmware change is proof of a boot the
    /// tolerance would otherwise absorb. Never drop that half: a reflash pair - a downgrade and the
    /// roll-forward behind it - lands minutes apart, and merging the two left the second change
    /// with no record, no chart mark and no reason on the tooltip.
    /// </summary>
    /// <param name="recordedBootAt">Boot instant the record holds.</param>
    /// <param name="recordedFirmware">Firmware the record holds, if any.</param>
    /// <param name="sampledBootAt">Boot instant this sample derives.</param>
    /// <param name="sampledFirmware">Firmware this sample reports, if any.</param>
    internal static bool IsSameBoot(
        DateTime recordedBootAt, string? recordedFirmware,
        DateTime sampledBootAt, string? sampledFirmware) =>
        WithinTolerance(recordedBootAt, sampledBootAt) &&
        !RebootReasonParser.NamesADifferentImage(recordedFirmware, sampledFirmware);

    private static bool WithinTolerance(DateTime a, DateTime b) =>
        (a - b).Duration() <= BootMatchTolerance;

    private static string Normalize(string mac) =>
        mac.Replace(":", "").Replace("-", "").ToLowerInvariant();
}
