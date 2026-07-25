using System.Text.RegularExpressions;

namespace NetworkOptimizer.Web.Services.Monitoring.RebootReason;

/// <summary>
/// Turns the raw output of the on-device reboot probe into a <see cref="DeviceRebootReason"/>.
///
/// Every UniFi platform checked (IPQ consoles, IPQ APs, Realtek MIPS switches) boots with
/// ramoops enabled, so the previous run's console ring survives the reboot in
/// <c>/sys/fs/pstore/console-ramoops-0</c> and a panic leaves a <c>dmesg-ramoops-*</c> record
/// beside it. That gives one probe that works fleet-wide:
///
/// <list type="bullet">
/// <item>An orderly stop ends the ring with <c>reboot: Restarting system</c> (Realtek switches add
/// <c>[RTK MS]System restart.</c>).</item>
/// <item>A firmware flash ends it with the early-upgrade banner ahead of that restart line.</item>
/// <item>Power loss or a hard hang leaves NO shutdown line at all - the ring just stops mid-traffic.</item>
/// </list>
///
/// UniFi OS consoles additionally keep <c>/var/log/reboot-time.log</c>, which records the SoC
/// restart register per boot and is the only source that separates power loss (<c>0x20</c>) from an
/// internal bus hang (<c>0x3</c>) from a clean reboot (<c>0x10</c>). It wins when present.
/// </summary>
public static class RebootReasonParser
{
    private const string RestartMarker = "reboot: Restarting system";
    private const string RealtekRestartMarker = "System restart";

    /// <summary>
    /// Parse the last line of a UniFi OS console's <c>/var/log/reboot-time.log</c>.
    /// </summary>
    /// <param name="logTail">One or more trailing lines of the log; the last non-empty one is used.</param>
    /// <returns>The reason, or null when the text does not look like a reboot-time entry.</returns>
    public static DeviceRebootReason? ParseConsoleRebootLog(string? logTail)
    {
        var line = LastNonEmptyLine(logTail);
        if (line == null || !line.Contains("Experience", StringComparison.OrdinalIgnoreCase))
            return null;

        // "Experience an improper shutdown(Power on Reset [0x20])"
        if (line.Contains("improper shutdown", StringComparison.OrdinalIgnoreCase))
        {
            var register = ExtractRegister(line);
            var (category, summary) = register?.Code switch
            {
                "0x20" => (RebootCategory.PowerLoss, "Power loss"),
                "0x3" => (RebootCategory.HardwareHang, "Internal bus hang"),
                _ => (RebootCategory.AbruptStop, "Unexpected stop")
            };

            var detail = register != null
                ? $"Restart register: {register.Value.Text}"
                : "Console recorded an improper shutdown";
            return new DeviceRebootReason(category, summary, detail, RebootReasonSource.ConsoleRebootLog);
        }

        // "Experience an upgrade reboot from <old> to <new>, and takes 203.943s"
        if (line.Contains("upgrade reboot", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceRebootReason(
                RebootCategory.FirmwareUpgrade,
                "Firmware upgrade",
                ExtractUpgradeVersions(line) ?? "Console recorded an upgrade reboot",
                RebootReasonSource.ConsoleRebootLog);
        }

        // "Experience a normal reboot" / "System reset or reboot [0x10]"
        if (line.Contains("normal reboot", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("reset or reboot", StringComparison.OrdinalIgnoreCase))
        {
            var register = ExtractRegister(line);
            return new DeviceRebootReason(
                RebootCategory.CommandedReboot,
                "Restarted",
                register != null ? $"Restart register: {register.Value.Text}" : "Console recorded a normal reboot",
                RebootReasonSource.ConsoleRebootLog);
        }

        return null;
    }

    /// <summary>
    /// Classify the previous run from the pstore records.
    /// </summary>
    /// <param name="pstoreListing">Output of listing <c>/sys/fs/pstore</c> (one name per line, or ls output).</param>
    /// <param name="consoleTail">Trailing lines of <c>console-ramoops-0</c>.</param>
    /// <param name="crashTail">First lines of <c>dmesg-ramoops-*</c> when one exists.</param>
    /// <returns>The reason, or null when pstore held nothing usable.</returns>
    public static DeviceRebootReason? ParsePstore(string? pstoreListing, string? consoleTail, string? crashTail = null)
    {
        var hasCrashDump = pstoreListing?.Contains("dmesg-ramoops", StringComparison.OrdinalIgnoreCase) == true;
        if (hasCrashDump)
        {
            return new DeviceRebootReason(
                RebootCategory.KernelPanic,
                "Kernel panic",
                FirstMeaningfulLine(crashTail) ?? "A kernel crash trace survived in pstore",
                RebootReasonSource.PstoreCrashDump);
        }

        var lines = NonEmptyLines(consoleTail);
        if (lines.Count == 0)
            return null;

        // The ECC footer ramoops appends ("No errors detected", "4 Corrected bytes, ...") is not
        // console output, so drop it before looking at how the run ended.
        var tail = string.Join("\n", lines.Where(l => !IsEccFooter(l)));
        if (tail.Length == 0)
            return null;

        var stoppedOnPurpose = tail.Contains(RestartMarker, StringComparison.OrdinalIgnoreCase) ||
                               tail.Contains(RealtekRestartMarker, StringComparison.OrdinalIgnoreCase);

        if (stoppedOnPurpose)
        {
            if (LooksLikeFirmwareFlash(tail))
            {
                return new DeviceRebootReason(
                    RebootCategory.FirmwareUpgrade,
                    "Firmware upgrade",
                    "Previous boot ran a firmware upgrade, then restarted",
                    RebootReasonSource.PstoreConsole);
            }

            if (LooksLikeWatchdog(tail))
            {
                return new DeviceRebootReason(
                    RebootCategory.Watchdog,
                    "Watchdog reset",
                    "Watchdog fired before the restart",
                    RebootReasonSource.PstoreConsole);
            }

            return new DeviceRebootReason(
                RebootCategory.CommandedReboot,
                "Restarted",
                "Previous boot shut down cleanly",
                RebootReasonSource.PstoreConsole);
        }

        if (LooksLikeWatchdog(tail))
        {
            return new DeviceRebootReason(
                RebootCategory.Watchdog,
                "Watchdog reset",
                "Watchdog fired with no clean shutdown",
                RebootReasonSource.PstoreConsole);
        }

        return new DeviceRebootReason(
            RebootCategory.AbruptStop,
            "Unexpected stop",
            "Previous boot stopped with no shutdown recorded - power loss or a hang",
            RebootReasonSource.PstoreConsole);
    }

    /// <summary>
    /// Read the pending-upgrade marker and firmware slot fields devices leave behind.
    /// Used to catch a firmware flash when pstore has already been overwritten.
    /// </summary>
    /// <param name="hasPendingUpgradeMarker">Whether <c>/etc/persistent/post_upgrade_pending</c> exists.</param>
    /// <param name="firmwareChanged">Whether the reported firmware version changed across the boot.</param>
    public static DeviceRebootReason? ParseDeviceState(bool hasPendingUpgradeMarker, bool firmwareChanged)
    {
        if (!hasPendingUpgradeMarker && !firmwareChanged)
            return null;

        var detail = firmwareChanged
            ? "Firmware version changed across the restart"
            : "Device left a pending-upgrade marker";

        return new DeviceRebootReason(
            RebootCategory.FirmwareUpgrade, "Firmware upgrade", detail, RebootReasonSource.DeviceState);
    }

    /// <summary>
    /// Last-resort mapping of a UniFi Network event key. These are generic: the "unknown reason"
    /// variants cover power loss, hangs and panics alike, so the summary says so rather than
    /// implying the console knows.
    /// </summary>
    /// <param name="eventKey">Event key such as <c>EVT_SW_Restarted</c>.</param>
    /// <param name="adminName">Admin the console attributed the restart to, when it named one.</param>
    public static DeviceRebootReason? ParseUniFiEvent(string? eventKey, string? adminName = null)
    {
        if (string.IsNullOrWhiteSpace(eventKey))
            return null;

        var key = eventKey.Trim();

        if (key.EndsWith("Upgraded", StringComparison.OrdinalIgnoreCase) ||
            key.EndsWith("UPGRADED", StringComparison.Ordinal))
        {
            return new DeviceRebootReason(RebootCategory.FirmwareUpgrade, "Firmware upgrade",
                "Reported by UniFi Network", RebootReasonSource.UniFiEvent);
        }

        if (key.Contains("OutletPowerCycle", StringComparison.OrdinalIgnoreCase))
        {
            return new DeviceRebootReason(RebootCategory.PowerCycle, "Power cycled",
                "Outlet power cycle reported by UniFi Network", RebootReasonSource.UniFiEvent);
        }

        if (key.Contains("RestartedUnknown", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("RESTARTED_UNKNOWN", StringComparison.Ordinal))
        {
            return new DeviceRebootReason(RebootCategory.Unknown,
                "Restarted, reason not reported",
                "UniFi Network logged an unknown restart reason",
                RebootReasonSource.UniFiEvent);
        }

        if (key.Contains("Restart", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("RESTART", StringComparison.Ordinal))
        {
            return new DeviceRebootReason(RebootCategory.CommandedReboot, "Restarted",
                string.IsNullOrWhiteSpace(adminName)
                    ? "Reported by UniFi Network"
                    : $"Restarted by {adminName}",
                RebootReasonSource.UniFiEvent);
        }

        return null;
    }

    /// <summary>
    /// Keep the strongest of several candidate reasons: best evidence source first, and a
    /// conclusive answer always beats an inconclusive one.
    /// </summary>
    public static DeviceRebootReason Best(params DeviceRebootReason?[] candidates)
    {
        var best = candidates
            .Where(c => c != null)
            .OrderByDescending(c => c!.IsConclusive)
            .ThenBy(c => (int)c!.Source)
            .FirstOrDefault();

        return best ?? DeviceRebootReason.Unknown();
    }

    private static bool LooksLikeFirmwareFlash(string tail) =>
        tail.Contains("Upgrading, please stand by", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("perform_early_upgrade", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("fwupdate", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeWatchdog(string tail) =>
        tail.Contains("watchdog reset", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("wdt reset", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("Watchdog expired", StringComparison.OrdinalIgnoreCase) ||
        tail.Contains("hardware watchdog", StringComparison.OrdinalIgnoreCase);

    // ramoops appends its ECC verdict after the console text; these lines carry no boot meaning.
    private static bool IsEccFooter(string line) =>
        line.Contains("No errors detected", StringComparison.OrdinalIgnoreCase) ||
        Regex.IsMatch(line, @"^\d+ Corrected bytes", RegexOptions.IgnoreCase);

    private static (string Code, string Text)? ExtractRegister(string line)
    {
        // "improper shutdown(Power on Reset [0x20])" / "System reset or reboot [0x10]"
        var match = Regex.Match(line, @"\(?([A-Za-z][A-Za-z /]*?)\s*\[(0x[0-9a-fA-F]+)\]\)?");
        if (!match.Success)
            return null;

        var name = match.Groups[1].Value.Trim();
        var code = match.Groups[2].Value.ToLowerInvariant();
        return (code, $"{name} [{match.Groups[2].Value}]");
    }

    private static string? ExtractUpgradeVersions(string line)
    {
        var match = Regex.Match(line, @"from\s+(\S+)\s+to\s+(\S+?)[,\s]", RegexOptions.IgnoreCase);
        return match.Success
            ? $"Upgraded from {match.Groups[1].Value} to {match.Groups[2].Value}"
            : null;
    }

    private static string? LastNonEmptyLine(string? text) =>
        NonEmptyLines(text).LastOrDefault();

    private static string? FirstMeaningfulLine(string? text) =>
        NonEmptyLines(text).FirstOrDefault(l => !IsEccFooter(l));

    private static List<string> NonEmptyLines(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? new List<string>()
            : text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.TrimEnd('\r').Trim())
                .Where(l => l.Length > 0)
                .ToList();
}
