using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Characterisation tests for <see cref="RealtekOntProvider"/>'s status_pon.asp parser.
///
/// The fixture reproduces the rendered <c>GET /status_pon.asp</c> page of a Realtek
/// RTL960x GPON SFP stick. Its structure - the "PON Status" title, the
/// <c>&lt;tr bgcolor="#DDDDDD"&gt;</c> rows, and the English label strings
/// (Temperature / Voltage / Tx Power / Rx Power / Bias Current / ONU State) - is verbatim
/// from the Luleey LL-XS2510 firmware web root (home/httpd/web/status_pon.asp plus the
/// compiled ponGetRegStatus()/showgpon_status() row emitter in bin/boa, byte-identical
/// across firmware v1.0.1 / v1.0.2 / v1.1.4-HGU). The optical *values* are representative
/// of a live GPON link; only the markup they sit in is asserted, which is what the parser
/// depends on. This confirms the shipped "realtek-ont" provider supports the Luleey with
/// no code change - it is configuration-only.
/// </summary>
public class RealtekOntProviderTests
{
    private static OntPollContext Context() => new()
    {
        Id = 1,
        Name = "Luleey WAN ONT",
        Host = "192.168.1.1",
        ConfiguredHost = "192.168.1.1",
        Port = 80,
        Username = "admin",
        Password = "admin",
    };

    // Verbatim structure from status_pon.asp; the ONU State row is emitted by the
    // compiled showgpon_status() as <tr bgcolor="#DDDDDD">...O5</tr>.
    private const string LuleeyStatusPon = """
    <html><head><title>PON Status</title></head><body>
    <h2>PON Status</h2>
    <table width=400 border=0>
      <tr><td colspan="2" bgcolor="#008000"><b>PON Status</b></td></tr>
      <tr bgcolor="#DDDDDD"><td width=40%><b>Temperature</b></td><td width=60%>45.55 C</td></tr>
      <tr bgcolor="#DDDDDD"><td width=40%><b>Voltage</b></td><td width=60%>3.29 V</td></tr>
      <tr bgcolor="#DDDDDD"><td width=40%><b>Tx Power</b></td><td width=60%>2.14 dBm</td></tr>
      <tr bgcolor="#DDDDDD"><td width=40%><b>Rx Power</b></td><td width=60%>-19.23 dBm</td></tr>
      <tr bgcolor="#DDDDDD"><td width=40%><b>Bias Current</b></td><td width=60%>9.51 mA</td></tr>
    </table>
    <table>
      <tr bgcolor="#DDDDDD"><td width="30%"><b>ONU State</b></td><td width="70%">O5</td></tr>
    </table>
    </body></html>
    """;

    [Fact]
    public void ParseStatusPon_LuleeyStatusPage_MapsAllOpticsAndState()
    {
        var stats = RealtekOntProvider.ParseStatusPon(LuleeyStatusPon, Context());

        stats.TemperatureC.Should().Be(45.55);
        stats.VoltageV.Should().Be(3.29);
        stats.TxPowerDbm.Should().Be(2.14);
        stats.RxPowerDbm.Should().Be(-19.23, "receive power is negative and the sign must survive parsing");
        stats.BiasMa.Should().Be(9.51);
        stats.LinkState.Should().Be("O5", "ONU State row carries the ITU O-state string");
        stats.PonType.Should().Be("GPON");
        stats.DeviceModel.Should().Be("Realtek ONT");
        stats.DeviceHost.Should().Be("192.168.1.1");
        stats.DeviceName.Should().Be("Luleey WAN ONT");
    }

    [Fact]
    public void ParseStatusPon_StripsUnitsFromValues()
    {
        // The label/value cells carry a trailing unit ("3.29 V"); only the number is stored.
        var stats = RealtekOntProvider.ParseStatusPon(LuleeyStatusPon, Context());

        stats.VoltageV.Should().Be(3.29);
        stats.TxPowerDbm.Should().Be(2.14);
    }

    [Fact]
    public void ParseStatusPon_PartialPage_MapsOnlyPresentRows()
    {
        const string partial = """
        <html><body><table>
          <tr bgcolor="#DDDDDD"><td><b>Temperature</b></td><td>50.10 C</td></tr>
          <tr bgcolor="#DDDDDD"><td><b>Rx Power</b></td><td>-21.00 dBm</td></tr>
        </table></body></html>
        """;

        var stats = RealtekOntProvider.ParseStatusPon(partial, Context());

        stats.TemperatureC.Should().Be(50.10);
        stats.RxPowerDbm.Should().Be(-21.00);
        stats.VoltageV.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
        stats.BiasMa.Should().BeNull();
        stats.LinkState.Should().BeNull();
        stats.PonType.Should().Be("GPON", "PonType is seeded once at least one status row is parsed");
    }

    [Fact]
    public void ParseStatusPon_NoDataRows_ReturnsSeededStatsWithoutThrowing()
    {
        // A login/redirect page or an empty response must not throw, just yield no readings.
        const string noRows = "<html><body><h2>PON Status</h2><p>no data</p></body></html>";

        var act = () => RealtekOntProvider.ParseStatusPon(noRows, Context());

        var stats = act.Should().NotThrow().Which;
        stats.RxPowerDbm.Should().BeNull();
        stats.TemperatureC.Should().BeNull();
        stats.LinkState.Should().BeNull();
        // Characterisation: PonType is assigned only after the row loop, so a page with no
        // #DDDDDD data rows returns without it set. A minor shipped-behaviour quirk, left as-is
        // (changing it would alter output for every Realtek stick, not just the Luleey).
        stats.PonType.Should().BeNull();
        stats.DeviceHost.Should().Be("192.168.1.1");
    }

    [Fact]
    public void ParseStatusPon_UsesConfiguredHostForDeviceHostWhenPresent()
    {
        var ctx = Context() with { Host = "127.0.0.1", ConfiguredHost = "192.168.1.1" };

        var stats = RealtekOntProvider.ParseStatusPon(LuleeyStatusPon, ctx);

        stats.DeviceHost.Should().Be("192.168.1.1",
            "logs and UI should name the real device, not a tunnel-proxy loopback host");
    }
}
