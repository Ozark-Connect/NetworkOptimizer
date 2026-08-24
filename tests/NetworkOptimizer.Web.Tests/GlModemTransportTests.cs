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
===GLVER===
4.8.5
===BOARD===
{
        "kernel": "5.15.170-perf",
        "hostname": "GL-E5800",
        "model": "GL.iNet E5800, Qualcomm Technologies, Inc. SDXPINN IDP MBB",
        "board_name": "qcom,sdxpinn-idp",
        "release": {
                "distribution": "OpenWrt",
                "version": "23.05.4",
                "target": "sdx75/generic",
                "description": "OpenWrt 23.05.4 r24012-d8dd03c46f"
        }
}
""";

    private const string UsbOnlyDiscovery = """
===INFO===
===STATUS===
===GLVER===
===BOARD===
{
        "release": {
                "distribution": "OpenWrt",
                "version": "23.05.4",
                "description": "OpenWrt 23.05.4 r24012-d8dd03c46f"
        }
}
===MHI===
no
""";

    // XE3000P01 with RM520N-GL on PCIe/MHI: ubus returns valid JSON with all fields empty,
    // and /dev/mhi_DUN is the AT port. Trimmed from a real device running firmware 4.9.0.
    private const string MhiModemDiscovery = """
===INFO===
{
        "bus": "",
        "name": "",
        "version": "",
        "vendor": "",
        "sim_slot_num": 0,
        "signal_support": false,
        "sms_support": false
}
===STATUS===
{
        "bus": "",
        "status": 0,
        "current_sim_slot": "",
        "slot_switch_status": 0,
        "slot_switch_count": 0
}
===GLVER===
4.9.0
===BOARD===
{
        "kernel": "5.4.211",
        "hostname": "GL-XE3000P01",
        "system": "ARMv8 Processor rev 4",
        "model": "GL.iNet GL-XE3000",
        "board_name": "glinet,xe3000-emmc",
        "release": {
                "distribution": "OpenWrt",
                "version": "21.02-SNAPSHOT",
                "target": "mediatek/mt7981",
                "description": "OpenWrt 21.02-SNAPSHOT "
        }
}
===MHI===
yes
""";

    [Fact]
    public void ParseDiscovery_IntegratedModem_UsesCpuBusAndCurrentSimSlot()
    {
        var endpoint = GlModemTransport.ParseDiscovery(IntegratedModemDiscovery, configuredBus: "");

        endpoint.Bus.Should().Be("cpu");
        endpoint.Sub.Should().Be(1);
        endpoint.Model.Should().Be("RG650V-NA");
        endpoint.Vendor.Should().Be("Quectel", "GL reports it lowercase; the stats and Test Connection must agree");
        endpoint.Description.Should().Be("Quectel RG650V-NA");
        endpoint.SoftwareVersion.Should().Be("QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005");
        endpoint.HostVersion.Should().Be("4.8.5", "GL stamps their own firmware version, which is what the owner sees");
        endpoint.Product.Should().Be("E5800", "the brand comes from the provider, so it is stripped from the model");
    }

    [Fact]
    public void ParseModuleFirmware_ReadsTheModulesOwnBuild()
    {
        var json = @"{
        ""bus"": ""cpu"",
        ""name"": ""RG650V-NA"",
        ""version"": ""QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005""
}";

        GlModemTransport.ParseModuleFirmware(json)
            .Should().Be("QRM650VNA01ACR02A04G8G_OCPU_RGH_01.005.01.005");
    }

    [Fact]
    public void ParseModuleFirmware_NoUbusAnswer_ReturnsNull()
    {
        // Firmware without GL's cellular ubus answers nothing here, and AT+CGMR covers it.
        GlModemTransport.ParseModuleFirmware("").Should().BeNull();
        GlModemTransport.ParseModuleFirmware(null).Should().BeNull();
    }

    [Fact]
    public void ParseDiscovery_NoGlVersionFile_FallsBackToTheOpenWrtBase()
    {
        var endpoint = GlModemTransport.ParseDiscovery(UsbOnlyDiscovery, configuredBus: "");

        endpoint.HostVersion.Should().Be("OpenWrt 23.05.4 r24012-d8dd03c46f");
    }

    [Fact]
    public void ParseDiscovery_NeitherSource_LeavesHostVersionUnset()
    {
        var endpoint = GlModemTransport.ParseDiscovery("===INFO===\n===STATUS===\n===USB===\n", configuredBus: "");

        endpoint.HostVersion.Should().BeNull();
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
    public void ParseDiscovery_NoUbusNoConfiguredBus_LeavesTheBusToGlModem()
    {
        var endpoint = GlModemTransport.ParseDiscovery(UsbOnlyDiscovery, configuredBus: "");

        endpoint.Bus.Should().BeNull("forcing -B at a guessed USB path addresses the hub a modem sits behind");
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

    [Fact]
    public void ParseDiscovery_MhiModem_UsesMhiDevice()
    {
        var endpoint = GlModemTransport.ParseDiscovery(MhiModemDiscovery, configuredBus: "");

        endpoint.IsMhi.Should().BeTrue();
        endpoint.MhiDevice.Should().Be("/dev/mhi_DUN");
        endpoint.Bus.Should().BeNull();
        endpoint.Sub.Should().BeNull();
        endpoint.HostVersion.Should().Be("4.9.0");
        endpoint.Product.Should().Be("GL-XE3000");
    }

    [Fact]
    public void ParseDiscovery_MhiModem_ConfiguredBusIgnored()
    {
        var endpoint = GlModemTransport.ParseDiscovery(MhiModemDiscovery, configuredBus: "1-1.2");

        endpoint.IsMhi.Should().BeTrue("MHI wins over a configured bus when gl_modem can't reach the modem");
        endpoint.MhiDevice.Should().Be("/dev/mhi_DUN");
    }

    [Fact]
    public void ParseDiscovery_EmptyUbusNoMhi_FallsThrough()
    {
        // Same as the MHI fixture but without /dev/mhi_DUN present.
        var output = MhiModemDiscovery.Replace("yes", "no");

        var endpoint = GlModemTransport.ParseDiscovery(output, configuredBus: "");

        endpoint.IsMhi.Should().BeFalse();
        endpoint.Bus.Should().BeNull();
        endpoint.Sub.Should().BeNull();
        endpoint.HostVersion.Should().Be("4.9.0");
        endpoint.Product.Should().Be("GL-XE3000");
    }

    [Fact]
    public void ParseDiscovery_EmptyUbusNoMhi_UsesConfiguredBus()
    {
        var output = MhiModemDiscovery.Replace("yes", "no");

        var endpoint = GlModemTransport.ParseDiscovery(output, configuredBus: "1-1.2");

        endpoint.IsMhi.Should().BeFalse();
        endpoint.Bus.Should().Be("1-1.2");
    }

    [Fact]
    public void BuildMhiCommand_WritesAndReadsFromDevice()
    {
        var command = GlModemTransport.BuildMhiCommand("/dev/mhi_DUN", "AT+QENG=\"servingcell\"");

        command.Should().Contain("> /dev/mhi_DUN");
        command.Should().Contain("< /dev/mhi_DUN");
        command.Should().Contain("AT+QENG=");
        command.Should().Contain("timeout");
    }

    [Theory]
    [InlineData("/dev/mhi_DUN; reboot")]
    [InlineData("/dev/../etc/passwd")]
    [InlineData("$(id)")]
    public void BuildMhiCommand_RejectsInjection(string device)
    {
        var act = () => GlModemTransport.BuildMhiCommand(device, "AT");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ParseDiscovery_BoardQueryUnsupported_StillResolvesTheModem()
    {
        // A GL device that answers about its modem but not its board: the sections that did
        // come back must still be used.
        var output = IntegratedModemDiscovery[..IntegratedModemDiscovery.IndexOf("===GLVER===")]
                     + "===GLVER===\n===BOARD===\n";

        var endpoint = GlModemTransport.ParseDiscovery(output, configuredBus: "");

        endpoint.Bus.Should().Be("cpu");
        endpoint.Sub.Should().Be(1);
        endpoint.Model.Should().Be("RG650V-NA");
        endpoint.HostVersion.Should().BeNull();
        endpoint.Product.Should().BeNull();
    }
}
