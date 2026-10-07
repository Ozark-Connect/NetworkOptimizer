using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// Reads the reboot evidence a UniFi device keeps across a restart, over SSH, in one
/// read-only round trip.
///
/// The device itself is the only place the real reason lives. UniFi Network's event log knows
/// only "restarted" or "restarted for unknown reason", which lumps power loss, watchdog resets,
/// panics and SoC hangs together, so it is used only as a fallback when SSH gives nothing.
/// </summary>
public class DeviceRebootProbe
{
    private readonly DeviceSshRouter _ssh;
    private readonly ILogger<DeviceRebootProbe> _logger;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private const string PstoreMarker = "###PSTORE";
    private const string ConsoleMarker = "###CONSOLE";
    private const string CrashMarker = "###CRASH";
    private const string RebootLogMarker = "###REBOOTLOG";
    private const string RebootLogAgeMarker = "###REBOOTLOGAGE";
    private const string UpgradeMarker = "###UPGRADE";
    private const string UpgradeAgeMarker = "###UPGRADEAGE";
    private const string ConsoleRingMarker = "###CONSOLERING";
    private const string CrashAgeMarker = "###CRASHAGE";
    private const string DeviceClockMarker = "###DEVICECLOCK";

    /// <summary>
    /// How far the device's clock may sit from the server's before the reason log's age, which is
    /// measured on the device clock, stops being trusted.
    /// </summary>
    private const int MaxClockSkewSeconds = 600;

    /// <summary>
    /// One shell line per evidence source, each behind a marker so the reply can be split.
    /// Everything is a read: a listing, three tails and one file test. Nothing is written,
    /// no daemon is touched, and a missing path just yields an empty section.
    /// </summary>
    /// The markers MUST stay single-quoted: unquoted, the shell reads the leading '#' as the start
    /// of a comment and discards the whole rest of the line, which is every command after it.
    private static readonly string ProbeCommand = string.Join("; ",
        $"echo '{PstoreMarker}'",
        "ls /sys/fs/pstore/ 2>/dev/null",
        $"echo '{ConsoleMarker}'",
        "tail -n 40 /sys/fs/pstore/console-ramoops-0 2>/dev/null",
        $"echo '{CrashMarker}'",
        "head -n 12 /sys/fs/pstore/dmesg-ramoops-0 2>/dev/null",
        // Crash dumps outlive the boot that produced them, so date the newest one against this
        // boot. These files carry real mtimes even though the console ring's is epoch 0.
        $"echo '{CrashAgeMarker}'",
        "D=$(ls /sys/fs/pstore/dmesg-ramoops-* 2>/dev/null | head -n 1); " +
        "M=$(stat -c %Y \"$D\" 2>/dev/null); U=$(awk '{print int($1)}' /proc/uptime 2>/dev/null); " +
        "N=$(date +%s 2>/dev/null); " +
        "if [ -n \"$M\" ] && [ -n \"$U\" ] && [ -n \"$N\" ]; then echo $((M - N + U)); fi",
        $"echo '{RebootLogMarker}'",
        "tail -n 2 /var/log/reboot-time.log 2>/dev/null",
        // The console writes this boot's entry minutes after the kernel is up, so a probe can
        // arrive while the last line still describes the PREVIOUS boot. Date the file against this
        // boot and let the parser decide, same shape as the markers above.
        $"echo '{RebootLogAgeMarker}'",
        "M=$(stat -c %Y /var/log/reboot-time.log 2>/dev/null); " +
        "U=$(awk '{print int($1)}' /proc/uptime 2>/dev/null); N=$(date +%s 2>/dev/null); " +
        "if [ -n \"$M\" ] && [ -n \"$U\" ] && [ -n \"$N\" ]; then echo $((M - N + U)); fi",
        // Whether this platform writes a console ring at all. Without it an empty pstore is
        // meaningless; with it, an empty pstore means the RAM was lost, i.e. power was removed.
        $"echo '{ConsoleRingMarker}'",
        "grep -o 'ramoops.console_size=[^ ]*' /proc/cmdline 2>/dev/null",
        $"echo '{UpgradeMarker}'",
        "cat /etc/persistent/post_upgrade_pending 2>/dev/null",
        // The marker persists, so its contents alone cannot say which boot it explains. Report its
        // mtime relative to this boot's start and let the parser decide: seconds = mtime - bootTime,
        // normally a small negative number because the file is written just before the reboot.
        $"echo '{UpgradeAgeMarker}'",
        "M=$(stat -c %Y /etc/persistent/post_upgrade_pending 2>/dev/null); " +
        "U=$(awk '{print int($1)}' /proc/uptime 2>/dev/null); N=$(date +%s 2>/dev/null); " +
        "if [ -n \"$M\" ] && [ -n \"$U\" ] && [ -n \"$N\" ]; then echo $((M - N + U)); fi",
        // The device's clock, so the server can tell when the ages above were measured on a wrong one.
        $"echo '{DeviceClockMarker}'",
        "date +%s 2>/dev/null",
        // Absent paths make the last command exit non-zero, which the SSH layer reports as a
        // failed run even though the probe output is right there. Land on a success either way.
        "true");

    /// <summary>Creates the probe.</summary>
    public DeviceRebootProbe(
        DeviceSshRouter ssh,
        ILogger<DeviceRebootProbe> logger)
    {
        _ssh = ssh;
        _logger = logger;
    }

    /// <summary>
    /// Probe one device for why its previous run ended.
    /// </summary>
    /// <param name="deviceMac">The device's MAC, which the SSH router keys its credential route on.</param>
    /// <param name="host">Device IP or hostname to SSH to.</param>
    /// <param name="deviceType">The device's role. <see cref="DeviceSshRouter"/> picks the credentials from it.</param>
    /// <param name="firmwareChanged">Whether the reported firmware version changed across this boot.</param>
    /// <param name="firmwareKnownUnchanged">Whether a firmware version was recorded before this boot and
    /// still names the same image. Distinct from <paramref name="firmwareChanged"/> being false, which
    /// also covers having no earlier version to compare.</param>
    /// <param name="trustDeviceClock">Date the reason log on the device's clock even when it disagrees with
    /// the server's: past the provisional window a clock that never syncs gets the same answer as before.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The reason, or null when SSH produced nothing usable (not configured, unreachable, or a
    /// platform that keeps no pstore). Callers fall back to the UniFi Network event in that case.
    /// </returns>
    public async Task<DeviceRebootReason?> ProbeAsync(
        string deviceMac,
        string host,
        DeviceType deviceType,
        bool firmwareChanged,
        bool firmwareKnownUnchanged = false,
        bool trustDeviceClock = false,
        CancellationToken cancellationToken = default)
    {
        var credentialSet = deviceType == DeviceType.Gateway ? "console" : "shared device";

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogDebug("Reboot reason probe skipped: no host address for a {DeviceType}", deviceType);
            return null;
        }

        var (success, output) = await RunProbeAsync(deviceMac, host, deviceType, cancellationToken);

        if (!success)
        {
            // The usual cause is SSH not being set up (or device SSH being off in UniFi Network).
            // Surface whatever the SSH layer said, since that is the actionable part.
            _logger.LogDebug(
                "Reboot reason probe could not reach {Host} ({DeviceType}) using the {CredentialSet} credentials: {Error}",
                host, deviceType, credentialSet, Summarize(output));
            return null;
        }

        if (string.IsNullOrWhiteSpace(output))
        {
            _logger.LogDebug(
                "Reboot reason probe connected to {Host} ({DeviceType}) but the probe returned nothing",
                host, deviceType);
            return null;
        }

        var sections = SplitSections(output);
        var rebootLogAge = ParseSeconds(sections.GetValueOrDefault(RebootLogAgeMarker));
        var rebootLog = sections.GetValueOrDefault(RebootLogMarker);

        // A console booted without a synced clock dates its log on the wrong clock, and an old entry
        // then reads as this boot's. Hold the log back until the clock is right.
        var clockSkewed = !trustDeviceClock &&
            ClockSkewSeconds(sections.GetValueOrDefault(DeviceClockMarker)) > MaxClockSkewSeconds;
        var rebootLogStale = HasContent(rebootLog) &&
            (clockSkewed || RebootReasonParser.ConsoleRebootLogIsStale(rebootLog, rebootLogAge, firmwareKnownUnchanged));

        var reason = RebootReasonParser.Best(
            rebootLogStale ? null : RebootReasonParser.ParseConsoleRebootLog(rebootLog, rebootLogAge),
            RebootReasonParser.ParsePstore(
                sections.GetValueOrDefault(PstoreMarker),
                sections.GetValueOrDefault(ConsoleMarker),
                sections.GetValueOrDefault(CrashMarker),
                ParseSeconds(sections.GetValueOrDefault(CrashAgeMarker))),
            RebootReasonParser.ParseDeviceState(
                markerFirmware: HasContent(sections.GetValueOrDefault(UpgradeMarker))
                    ? sections.GetValueOrDefault(UpgradeMarker)
                    : null,
                markerAgeVsBootSeconds: ParseSeconds(sections.GetValueOrDefault(UpgradeAgeMarker)),
                firmwareChanged: firmwareChanged),
            RebootReasonParser.ParseClearedPstore(
                sections.GetValueOrDefault(PstoreMarker),
                consoleRingConfigured: HasContent(sections.GetValueOrDefault(ConsoleRingMarker))));

        if (!reason.IsConclusive)
        {
            // Distinguish "SSH worked but this platform keeps no evidence" from a reachability
            // problem: name which sections came back so the gap is obvious from the log alone.
            _logger.LogDebug(
                "Reboot reason probe found no evidence on {Host} ({DeviceType}): {Evidence}",
                host, deviceType, DescribeEvidence(sections));
            return null;
        }

        // This console keeps a reason log and has not written this boot's entry into it yet, or its
        // clock cannot date the entry, so the strongest source has still to speak. Answer from what
        // is here, but come back for it.
        if (rebootLogStale)
        {
            _logger.LogDebug(
                "Reboot reason probe on {Host} ({DeviceType}) resolved {Category} from {Source}, provisionally: " +
                "the console's reason log {Why}; evidence: {Evidence}",
                host, deviceType, reason.Category, reason.Source,
                clockSkewed ? "cannot be dated on the device's clock" : "still holds the previous boot's entry",
                DescribeEvidence(sections));

            return reason with { Provisional = true };
        }

        _logger.LogDebug(
            "Reboot reason probe on {Host} ({DeviceType}) resolved {Category} from {Source}; evidence: {Evidence}",
            host, deviceType, reason.Category, reason.Source, DescribeEvidence(sections));

        return reason;
    }

    /// <summary>One line describing which evidence sources answered, for the debug log.</summary>
    private static string DescribeEvidence(Dictionary<string, string> sections)
    {
        string Describe(string marker, string label)
        {
            var section = sections.GetValueOrDefault(marker);
            if (!HasContent(section))
                return $"{label}=absent";

            var lines = section!.Split('\n').Count(l => l.Trim().Length > 0);
            return $"{label}={lines} line(s)";
        }

        return string.Join(", ",
            Describe(PstoreMarker, "pstore"),
            Describe(ConsoleMarker, "console-ramoops"),
            Describe(CrashMarker, "dmesg-ramoops"),
            Describe(RebootLogMarker, "reboot-time.log"),
            Describe(UpgradeMarker, "post_upgrade_pending"),
            Describe(ConsoleRingMarker, "console-ring-configured"),
            $"reboot-log-vs-boot={ParseSeconds(sections.GetValueOrDefault(RebootLogAgeMarker))?.ToString() ?? "unknown"}s",
            $"marker-vs-boot={ParseSeconds(sections.GetValueOrDefault(UpgradeAgeMarker))?.ToString() ?? "unknown"}s",
            $"crash-vs-boot={ParseSeconds(sections.GetValueOrDefault(CrashAgeMarker))?.ToString() ?? "unknown"}s");
    }

    private static int? ParseSeconds(string? section) =>
        int.TryParse(section?.Trim(), out var seconds) ? seconds : null;

    /// <summary>How far the device's clock is from the server's, or 0 when the device did not report it.</summary>
    internal static long ClockSkewSeconds(string? deviceClockSection, long? serverNowUnix = null)
    {
        if (!long.TryParse(deviceClockSection?.Trim(), out var deviceNow)) return 0;
        var serverNow = serverNowUnix ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Math.Abs(deviceNow - serverNow);
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "no error text";
        var flattened = output.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flattened.Length <= 200 ? flattened : flattened[..200] + "...";
    }

    private async Task<(bool success, string output)> RunProbeAsync(
        string deviceMac, string host, DeviceType deviceType, CancellationToken cancellationToken)
    {
        try
        {
            // The gateway keeps the probe's own timeout; every other device keeps the SSH service default.
            var timeout = deviceType == DeviceType.Gateway ? ProbeTimeout : (TimeSpan?)null;
            return await _ssh.RunAsync(new DeviceSshTarget(deviceMac, host, deviceType), ProbeCommand, timeout, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reboot reason probe failed for {Host}", host);
            return (false, string.Empty);
        }
    }

    private static Dictionary<string, string> SplitSections(string output)
    {
        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var buffer = new List<string>();

        void Flush()
        {
            if (current != null)
                sections[current] = string.Join("\n", buffer);
            buffer.Clear();
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var trimmed = line.Trim();

            if (trimmed is PstoreMarker or ConsoleMarker or CrashMarker or RebootLogMarker
                or RebootLogAgeMarker or UpgradeMarker or UpgradeAgeMarker or ConsoleRingMarker
                or CrashAgeMarker or DeviceClockMarker)
            {
                Flush();
                current = trimmed;
                continue;
            }

            if (current != null)
                buffer.Add(line);
        }

        Flush();
        return sections;
    }

    // A shell that prints its own error text into the section (e.g. "No such file") has found nothing.
    private static bool HasContent(string? section) =>
        !string.IsNullOrWhiteSpace(section) &&
        !section.Contains("No such file", StringComparison.OrdinalIgnoreCase) &&
        !section.Contains("not found", StringComparison.OrdinalIgnoreCase);
}
