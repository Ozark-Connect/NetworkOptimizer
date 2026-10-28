using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using Xunit;

namespace NetworkOptimizer.Monitoring.Tests;

/// <summary>
/// Unit tests for <see cref="RouterOsLteMonitorParser"/>. The fixture is real RouterOS 7.24.5 output
/// from a MikroTik ATLGM&amp;RG520F-EU (Quectel RG520F-EU module) on LTE, with the operator name replaced and the
/// identifiers redacted as the reporter sent them. Trailing padding is kept as printed.
/// </summary>
public class RouterOsLteMonitorParserTests
{
    private const string Host = "192.0.2.1";  // RFC 5737 documentation address

    private static readonly string LteMonitor = string.Join("\n",
        "status: running                              ",
        "model: RG520F-EU                            ",
        "revision: RG520FEUEAR03A07M4GX                 ",
        "current-operator: Test Carrier                         ",
        "current-cellid: 41979168                             ",
        "enb-id: 163981                               ",
        "sector-id: 32                                   ",
        "phy-cellid: 11                                   ",
        "data-class: LTE                                  ",
        "session-uptime: 1w2d4h4m29s                          ",
        "imei: xxxxx                      ",
        "imsi: xxxxx                      ",
        "iccid: xxxxx                 ",
        "primary-band: B20@10Mhz earfcn: 6200 phy-cellid: 11",
        "dl-modulation: qpsk                                 ",
        "cqi: 13                                   ",
        "ri: 1                                    ",
        "mcs: 0                                    ",
        "rssi: -58dBm                               ",
        "rsrp: -84dBm                               ",
        "rsrq: -9dB                                 ",
        "sinr: 16dB  ",
        "",
        "");

    private static readonly string SystemResource = string.Join("\n",
        "uptime: 2w5d20h59m50s      ",
        "version: 7.24.5 (stable)    ",
        "build-time: 2026-09-29 14:21:27",
        "minimum-version: 7.19               ",
        "free-memory: 410.2MiB           ",
        "total-memory: 512.0MiB           ",
        "cpu: ARM64              ",
        "cpu-count: 2                  ",
        "cpu-load: 0%                 ",
        "architecture-name: arm64              ",
        "board-name: ATLGM              ",
        "platform: MikroTik  ");

    // The same router over a one-shot `ssh user+ct@host "<commands>"`, the way the provider runs it:
    // keys are right-aligned with leading spaces, and RouterOS 7.25 prints no channel after the version.
    private static readonly string OneShotLteMonitor = string.Join("\n",
        "            status: running",
        "             model: RG520F-EU",
        "          revision: RG520FEUEAR03A07M4GX",
        "  current-operator: Test Carrier",
        "    current-cellid: 41979168",
        "            enb-id: 163981",
        "         sector-id: 32",
        "        phy-cellid: 11",
        "        data-class: LTE",
        "    session-uptime: 2d19h4m9s",
        "              imei: xxx",
        "              imsi: xxx",
        "             iccid: xxx",
        "      primary-band: B20@10MHz earfcn: 6200 phy-cellid: 11",
        "     dl-modulation: qpsk",
        "               cqi: 13",
        "                ri: 1",
        "               mcs: 0",
        "              rssi: -59dBm",
        "              rsrp: -85dBm",
        "              rsrq: -9dB",
        "              sinr: 17dB");

    private static readonly string OneShotSystemResource = string.Join("\n",
        "                   uptime: 2d19h4m57s",
        "                  version: 7.25",
        "               build-time: 2026-10-22 14:12:00",
        "          minimum-version: 7.19",
        "                 platform: MikroTik",
        "               board-name: ATLGM");

    [Fact]
    public void Parse_OneShotSshOutput_ReadsRightAlignedKeys()
    {
        var stats = RouterOsLteMonitorParser.Parse(OneShotLteMonitor, Host, "Backup LTE", "ATLGM")!;

        stats.Should().NotBeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.Lte!.Rsrp.Should().Be(-85);
        stats.Lte.Rsrq.Should().Be(-9);
        stats.Lte.Rssi.Should().Be(-59);
        stats.Lte.Snr.Should().Be(17);
        stats.Carrier.Should().Be("Test Carrier");
        stats.ModuleModel.Should().Be("RG520F-EU");
        stats.ActiveBand!.BandClass.Should().Be("eutran-20");
        stats.ActiveBand.BandwidthMhz.Should().Be(10, "the one-shot output spells the unit MHz");
        stats.ActiveBand.Channel.Should().Be(6200);
    }

    [Fact]
    public void ParseVersion_OneShotOutputWithoutChannel()
    {
        RouterOsLteMonitorParser.ParseVersion(OneShotSystemResource).Should().Be("7.25");
        RouterOsLteMonitorParser.ParseBoardName(OneShotSystemResource).Should().Be("ATLGM");
    }

    [Fact]
    public void Parse_LteMonitor_MapsSignal()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor, Host, "Backup LTE", "ATLGM")!;

        stats.Should().NotBeNull();
        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.Nr5g.Should().BeNull();
        stats.Lte!.Rsrp.Should().Be(-84);
        stats.Lte.Rsrq.Should().Be(-9);
        stats.Lte.Rssi.Should().Be(-58);
        stats.Lte.Snr.Should().Be(16);
    }

    [Fact]
    public void Parse_LteMonitor_MapsIdentityAndRegistration()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor, Host, "Backup LTE", "ATLGM")!;

        stats.ModemHost.Should().Be(Host);
        stats.ModemName.Should().Be("Backup LTE");
        stats.ModemModel.Should().Be("ATLGM");
        stats.Carrier.Should().Be("Test Carrier");
        stats.RegistrationState.Should().Be("registered", "RouterOS reports a live data session as running");
        stats.IsRoaming.Should().BeFalse();
        stats.ModuleModel.Should().Be("RG520F-EU");
        stats.ModuleVendor.Should().Be("Quectel");
        stats.SoftwareVersion.Should().Be("RG520FEUEAR03A07M4GX");
    }

    [Fact]
    public void Parse_LteMonitor_SplitsThePackedPrimaryBandLine()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor, Host, "Backup LTE", "ATLGM")!;

        stats.ActiveBand!.RadioInterface.Should().Be("lte");
        stats.ActiveBand.BandClass.Should().Be("eutran-20");
        stats.ActiveBand.BandwidthMhz.Should().Be(10);
        stats.ActiveBand.Channel.Should().Be(6200);
    }

    [Fact]
    public void Parse_LteMonitor_MapsServingCell()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor, Host, "Backup LTE", "ATLGM")!;

        stats.ServingCell!.IsServing.Should().BeTrue();
        stats.ServingCell.PhysicalCellId.Should().Be(11);
        stats.ServingCell.Earfcn.Should().Be(6200, "the monitor prints the EARFCN only inside primary-band");
        stats.ServingCell.GlobalCellId.Should().Be("41979168");
        stats.ServingCell.EnbId.Should().Be(163981, "it must match RouterOS's own enb-id");
        stats.ServingCell.SectorId.Should().Be(32, "it must match RouterOS's own sector-id");
        stats.ServingCell.Signal!.Rsrp.Should().Be(-84);
    }

    [Fact]
    public void Parse_LteMonitor_NeverCarriesIdentifiers()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor, Host, "Backup LTE", "ATLGM")!;

        JsonSerializer.Serialize(stats).Should().NotContain("xxxxx");
    }

    [Fact]
    public void Parse_CrLfLineEndings_ReadsTheSame()
    {
        var stats = RouterOsLteMonitorParser.Parse(LteMonitor.Replace("\n", "\r\n"), Host, "Backup LTE", "ATLGM")!;

        stats.Lte!.Rsrp.Should().Be(-84);
        stats.Lte.Snr.Should().Be(16);
        stats.Carrier.Should().Be("Test Carrier");
        stats.ActiveBand!.BandClass.Should().Be("eutran-20");
    }

    [Fact]
    public void Parse_UnknownAndMalformedLines_AreIgnored()
    {
        var noisy = "Flags: R - RUNNING\n" +
                    "some-future-field: 42\n" +
                    "this line has no key\n" +
                    "\u001b[m" + LteMonitor +
                    "another-new-field: -1dBm\n";

        var stats = RouterOsLteMonitorParser.Parse(noisy, Host, "Backup LTE", "ATLGM")!;

        stats.NetworkMode.Should().Be(CellularNetworkMode.Lte);
        stats.Lte!.Rsrp.Should().Be(-84);
        stats.ActiveBand!.BandClass.Should().Be("eutran-20");
        stats.ServingCell!.PhysicalCellId.Should().Be(11);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no such item")]
    [InlineData("status: searching\nmodel: RG520F-EU\n")]
    public void Parse_NoSignal_ReturnsNull(string? output)
    {
        RouterOsLteMonitorParser.Parse(output, Host, "Backup LTE", "ATLGM").Should().BeNull();
    }

    [Fact]
    public void HasMultipleInterfaces_SingleInterfaceOutput_IsFalse()
    {
        RouterOsLteMonitorParser.HasMultipleInterfaces(RouterOsLteMonitorParser.ParseFields(LteMonitor))
            .Should().BeFalse();
    }

    [Fact]
    public void ParseVersion_DropsTheChannel()
    {
        RouterOsLteMonitorParser.ParseVersion(SystemResource).Should().Be("7.24.5");
    }

    [Fact]
    public void ParseBoardName_ReadsResourcePrint()
    {
        RouterOsLteMonitorParser.ParseBoardName(SystemResource).Should().Be("ATLGM");
    }

    [Theory]
    [InlineData("RG520F-EU", "Quectel")]
    [InlineData("RM520N-GL", "Quectel")]
    [InlineData("EG18-EA", "Quectel")]
    [InlineData("EM7455", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void InferModuleVendor_OnlyQuectelPrefixes(string? model, string? expected)
    {
        RouterOsLteMonitorParser.InferModuleVendor(model).Should().Be(expected);
    }
}
