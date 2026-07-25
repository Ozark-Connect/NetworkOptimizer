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
    /// Read the upgrade evidence a device leaves outside pstore.
    ///
    /// Switches write <c>/etc/persistent/post_upgrade_pending</c> naming the image they flashed,
    /// which matters because their console ring shows an upgrade reboot as an ordinary clean
    /// shutdown - the flash itself is never announced there the way an AP announces it. The file
    /// persists, so it only counts when it was written around the boot it would explain;
    /// otherwise a months-old marker would relabel every later restart as an upgrade.
    /// </summary>
    /// <param name="markerFirmware">Contents of the marker, naming the flashed image, if present.</param>
    /// <param name="markerAgeVsBootSeconds">
    /// Marker mtime minus this boot's start, in seconds. Negative means it was written just before
    /// the reboot, which is the normal case. Null when the device could not report it.
    /// </param>
    /// <param name="firmwareChanged">Whether the reported firmware version changed across the boot.</param>
    public static DeviceRebootReason? ParseDeviceState(
        string? markerFirmware,
        int? markerAgeVsBootSeconds,
        bool firmwareChanged)
    {
        var markerBelongsToThisBoot = markerFirmware != null &&
            markerAgeVsBootSeconds.HasValue &&
            Math.Abs(markerAgeVsBootSeconds.Value) <= MarkerBootWindowSeconds;

        if (!markerBelongsToThisBoot && !firmwareChanged)
            return null;

        string detail;
        if (markerBelongsToThisBoot)
        {
            var image = ShortenFirmware(markerFirmware!.Trim());
            detail = string.IsNullOrWhiteSpace(image)
                ? "Device flashed a new image on this boot"
                : $"Upgraded to {image}";
        }
        else
        {
            detail = "Firmware version changed across the restart";
        }

        return new DeviceRebootReason(
            RebootCategory.FirmwareUpgrade, "Firmware upgrade", detail, RebootReasonSource.DeviceState);
    }

    /// <summary>How close the upgrade marker's mtime must sit to the boot to count as its cause.</summary>
    private const int MarkerBootWindowSeconds = 900;

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
    ///
    /// One exception overrides source order. A firmware upgrade looks exactly like a clean
    /// shutdown from the console ring, so "restarted" and "upgraded" are not competing claims
    /// about the same event - the upgrade is the more specific account of it. Whenever anything
    /// says the device flashed an image, that beats a bare CommandedReboot no matter which source
    /// each came from. It never displaces an unexpected finding: a device that upgraded and then
    /// lost power still reports the power loss.
    /// </summary>
    public static DeviceRebootReason Best(params DeviceRebootReason?[] candidates)
    {
        var known = candidates.Where(c => c != null).Select(c => c!).ToList();

        var best = known
            .OrderByDescending(c => c.IsConclusive)
            .ThenBy(c => (int)c.Source)
            .FirstOrDefault();

        if (best == null)
            return DeviceRebootReason.Unknown();

        if (best.Category == RebootCategory.CommandedReboot)
        {
            var upgrade = known
                .Where(c => c.Category == RebootCategory.FirmwareUpgrade)
                .OrderBy(c => (int)c.Source)
                .FirstOrDefault();

            if (upgrade != null)
                return upgrade;
        }

        return best;
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
            ? $"Upgraded from {ShortenFirmware(match.Groups[1].Value)} to {ShortenFirmware(match.Groups[2].Value)}"
            : null;
    }

    /// <summary>
    /// Reduce a firmware string to the part an operator reads. The platform, git hash and build
    /// stamp only make a tooltip unreadable. Both shapes in the fleet collapse to the version:
    /// consoles report <c>UXGA6AA.ipq9574.v5.1.26.0bc0fe4.260716.1128</c> and the switch upgrade
    /// marker reports <c>US3.rtl93xx_7.5.6+17090.260622.0846</c>; both become <c>5.1.26</c> /
    /// <c>7.5.6</c>.
    /// </summary>
    internal static string ShortenFirmware(string firmware)
    {
        if (string.IsNullOrWhiteSpace(firmware))
            return firmware;

        // Three components is the version everywhere in the fleet; anything after it is build
        // metadata, whatever separator it uses.
        var threePart = Regex.Match(firmware, @"(\d+\.\d+\.\d+)");
        if (threePart.Success)
            return threePart.Groups[1].Value;

        // Two-component versions exist on older builds. The lookahead keeps the match off a git
        // hash: in "v5.1.b3a286b" the trailing part is not a version component.
        var twoPart = Regex.Match(firmware, @"v?(\d+\.\d+)(?![0-9A-Za-z])", RegexOptions.IgnoreCase);
        return twoPart.Success ? twoPart.Groups[1].Value : firmware;
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
