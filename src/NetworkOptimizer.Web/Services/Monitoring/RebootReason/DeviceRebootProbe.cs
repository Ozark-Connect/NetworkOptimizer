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
    private readonly IUniFiSshService _deviceSsh;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly ILogger<DeviceRebootProbe> _logger;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    private const string PstoreMarker = "###PSTORE";
    private const string ConsoleMarker = "###CONSOLE";
    private const string CrashMarker = "###CRASH";
    private const string RebootLogMarker = "###REBOOTLOG";
    private const string UpgradeMarker = "###UPGRADE";

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
        $"echo '{RebootLogMarker}'",
        "tail -n 2 /var/log/reboot-time.log 2>/dev/null",
        $"echo '{UpgradeMarker}'",
        "ls /etc/persistent/post_upgrade_pending 2>/dev/null");

    /// <summary>Creates the probe.</summary>
    public DeviceRebootProbe(
        IUniFiSshService deviceSsh,
        IGatewaySshService gatewaySsh,
        ILogger<DeviceRebootProbe> logger)
    {
        _deviceSsh = deviceSsh;
        _gatewaySsh = gatewaySsh;
        _logger = logger;
    }

    /// <summary>
    /// Probe one device for why its previous run ended.
    /// </summary>
    /// <param name="host">Device IP or hostname to SSH to.</param>
    /// <param name="deviceType">Gateways use the console's SSH credentials, everything else the shared device ones.</param>
    /// <param name="firmwareChanged">Whether the reported firmware version changed across this boot.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The reason, or null when SSH produced nothing usable (not configured, unreachable, or a
    /// platform that keeps no pstore). Callers fall back to the UniFi Network event in that case.
    /// </returns>
    public async Task<DeviceRebootReason?> ProbeAsync(
        string host,
        DeviceType deviceType,
        bool firmwareChanged,
        CancellationToken cancellationToken = default)
    {
        var credentialSet = deviceType == DeviceType.Gateway ? "console" : "shared device";

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogDebug("Reboot reason probe skipped: no host address for a {DeviceType}", deviceType);
            return null;
        }

        var (success, output) = await RunProbeAsync(host, deviceType, cancellationToken);

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

        var reason = RebootReasonParser.Best(
            RebootReasonParser.ParseConsoleRebootLog(sections.GetValueOrDefault(RebootLogMarker)),
            RebootReasonParser.ParsePstore(
                sections.GetValueOrDefault(PstoreMarker),
                sections.GetValueOrDefault(ConsoleMarker),
                sections.GetValueOrDefault(CrashMarker)),
            RebootReasonParser.ParseDeviceState(
                hasPendingUpgradeMarker: HasContent(sections.GetValueOrDefault(UpgradeMarker)),
                firmwareChanged: firmwareChanged));

        if (!reason.IsConclusive)
        {
            // Distinguish "SSH worked but this platform keeps no evidence" from a reachability
            // problem: name which sections came back so the gap is obvious from the log alone.
            _logger.LogDebug(
                "Reboot reason probe found no evidence on {Host} ({DeviceType}): {Evidence}",
                host, deviceType, DescribeEvidence(sections));
            return null;
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
            Describe(UpgradeMarker, "post_upgrade_pending"));
    }

    private static string Summarize(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return "no error text";
        var flattened = output.Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flattened.Length <= 200 ? flattened : flattened[..200] + "...";
    }

    private async Task<(bool success, string output)> RunProbeAsync(
        string host, DeviceType deviceType, CancellationToken cancellationToken)
    {
        try
        {
            // The gateway is a UniFi OS console with its own credentials; APs and switches
            // share one device credential set.
            if (deviceType == DeviceType.Gateway)
                return await _gatewaySsh.RunCommandAsync(ProbeCommand, ProbeTimeout, cancellationToken);

            return await _deviceSsh.RunCommandAsync(host, ProbeCommand, portOverride: null,
                cancellationToken: cancellationToken);
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

            if (trimmed is PstoreMarker or ConsoleMarker or CrashMarker or RebootLogMarker or UpgradeMarker)
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
