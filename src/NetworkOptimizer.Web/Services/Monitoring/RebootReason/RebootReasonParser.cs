using System.Text.RegularExpressions;
using NetworkOptimizer.Core.Helpers;

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
                ? $"{PlainRegisterCause(category)} (reset register: {register.Value.Text})"
                : PlainRegisterCause(category);
            return new DeviceRebootReason(category, summary, detail, RebootReasonSource.ConsoleRebootLog);
        }

        // "Experience an upgrade reboot from <old> to <new>, and takes 203.943s"
        if (line.Contains("upgrade reboot", StringComparison.OrdinalIgnoreCase))
        {
            var change = ExtractUpgradeVersions(line);
            return new DeviceRebootReason(
                RebootCategory.FirmwareUpgrade,
                change?.Summary ?? "Firmware upgrade",
                change?.Detail ?? "The device installed new firmware and restarted to run it",
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
                register != null
                    ? $"Something asked the device to restart (reset register: {register.Value.Text})"
                    : "Something asked the device to restart",
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
    /// <param name="crashAgeVsBootSeconds">
    /// Crash record mtime minus this boot's start, in seconds, or null when the device could not
    /// report it. Crash records outlive many boots, so their age is what ties one to this boot.
    /// </param>
    /// <returns>The reason, or null when pstore held nothing usable.</returns>
    public static DeviceRebootReason? ParsePstore(
        string? pstoreListing,
        string? consoleTail,
        string? crashTail = null,
        int? crashAgeVsBootSeconds = null)
    {
        var lines = NonEmptyLines(consoleTail);

        // The ECC footer ramoops appends ("No errors detected", "4 Corrected bytes, ...") is not
        // console output, so drop it before looking at how the run ended.
        var tail = string.Join("\n", lines.Where(l => !IsEccFooter(l)));

        var stoppedOnPurpose = tail.Contains(RestartMarker, StringComparison.OrdinalIgnoreCase) ||
                               tail.Contains(RealtekRestartMarker, StringComparison.OrdinalIgnoreCase);

        // A crash record is NOT proof that this boot followed a panic. pstore keeps them until
        // something erases them, so a device can carry a dump from months ago: one AP in the field
        // held two dumps from eight weeks earlier while its console ring showed a firmware upgrade
        // for the current boot, and its sibling with no dumps read correctly. Two independent checks
        // keep that history out. A ring ending in a deliberate stop means the previous run chose to
        // end, so any dump predates it. And where the device can date the dump - these carry real
        // mtimes, unlike the ring - it has to sit near this boot.
        var hasCrashDump = pstoreListing?.Contains("dmesg-ramoops", StringComparison.OrdinalIgnoreCase) == true;
        var crashBelongsToThisBoot = !crashAgeVsBootSeconds.HasValue ||
            Math.Abs(crashAgeVsBootSeconds.Value) <= CrashBootWindowSeconds;

        if (hasCrashDump && !stoppedOnPurpose && crashBelongsToThisBoot)
        {
            return new DeviceRebootReason(
                RebootCategory.KernelPanic,
                "Kernel panic",
                FirstMeaningfulLine(crashTail) ?? "The device software crashed",
                RebootReasonSource.PstoreCrashDump);
        }

        if (lines.Count == 0 || tail.Length == 0)
            return null;

        if (stoppedOnPurpose)
        {
            if (LooksLikeFirmwareFlash(tail))
            {
                return new DeviceRebootReason(
                    RebootCategory.FirmwareUpgrade,
                    "Firmware upgrade",
                    "The device installed new firmware and restarted to run it",
                    RebootReasonSource.PstoreConsole);
            }

            if (LooksLikeWatchdog(tail))
            {
                return new DeviceRebootReason(
                    RebootCategory.Watchdog,
                    "Watchdog reset",
                    "The device stopped responding and reset itself (watchdog)",
                    RebootReasonSource.PstoreConsole);
            }

            return new DeviceRebootReason(
                RebootCategory.CommandedReboot,
                "Restarted",
                "The device shut down cleanly, so something asked it to restart",
                RebootReasonSource.PstoreConsole);
        }

        if (LooksLikeWatchdog(tail))
        {
            return new DeviceRebootReason(
                RebootCategory.Watchdog,
                "Watchdog reset",
                "The device stopped responding and reset itself (watchdog, no clean shutdown)",
                RebootReasonSource.PstoreConsole);
        }

        return new DeviceRebootReason(
            RebootCategory.AbruptStop,
            "Unexpected stop",
            "The device stopped without shutting down, so it either lost power or hung",
            RebootReasonSource.PstoreConsole);
    }

    /// <summary>
    /// Infer power loss from pstore holding nothing on a device that does write a console ring.
    ///
    /// ramoops lives in reserved RAM. A warm reboot preserves it, so the previous run's console
    /// ring is there; losing power clears it, so the store comes up empty. That makes an empty
    /// pstore meaningful evidence on its own - and it is the only way a device with no reset
    /// register (an AP, a modem) can tell power loss apart from a warm restart.
    ///
    /// It only holds where the platform actually configures a console ring: a device with no
    /// <c>ramoops.console_size</c> never writes one, so its empty store says nothing. Verified
    /// across the fleet: a 5G modem whose boot matches a known outage reads empty with the ring
    /// configured, APs and switches that rebooted warm each hold a record, and a device bridge
    /// with no console_size reads empty for the unrelated reason.
    /// </summary>
    /// <param name="pstoreListing">Listing of <c>/sys/fs/pstore</c>.</param>
    /// <param name="consoleRingConfigured">Whether <c>ramoops.console_size</c> is on the kernel command line.</param>
    public static DeviceRebootReason? ParseClearedPstore(string? pstoreListing, bool consoleRingConfigured)
    {
        if (!consoleRingConfigured)
            return null;

        // Any record at all means the RAM survived, so this inference does not apply.
        if (NonEmptyLines(pstoreListing).Count > 0)
            return null;

        return new DeviceRebootReason(
            RebootCategory.PowerLoss,
            "Power loss",
            "The device lost power rather than being restarted (nothing from its previous run survived in memory)",
            RebootReasonSource.PstoreCleared);
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
                ? "The device installed new firmware on this boot"
                : $"Upgraded to {image}";
        }
        else
        {
            detail = "The device is running a different firmware version than before the restart";
        }

        return new DeviceRebootReason(
            RebootCategory.FirmwareUpgrade, "Firmware upgrade", detail, RebootReasonSource.DeviceState);
    }

    /// <summary>How close the upgrade marker's mtime must sit to the boot to count as its cause.</summary>
    private const int MarkerBootWindowSeconds = 900;

    /// <summary>How close a crash record's mtime must sit to the boot to be read as its cause.</summary>
    private const int CrashBootWindowSeconds = 900;

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

    /// <summary>
    /// Name the versions on a firmware upgrade using what the UniFi device data knows.
    ///
    /// The strongest upgrade evidence is often the least specific about versions: an AP's console
    /// ring says a flash happened but never which image, so the reason would read "ran a firmware
    /// upgrade" with no version. The console's own reason log DOES name both versions, and the
    /// switch marker names the new one, so an existing from/to detail is left alone.
    ///
    /// Naming both versions is also what settles whether the change was an upgrade or a downgrade.
    /// </summary>
    /// <param name="reason">The chosen reason.</param>
    /// <param name="previousFirmware">Firmware recorded on the device's previous boot, if known.</param>
    /// <param name="currentFirmware">Firmware the device reports now.</param>
    public static DeviceRebootReason WithFirmwareVersions(
        DeviceRebootReason reason,
        string? previousFirmware,
        string? currentFirmware)
    {
        if (reason.Category != RebootCategory.FirmwareUpgrade)
            return reason;

        var current = ShortenFirmware(currentFirmware?.Trim() ?? "");
        var previous = ShortenFirmware(previousFirmware?.Trim() ?? "");
        var haveCurrent = current.Length > 0;
        var havePrevious = previous.Length > 0 && !string.Equals(previous, current, StringComparison.OrdinalIgnoreCase);

        // A detail that already spans both versions is the most specific thing available.
        var detailNamesBoth = reason.Detail?.Contains(" to ", StringComparison.OrdinalIgnoreCase) == true &&
            reason.Detail.Contains("from", StringComparison.OrdinalIgnoreCase);
        if (detailNamesBoth || !haveCurrent)
            return reason;

        if (!havePrevious)
            return reason with { Detail = $"Upgraded to {current}" };

        // Both versions in hand is the only point where the direction can be established, so the
        // summary is settled here too - the evidence sources all call a flash an upgrade.
        var change = DescribeFirmwareChange(previous, current);
        return reason with { Summary = change.Summary, Detail = change.Detail };
    }

    /// <summary>Plain-language cause for a register-derived reason, before the register itself.</summary>
    private static string PlainRegisterCause(RebootCategory category) => category switch
    {
        RebootCategory.PowerLoss => "Power was removed from the device",
        RebootCategory.HardwareHang => "The device locked up internally and had to reset",
        _ => "The device stopped unexpectedly"
    };

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

    private static (string Summary, string Detail)? ExtractUpgradeVersions(string line)
    {
        var match = Regex.Match(line, @"from\s+(\S+)\s+to\s+(\S+?)[,\s]", RegexOptions.IgnoreCase);
        return match.Success
            ? DescribeFirmwareChange(
                ShortenFirmware(match.Groups[1].Value), ShortenFirmware(match.Groups[2].Value))
            : null;
    }

    /// <summary>
    /// Name a firmware change in the direction it actually went. A device put back on an older
    /// image is a downgrade, and the platform announces that as an "upgrade reboot" all the same,
    /// so the direction has to be worked out from the versions. Only claimed when both parse;
    /// otherwise the far commoner case is assumed.
    /// </summary>
    /// <param name="previous">Version the device ran before the restart, already shortened.</param>
    /// <param name="current">Version it runs now, already shortened.</param>
    internal static (string Summary, string Detail) DescribeFirmwareChange(string previous, string current) =>
        VersionUtilities.IsOlderThan(current, previous)
            ? ("Firmware downgrade", $"Downgraded from {previous} to {current}")
            : ("Firmware upgrade", $"Upgraded from {previous} to {current}");

    /// <summary>
    /// Whether two reported firmware strings name different images.
    ///
    /// Compared on the version alone: the console reports <c>displayable_version</c> to one caller
    /// and <c>version</c> to another, so the raw strings differ for the same image. A blank side is
    /// unknown, not different.
    /// </summary>
    /// <param name="previous">Firmware recorded earlier.</param>
    /// <param name="current">Firmware reported now.</param>
    internal static bool NamesADifferentImage(string? previous, string? current)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(current))
            return false;

        return !string.Equals(
            ShortenFirmware(previous.Trim()), ShortenFirmware(current.Trim()),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reduce a firmware string to the part an operator reads. Shared with every other firmware
    /// display so one device never reads two ways.
    /// </summary>
    internal static string ShortenFirmware(string firmware) =>
        NetworkOptimizer.Core.Helpers.FirmwareVersionFormat.Short(firmware);

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
