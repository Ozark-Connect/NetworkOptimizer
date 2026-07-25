using NetworkOptimizer.Web.Services.Monitoring.RebootReason;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Fixtures are trimmed from real probe output captured over SSH from a UniFi OS console
/// (IPQ gateway), a U7-Pro-XGS-B access point (IPQ) and USW-Pro-XG-8-PoE / USW-Lite-8-PoE
/// switches (Realtek MIPS).
/// </summary>
public class RebootReasonParserTests
{
    [Fact]
    public void ConsoleRebootLog_PowerOnReset_IsPowerLoss()
    {
        const string log = "2026-07-15T16:00:55-0500 Experience an improper shutdown(Power on Reset [0x20])";

        var reason = RebootReasonParser.ParseConsoleRebootLog(log);

        Assert.NotNull(reason);
        Assert.Equal(RebootCategory.PowerLoss, reason!.Category);
        Assert.Equal("Power loss", reason.Summary);
        Assert.Contains("Power on Reset [0x20]", reason.Detail);
        Assert.Equal(RebootReasonSource.ConsoleRebootLog, reason.Source);
        Assert.True(reason.IsUnexpected);
    }

    [Fact]
    public void ConsoleRebootLog_AhbTimeout_IsHardwareHang()
    {
        const string log = "2026-07-19T01:02:11-0500 Experience an improper shutdown(AHB Timeout [0x3])";

        var reason = RebootReasonParser.ParseConsoleRebootLog(log);

        Assert.Equal(RebootCategory.HardwareHang, reason!.Category);
        Assert.Contains("AHB Timeout [0x3]", reason.Detail);
        Assert.True(reason.IsUnexpected);
    }

    [Fact]
    public void ConsoleRebootLog_UpgradeReboot_ReportsVersions()
    {
        const string log = "2026-07-21T15:34:35-0500 Experience an upgrade reboot from " +
            "UXGA6AA.ipq9574.v5.1.17.b3a286b.260608.1701 to UXGA6AA.ipq9574.v5.1.26.0bc0fe4.260716.1128, " +
            "and takes 203.943s (0:03:23.942869)";

        var reason = RebootReasonParser.ParseConsoleRebootLog(log);

        Assert.Equal(RebootCategory.FirmwareUpgrade, reason!.Category);
        Assert.Equal("Upgraded from 5.1.17 to 5.1.26", reason.Detail);
        Assert.False(reason.IsUnexpected);
    }

    [Theory]
    [InlineData("UXGA6AA.ipq9574.v5.1.26.0bc0fe4.260716.1128", "5.1.26")]
    [InlineData("UXGA6AA.ipq9574.v5.0.10.d29afb8.251229.1655", "5.0.10")]
    [InlineData("v7.5.6", "7.5.6")]
    [InlineData("7.5.6.17090", "7.5.6.17090")]
    public void ShortenFirmware_KeepsOnlyTheVersion(string firmware, string expected)
    {
        Assert.Equal(expected, RebootReasonParser.ShortenFirmware(firmware));
    }

    [Fact]
    public void ConsoleRebootLog_NormalReboot_IsCommanded()
    {
        const string log = "2026-05-14T01:49:01-0500 Experience a normal reboot, and takes 91.590s (0:01:31.589680)";

        var reason = RebootReasonParser.ParseConsoleRebootLog(log);

        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.False(reason.IsUnexpected);
    }

    [Fact]
    public void ConsoleRebootLog_SystemResetRegister_IsCommanded()
    {
        const string log = "2026-07-23T03:53:02-0500 Experience a System reset or reboot [0x10]";

        var reason = RebootReasonParser.ParseConsoleRebootLog(log);

        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.Contains("[0x10]", reason.Detail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cat: /var/log/reboot-time.log: No such file or directory")]
    public void ConsoleRebootLog_NotAConsole_ReturnsNull(string? log)
    {
        Assert.Null(RebootReasonParser.ParseConsoleRebootLog(log));
    }

    [Fact]
    public void Pstore_CrashDumpPresent_IsKernelPanic()
    {
        const string listing = "console-ramoops-0\ndmesg-ramoops-0";
        const string crash = "Panic#1 Part1\nUnable to handle kernel paging request at virtual address 0000000c";

        var reason = RebootReasonParser.ParsePstore(listing, consoleTail: "whatever", crashTail: crash);

        Assert.Equal(RebootCategory.KernelPanic, reason!.Category);
        Assert.Contains("Panic#1", reason.Detail);
        Assert.Equal(RebootReasonSource.PstoreCrashDump, reason.Source);
    }

    [Fact]
    public void Pstore_ApFirmwareFlash_IsFirmwareUpgrade()
    {
        // Captured from ap-tiny-home (U7-Pro-XGS-B)
        const string tail = """
            [    5.424560] preinit: running 'preinit_ubnt/start_fanctrl'
            [    5.469425] preinit: running 'preinit_ubnt/perform_early_upgrade'
            [    6.404426] Upgrading, please stand by...
            [   19.278002] reboot: Restarting system

            No errors detected
            """;

        var reason = RebootReasonParser.ParsePstore("console-ramoops-0", tail);

        Assert.Equal(RebootCategory.FirmwareUpgrade, reason!.Category);
        Assert.Equal(RebootReasonSource.PstoreConsole, reason.Source);
        Assert.False(reason.IsUnexpected);
    }

    [Fact]
    public void Pstore_RealtekCleanRestart_IsCommandedReboot()
    {
        // Captured from switch-tiny-home-1 (USW-Pro-XG-8-PoE, RTL9313)
        const string tail = """
            [747190.870000] Port 10 moving from Forwarding to Disabled
            [747192.920000] reboot: Restarting system
            [747192.920000] [RTK MS]System restart.
            [747192.920000] [rtl9310_bspChip_reset]RESET

            4 Corrected bytes, 0 unrecoverable blocks
            """;

        var reason = RebootReasonParser.ParsePstore("console-ramoops-0", tail);

        Assert.Equal(RebootCategory.CommandedReboot, reason!.Category);
        Assert.False(reason.IsUnexpected);
    }

    [Fact]
    public void Pstore_RingStopsMidTraffic_IsAbruptStop()
    {
        // No shutdown line: the run was cut off (power loss or hang)
        const string tail = """
            [2026558.040000] Port 1 moving from Forwarding to Disabled
            [2026558.340000] Port 3 link down
            [2026559.010000] sh (15889): drop_caches: 3

            No errors detected
            """;

        var reason = RebootReasonParser.ParsePstore("console-ramoops-0", tail);

        Assert.Equal(RebootCategory.AbruptStop, reason!.Category);
        Assert.Contains("no shutdown recorded", reason.Detail);
        Assert.True(reason.IsUnexpected);
    }

    [Fact]
    public void Pstore_WatchdogBeforeRestart_IsWatchdog()
    {
        const string tail = """
            [   88.120000] hardware watchdog expired, resetting
            [   88.130000] reboot: Restarting system
            """;

        var reason = RebootReasonParser.ParsePstore("console-ramoops-0", tail);

        Assert.Equal(RebootCategory.Watchdog, reason!.Category);
        Assert.True(reason.IsUnexpected);
    }

    [Fact]
    public void Pstore_EccFooterOnly_ReturnsNull()
    {
        Assert.Null(RebootReasonParser.ParsePstore("console-ramoops-0", "No errors detected"));
    }

    [Fact]
    public void Pstore_NoRecords_ReturnsNull()
    {
        Assert.Null(RebootReasonParser.ParsePstore(pstoreListing: "", consoleTail: null));
    }

    [Fact]
    public void DeviceState_FirmwareChanged_IsFirmwareUpgrade()
    {
        var reason = RebootReasonParser.ParseDeviceState(
            markerFirmware: null, markerAgeVsBootSeconds: null, firmwareChanged: true);

        Assert.Equal(RebootCategory.FirmwareUpgrade, reason!.Category);
        Assert.Equal(RebootReasonSource.DeviceState, reason.Source);
        Assert.Contains("changed across the restart", reason.Detail);
    }

    /// <summary>
    /// Real marker from a USW-Pro-XG-8-PoE, written 122 s before the boot it caused.
    /// </summary>
    [Fact]
    public void DeviceState_MarkerWrittenAtThisBoot_NamesTheImage()
    {
        var reason = RebootReasonParser.ParseDeviceState(
            markerFirmware: "US3.rtl93xx_7.5.6+17090.260622.0846",
            markerAgeVsBootSeconds: -122,
            firmwareChanged: false);

        Assert.Equal(RebootCategory.FirmwareUpgrade, reason!.Category);
        Assert.Contains("7.5.6", reason.Detail);
    }

    /// <summary>
    /// The marker lives in persistent storage, so a months-old one must not relabel a later
    /// restart as an upgrade.
    /// </summary>
    [Fact]
    public void DeviceState_StaleMarker_IsIgnored()
    {
        var reason = RebootReasonParser.ParseDeviceState(
            markerFirmware: "US3.rtl93xx_7.5.6+17090.260622.0846",
            markerAgeVsBootSeconds: -60 * 60 * 24 * 30,
            firmwareChanged: false);

        Assert.Null(reason);
    }

    [Fact]
    public void DeviceState_MarkerWithoutAge_IsIgnored()
    {
        Assert.Null(RebootReasonParser.ParseDeviceState(
            markerFirmware: "US3.rtl93xx_7.5.6+17090.260622.0846",
            markerAgeVsBootSeconds: null,
            firmwareChanged: false));
    }

    [Fact]
    public void DeviceState_NothingKnown_ReturnsNull()
    {
        Assert.Null(RebootReasonParser.ParseDeviceState(null, null, false));
    }

    /// <summary>
    /// The switch case that was getting mislabelled: an upgrade reboot leaves a Realtek console
    /// ring that looks like any clean shutdown, so the upgrade evidence has to win even though
    /// pstore ranks as the stronger source.
    /// </summary>
    [Fact]
    public void Best_UpgradeEvidenceBeatsGenericCleanShutdown()
    {
        var pstore = RebootReasonParser.ParsePstore("console-ramoops-0", """
            [747190.870000] Port 10 moving from Forwarding to Disabled
            [747192.920000] reboot: Restarting system
            [747192.920000] [RTK MS]System restart.
            """);
        var upgrade = RebootReasonParser.ParseDeviceState(
            markerFirmware: "US3.rtl93xx_7.5.6+17090.260622.0846",
            markerAgeVsBootSeconds: -122,
            firmwareChanged: false);

        Assert.Equal(RebootCategory.CommandedReboot, pstore!.Category);

        var best = RebootReasonParser.Best(pstore, upgrade);

        Assert.Equal(RebootCategory.FirmwareUpgrade, best.Category);
    }

    /// <summary>
    /// The upgrade preference must not swallow an unexpected stop: a device that upgraded and then
    /// lost power still reports the power loss.
    /// </summary>
    [Fact]
    public void Best_UpgradeEvidenceDoesNotDisplacePowerLoss()
    {
        var register = RebootReasonParser.ParseConsoleRebootLog(
            "2026-07-15T16:00:55-0500 Experience an improper shutdown(Power on Reset [0x20])");
        var upgrade = RebootReasonParser.ParseDeviceState("US3.rtl93xx_7.5.6+17090", -60, false);

        var best = RebootReasonParser.Best(register, upgrade);

        Assert.Equal(RebootCategory.PowerLoss, best.Category);
    }

    [Theory]
    [InlineData("EVT_SW_Upgraded", RebootCategory.FirmwareUpgrade)]
    [InlineData("EVT_AP_UPGRADED", RebootCategory.FirmwareUpgrade)]
    [InlineData("EVT_GW_Restarted", RebootCategory.CommandedReboot)]
    [InlineData("EVT_AP_RestartedUnknown", RebootCategory.Unknown)]
    [InlineData("EVT_SW_RESTARTED_UNKNOWN", RebootCategory.Unknown)]
    [InlineData("EVT_USP_OutletPowerCycle", RebootCategory.PowerCycle)]
    public void UniFiEvent_MapsToCategory(string eventKey, RebootCategory expected)
    {
        var reason = RebootReasonParser.ParseUniFiEvent(eventKey);

        Assert.NotNull(reason);
        Assert.Equal(expected, reason!.Category);
        Assert.Equal(RebootReasonSource.UniFiEvent, reason.Source);
    }

    [Fact]
    public void UniFiEvent_NamedAdmin_IsCredited()
    {
        var reason = RebootReasonParser.ParseUniFiEvent("EVT_SW_Restarted", adminName: "Admin");

        Assert.Contains("Restarted by Admin", reason!.Detail);
    }

    [Fact]
    public void UniFiEvent_Unrelated_ReturnsNull()
    {
        Assert.Null(RebootReasonParser.ParseUniFiEvent("EVT_AP_ChannelChanged"));
    }

    [Fact]
    public void Best_PrefersConsoleRegisterOverUniFiEvent()
    {
        var consoleLog = RebootReasonParser.ParseConsoleRebootLog(
            "2026-07-15T16:00:55-0500 Experience an improper shutdown(Power on Reset [0x20])");
        var unifiEvent = RebootReasonParser.ParseUniFiEvent("EVT_GW_RestartedUnknown");

        var best = RebootReasonParser.Best(unifiEvent, consoleLog);

        Assert.Equal(RebootCategory.PowerLoss, best.Category);
        Assert.Equal(RebootReasonSource.ConsoleRebootLog, best.Source);
    }

    [Fact]
    public void Best_PrefersConclusiveOverUnknown()
    {
        var vague = RebootReasonParser.ParseUniFiEvent("EVT_AP_RestartedUnknown");
        var pstore = RebootReasonParser.ParsePstore("console-ramoops-0",
            "[ 5.4] reboot: Restarting system");

        var best = RebootReasonParser.Best(vague, pstore);

        Assert.Equal(RebootCategory.CommandedReboot, best.Category);
    }

    /// <summary>
    /// The Honeybee gateway case: two AHB Timeout resets whose console ring held only early-boot
    /// output and no shutdown line, and no panic record. pstore alone can only say "cut off";
    /// the restart register is what names it, so the register has to win.
    /// </summary>
    [Fact]
    public void Best_SilentHang_RegisterNamesItOverPstoreAndEvent()
    {
        const string silentRing = """
            [   24.918000] br0: port 2(eth1) entered forwarding state
            [   25.104000] ubnt-hald: fan control applied

            No errors detected
            """;

        var best = RebootReasonParser.Best(
            RebootReasonParser.ParseUniFiEvent("EVT_GW_RestartedUnknown"),
            RebootReasonParser.ParsePstore("console-ramoops-0", silentRing),
            RebootReasonParser.ParseConsoleRebootLog(
                "2026-07-19T16:00:41-0500 Experience an improper shutdown(AHB Timeout [0x3])"));

        Assert.Equal(RebootCategory.HardwareHang, best.Category);
        Assert.Equal(RebootReasonSource.ConsoleRebootLog, best.Source);
        Assert.Contains("AHB Timeout [0x3]", best.Detail);
    }

    /// <summary>
    /// Same silent ring without a console reason log (an AP or switch): the honest answer is
    /// "cut off, cause not recorded on the box", never a guess at power loss.
    /// </summary>
    [Fact]
    public void Best_SilentHangWithoutRegister_ReportsAbruptStop()
    {
        var best = RebootReasonParser.Best(
            RebootReasonParser.ParsePstore("console-ramoops-0", "[ 25.104000] ubnt-hald: started"),
            RebootReasonParser.ParseConsoleRebootLog(null));

        Assert.Equal(RebootCategory.AbruptStop, best.Category);
        Assert.True(best.IsUnexpected);
    }

    [Fact]
    public void Best_NothingFound_IsUnknownPlaceholder()
    {
        var best = RebootReasonParser.Best(null, null);

        Assert.False(best.IsConclusive);
        Assert.Equal("Reason unavailable", best.Summary);
    }
}
