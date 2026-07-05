using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class TelekomModem2OntProviderTests
{
    // Captured verbatim from a live Glasfaser-Modem 2's /ONT/client/data/Status.json.
    private const string FullStatusJson = """
        [
          {"vartype":"value","varid":"device_name","varvalue":"Glasfaser-Modem 2"},
          {"vartype":"value","varid":"rebooting","varvalue":"0"},
          {"vartype":"value","varid":"ploam_state","varvalue":""},
          {"vartype":"value","varid":"ploam_success","varvalue":"1"},
          {"vartype":"value","varid":"save_fails","varvalue":"0"},
          {"vartype":"value","varid":"service_mode","varvalue":"0"},
          {"vartype":"page_title","varid":"title","varvalue":"Glasfaser-Modem 2 Konfigurationsprogramm"},
          {"vartype":"value","varid":"datetime","varvalue":"05.07.2026 00:23:38"},
          {"vartype":"value","varid":"firmware_version","varvalue":"090144.1.0.001"},
          {"vartype":"value","varid":"hardware_revision","varvalue":"V1"},
          {"vartype":"value","varid":"fw_version_standby","varvalue":"090144.1.0.001"},
          {"vartype":"value","varid":"hardware_state","varvalue":"1"},
          {"vartype":"value","varid":"txpackets","varvalue":"1875424524"},
          {"vartype":"value","varid":"txbytes","varvalue":"2288625314980"},
          {"vartype":"value","varid":"rxpackets","varvalue":"820314346"},
          {"vartype":"value","varid":"rxbytes","varvalue":"522995802597"},
          {"vartype":"value","varid":"rxdrop_packets","varvalue":"0"},
          {"vartype":"value","varid":"link_status","varvalue":"0"},
          {"vartype":"value","varid":"stability","varvalue":"1247559"},
          {"vartype":"value","varid":"rxbip_crc","varvalue":"0"},
          {"vartype":"value","varid":"serial_number","varvalue":"53434F4D00C0FFEE"},
          {"vartype":"value","varid":"txpower","varvalue":"2.39"},
          {"vartype":"value","varid":"rxpower","varvalue":"-16.13"},
          {"vartype":"value","varid":"ui_version","varvalue":"2.18.161"}
        ]
        """;

    [Fact]
    public void ApplyStatus_FullFixture_MapsAllFields()
    {
        var stats = new OntStats();

        TelekomModem2OntProvider.ApplyStatus(FullStatusJson, stats);

        stats.DeviceModel.Should().Be("Glasfaser-Modem 2");
        stats.VendorPn.Should().Be("V1");
        stats.VendorSn.Should().Be("53434F4D00C0FFEE");
        stats.TxPowerDbm.Should().BeApproximately(2.39, 0.0001);
        stats.RxPowerDbm.Should().BeApproximately(-16.13, 0.0001);
        stats.BipErrors.Should().Be(0);
        stats.LinkUptimeSeconds.Should().Be(1247559);
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Connected (O5)");
        stats.PonType.Should().BeNull();
    }

    [Fact]
    public void ApplyStatus_HardwareFault_SetsDown()
    {
        var stats = new OntStats();
        var json = """
            [{"vartype":"value","varid":"hardware_state","varvalue":"0"},
             {"vartype":"value","varid":"ploam_success","varvalue":"1"},
             {"vartype":"value","varid":"ploam_state","varvalue":""}]
            """;

        TelekomModem2OntProvider.ApplyStatus(json, stats);

        stats.OperationalStatus.Should().Be("Down");
        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
    }

    [Fact]
    public void ApplyStatus_NotYetActivated_FallsBackToPloamState()
    {
        var stats = new OntStats();
        var json = """
            [{"vartype":"value","varid":"hardware_state","varvalue":"1"},
             {"vartype":"value","varid":"ploam_success","varvalue":"0"},
             {"vartype":"value","varid":"ploam_state","varvalue":"O4"}]
            """;

        TelekomModem2OntProvider.ApplyStatus(json, stats);

        stats.OperationalStatus.Should().Be("Down");
        stats.PonLinkStatus.Should().Be(PonLinkState.Ranging);
    }

    [Fact]
    public void ApplyStatus_MalformedJson_DoesNotThrowAndLeavesDefaults()
    {
        var stats = new OntStats();

        var act = () => TelekomModem2OntProvider.ApplyStatus("not json", stats);

        act.Should().NotThrow();
        stats.RxPowerDbm.Should().BeNull();
        stats.TxPowerDbm.Should().BeNull();
    }

    [Fact]
    public void CreateClient_SetsAcceptLanguageHeader()
    {
        // The device's firmware rejects any request missing this header with a malformed
        // "400 Bad Request" - confirmed live against a real Glasfaser-Modem 2, and matches
        // Netzwerkfehler/hass-GFM2 (a working Home Assistant integration for this device).
        using var client = TelekomModem2OntProvider.CreateClient();

        client.DefaultRequestHeaders.AcceptLanguage.ToString().Should().Be("en");
    }
}
