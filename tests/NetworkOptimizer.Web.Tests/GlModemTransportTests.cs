using FluentAssertions;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Tests for gl_modem addressing discovery. Fixtures are trimmed from a GL-E5800
/// running an SoC-integrated Quectel RG650V-NA.
/// </summary>
public class GlModemTransportTests
{
    private const string IntegratedModemDiscovery = """
===INFO===
{
        "bus": "cpu",
        "name": "RG650V-NA",
        "version": "QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005",
        "vendor": "quectel",
        "sim_slot_num": 2,
        "at_port": "/dev/smd9"
}
===STATUS===
{
        "bus": "cpu",
        "status": 0,
        "current_sim_slot": "1",
        "slot_switch_count": 0
}
===USB===
""";

    private const string UsbOnlyDiscovery = """
===INFO===
===STATUS===
===USB===
1-1
1-1.2
usb1
""";

    [Fact]
    public void ParseDiscovery_IntegratedModem_UsesCpuBusAndCurrentSimSlot()
    {
        var endpoint = GlModemTransport.ParseDiscovery(IntegratedModemDiscovery, configuredBus: "");

        endpoint.Bus.Should().Be("cpu");
        endpoint.Sub.Should().Be(1);
        endpoint.Model.Should().Be("RG650V-NA");
        endpoint.Vendor.Should().Be("quectel");
        endpoint.Description.Should().Be("Quectel RG650V-NA");
    }

    [Fact]
    public void ParseDiscovery_IntegratedModem_WinsOverConfiguredBus()
    {
        var endpoint = GlModemTransport.ParseDiscovery(IntegratedModemDiscovery, configuredBus: "1-1.2");

        endpoint.Bus.Should().Be("cpu");
    }

    [Fact]
    public void ParseDiscovery_NoUbus_PrefersConfiguredBusAndOmitsSub()
    {
        var endpoint = GlModemTransport.ParseDiscovery(UsbOnlyDiscovery, configuredBus: "1-1.4");

        endpoint.Bus.Should().Be("1-1.4");
        endpoint.Sub.Should().BeNull();
    }

    [Fact]
    public void ParseDiscovery_NoUbusNoConfiguredBus_TakesFirstUsbPath()
    {
        var endpoint = GlModemTransport.ParseDiscovery(UsbOnlyDiscovery, configuredBus: "");

        endpoint.Bus.Should().Be("1-1");
        endpoint.Sub.Should().BeNull();
    }

    [Fact]
    public void ParseDiscovery_NothingFound_LeavesGlModemToChoose()
    {
        var endpoint = GlModemTransport.ParseDiscovery("===INFO===\n===STATUS===\n===USB===\n", configuredBus: "");

        endpoint.Bus.Should().BeNull();
        endpoint.Sub.Should().BeNull();
        endpoint.Description.Should().BeNull();
    }

    [Fact]
    public void BuildAtCommand_IntegratedModem_PassesBothFlags()
    {
        var command = GlModemTransport.BuildAtCommand(new GlModemEndpoint("cpu", 1), "AT+QENG=\"servingcell\"");

        command.Should().Be("gl_modem -B cpu -U 1 AT 'AT+QENG=\"servingcell\"'");
    }

    [Fact]
    public void BuildAtCommand_UsbModem_OmitsSubFlag()
    {
        var command = GlModemTransport.BuildAtCommand(new GlModemEndpoint("1-1.2", null), "AT+COPS?");

        command.Should().Be("gl_modem -B 1-1.2 AT 'AT+COPS?'");
    }

    [Fact]
    public void BuildAtCommand_UnknownEndpoint_MatchesLegacyForm()
    {
        var command = GlModemTransport.BuildAtCommand(GlModemEndpoint.Unknown, "AT+COPS?");

        command.Should().Be("gl_modem AT 'AT+COPS?'");
    }

    [Theory]
    [InlineData("cpu; reboot")]
    [InlineData("$(id)")]
    [InlineData("../../etc")]
    public void BuildAtCommand_RejectsBusThatIsNotAPath(string bus)
    {
        var act = () => GlModemTransport.BuildAtCommand(new GlModemEndpoint(bus, null), "AT");

        act.Should().Throw<ArgumentException>();
    }
}
