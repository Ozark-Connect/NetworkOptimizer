using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Services.CellularModemProviders;

/// <summary>
/// The command line, login name, and output handling of <see cref="MikroTikRouterOsProvider"/>.
/// Output follows real RouterOS 7.24.5 from a MikroTik ATLGM&amp;RG520F-EU, with the operator name replaced.
/// </summary>
public class MikroTikRouterOsProviderTests
{
    private static readonly ModemPollContext Context = new()
    {
        Id = 1,
        Name = "Backup LTE",
        Host = "192.0.2.1",  // RFC 5737 documentation address
        ModemType = MikroTikRouterOsProvider.ModemTypePlaceholder,
    };

    private const string Resource =
        "uptime: 2w5d20h59m50s      \n" +
        "version: 7.24.5 (stable)    \n" +
        "board-name: ATLGM              \n" +
        "platform: MikroTik  \n";

    private const string Monitor =
        "status: running                              \n" +
        "model: RG520F-EU                            \n" +
        "revision: RG520FEUEAR03A07M4GX                 \n" +
        "current-operator: Test Carrier                         \n" +
        "current-cellid: 41979168                             \n" +
        "phy-cellid: 11                                   \n" +
        "data-class: LTE                                  \n" +
        "primary-band: B20@10Mhz earfcn: 6200 phy-cellid: 11\n" +
        "rssi: -58dBm                               \n" +
        "rsrp: -84dBm                               \n" +
        "rsrq: -9dB                                 \n" +
        "sinr: 16dB  \n";

    private static string Script(string? iface) => MikroTikRouterOsProvider.BuildScript(iface)!;

    private static string Marker() =>
        // The marker is whatever the script :put's between the two commands.
        Script(null).Split('"')[1];

    [Fact]
    public void BuildScript_NoInterface_MonitorsFind()
    {
        Script(null).Should().Be(
            "/system resource print; :put \"" + Marker() + "\"; /interface lte monitor [find] once");
        Script("  ").Should().EndWith("/interface lte monitor [find] once");
    }

    [Fact]
    public void BuildScript_NamedInterface_IsQuoted()
    {
        Script(" lte1 ").Should().EndWith("/interface lte monitor \"lte1\" once");
    }

    [Theory]
    [InlineData("lte1\"; /system reboot")]
    [InlineData("lte1; /system reboot")]
    [InlineData("[find]")]
    [InlineData("$x")]
    [InlineData("lte1\n/system reboot")]
    public void BuildScript_UnsafeInterface_IsRefused(string iface)
    {
        MikroTikRouterOsProvider.BuildScript(iface).Should().BeNull();
    }

    [Theory]
    [InlineData("netopt", "netopt+ct")]
    [InlineData(" netopt ", "netopt+ct")]
    [InlineData("netopt+ct", "netopt+ct")]
    [InlineData("netopt+cte", "netopt+cte")]
    public void LoginName_AddsTheConsoleOptionsOnce(string username, string expected)
    {
        MikroTikRouterOsProvider.LoginName(username).Should().Be(expected);
    }

    [Fact]
    public void Interpret_RealOutput_FillsDeviceAndModule()
    {
        var (stats, error) = MikroTikRouterOsProvider.Interpret(Resource + Marker() + "\n" + Monitor, Context);

        error.Should().BeNull();
        stats!.ModemModel.Should().Be("ATLGM");
        stats.HostVersion.Should().Be("7.24.5");
        stats.ModuleVendor.Should().Be("Quectel");
        stats.ModuleModel.Should().Be("RG520F-EU");
        stats.SoftwareVersion.Should().Be("RG520FEUEAR03A07M4GX");
        stats.Lte!.Rsrp.Should().Be(-84);
    }

    [Fact]
    public void Interpret_NoBoardName_DropsThePlaceholderModel()
    {
        var (stats, _) = MikroTikRouterOsProvider.Interpret(Marker() + "\n" + Monitor, Context);

        stats!.ModemModel.Should().BeEmpty("\"MikroTik\" is the form's placeholder, and the brand already shows it");
    }

    [Fact]
    public void Interpret_MonitorError_ReportsRouterOsText()
    {
        var (stats, error) = MikroTikRouterOsProvider.Interpret(Resource + Marker() + "\nno such item\n", Context);

        stats.Should().BeNull();
        error.Should().Contain("no such item");
    }

    [Fact]
    public void Interpret_NoMarker_ReportsTheScriptDidNotRun()
    {
        var (stats, error) = MikroTikRouterOsProvider.Interpret("expected end of command (line 1 column 9)\n", Context);

        stats.Should().BeNull();
        error.Should().Contain("expected end of command");
    }
}
