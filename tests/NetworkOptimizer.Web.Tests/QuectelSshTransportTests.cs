using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.CellularModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// Tests for the generic Quectel-over-SSH transport and provider (#1269). The ATI answer follows
/// the Quectel AT manual's shape with the RM551E-GL strings from the issue; the AT+QENG, AT+COPS?,
/// and AT+CGMR answers reuse the shapes in QuectelAtParserTests. Replace with the reporter's raw
/// output once it is posted.
/// </summary>
public class QuectelSshTransportTests
{
    private const string Rm551eAti = "ATI\r\nQuectel\r\nRM551E-GL\r\nRevision: RM551EGLAAR01A01M4G\r\n\r\nOK\r\n";

    // Non-GL OpenWrt 15.05 with a PCIe module: board call has only a release, no GL ubus.
    private const string MhiDiscovery =
        "===BOARD===\n" +
        "{\n\t\"release\": {\n\t\t\"distribution\": \"OpenWrt\",\n\t\t\"version\": \"15.05.1\",\n" +
        "\t\t\"description\": \"OpenWrt Chaos Calmer 15.05.1\"\n\t}\n}\n" +
        "===RELEASE===\n" +
        "DISTRIB_ID='OpenWrt'\nDISTRIB_RELEASE='15.05.1'\nDISTRIB_DESCRIPTION='OpenWrt Chaos Calmer 15.05.1'\n" +
        "===PORT:/dev/mhi_DUN===\n" + Rm551eAti;

    #region Port discovery

    [Fact]
    public void CandidatePorts_ProbeMhiThenWwanThenUsbThenSmd_AndNeverDmOrNmea()
    {
        QuectelSshTransport.CandidatePorts.Should().Equal(
            "/dev/mhi_DUN", "/dev/wwan0at0", "/dev/wwan0at1", "/dev/ttyUSB2", "/dev/ttyUSB3",
            "/dev/smd9", "/dev/smd8", "/dev/smd7", "/dev/smd11");
        QuectelSshTransport.CandidatePorts.Should().NotContain(new[] { "/dev/ttyUSB0", "/dev/ttyUSB1" });
    }

    [Fact]
    public void BuildDiscoveryScript_ProbesEachCandidateInOrder_AndStopsAtTheFirstQuectel()
    {
        var script = QuectelSshTransport.BuildDiscoveryScript(configuredPort: null);

        script.Should().StartWith(AtDeviceShell.TimeoutPrelude + "; echo '===BOARD==='; ubus call system board 2>/dev/null; " +
                                  "echo '===RELEASE==='; cat /etc/openwrt_release 2>/dev/null; ");

        var positions = QuectelSshTransport.CandidatePorts
            .Select(d => script.IndexOf($"if [ -c {d} ]; then echo '===PORT:{d}==='", StringComparison.Ordinal))
            .ToList();
        positions.Should().NotContain(-1);
        positions.Should().BeInAscendingOrder();
        script.Should().Contain("case \"$r\" in *Quectel*OK*) exit 0;; esac; fi");
        script.Should().NotContain("kill -STOP", "a vendor daemon holding the port is shared, never stopped");
    }

    [Fact]
    public void BuildDiscoveryScript_ConfiguredPort_ProbesOnlyThatPort()
    {
        var script = QuectelSshTransport.BuildDiscoveryScript("/dev/ttyUSB3");

        script.Should().EndWith(
            "if [ -c /dev/ttyUSB3 ]; then echo '===PORT:/dev/ttyUSB3==='; " +
            "r=$(" + AtDeviceShell.BuildSingleOpenCommand("/dev/ttyUSB3", "ATI") + "); echo \"$r\"; " +
            "case \"$r\" in *Quectel*OK*) exit 0;; esac; fi");
        script.Should().NotContain("/dev/mhi_DUN");
    }

    [Fact]
    public void ParseDiscovery_MhiRouter_TakesMhiWithIdentityAndOpenWrtVersion()
    {
        var discovery = QuectelSshTransport.ParseDiscovery(MhiDiscovery, configuredPort: "");

        discovery.PresentPorts.Should().Equal("/dev/mhi_DUN");
        discovery.Endpoint.Should().Be(new QuectelSshEndpoint(
            "/dev/mhi_DUN", "Quectel", "RM551E-GL", "RM551EGLAAR01A01M4G",
            HostModel: null, HostVersion: "OpenWrt Chaos Calmer 15.05.1"));
        discovery.Endpoint!.Description.Should().Be("Quectel RM551E-GL");
    }

    [Fact]
    public void ParseDiscovery_SkipsAPortThatDidNotAnswerAsQuectel()
    {
        // ttyUSB2 timed out mid-answer (no OK); ttyUSB3 answered in full.
        var output = "===BOARD===\n===RELEASE===\n" +
                     "===PORT:/dev/ttyUSB2===\nQuectel\r\n" +
                     "===PORT:/dev/ttyUSB3===\n" + Rm551eAti;

        var discovery = QuectelSshTransport.ParseDiscovery(output, configuredPort: null);

        discovery.PresentPorts.Should().Equal("/dev/ttyUSB2", "/dev/ttyUSB3");
        discovery.Endpoint!.Device.Should().Be("/dev/ttyUSB3");
    }

    [Fact]
    public void ParseDiscovery_NoPortAnswered_ReturnsNoEndpointButListsPresentPorts()
    {
        var output = "===BOARD===\n===RELEASE===\n===PORT:/dev/ttyUSB2===\n";

        var discovery = QuectelSshTransport.ParseDiscovery(output, configuredPort: null);

        discovery.Endpoint.Should().BeNull();
        discovery.PresentPorts.Should().Equal("/dev/ttyUSB2");
    }

    [Fact]
    public void ParseDiscovery_ConfiguredPort_IsUsedEvenWithoutAnIdentity()
    {
        var output = "===BOARD===\n===RELEASE===\n===PORT:/dev/smd8===\n\r\nERROR\r\n";

        var discovery = QuectelSshTransport.ParseDiscovery(output, configuredPort: "/dev/smd8");

        discovery.Endpoint.Should().Be(new QuectelSshEndpoint("/dev/smd8"));
    }

    [Fact]
    public void ParseDiscovery_ConfiguredPortMissing_ReturnsNoEndpoint()
    {
        var discovery = QuectelSshTransport.ParseDiscovery("===BOARD===\n===RELEASE===\n", configuredPort: "/dev/smd8");

        discovery.Endpoint.Should().BeNull();
        discovery.PresentPorts.Should().BeEmpty();
    }

    [Fact]
    public void ParseDiscovery_BoardModel_IsTheProductBeforeTheComma()
    {
        // Board model string from the GL-E5800 fixture in GlModemTransportTests.
        var output = "===BOARD===\n{ \"model\": \"GL.iNet E5800, Qualcomm Technologies, Inc. SDXPINN IDP MBB\", " +
                     "\"release\": { \"description\": \"OpenWrt 23.05.4 r24012-d8dd03c46f\" } }\n" +
                     "===RELEASE===\n===PORT:/dev/smd9===\n" + Rm551eAti;

        var endpoint = QuectelSshTransport.ParseDiscovery(output, configuredPort: null).Endpoint!;

        endpoint.HostModel.Should().Be("GL.iNet E5800");
        endpoint.HostVersion.Should().Be("OpenWrt 23.05.4 r24012-d8dd03c46f");
    }

    [Fact]
    public void ParseDiscovery_SocReferenceBoardModel_IsDropped()
    {
        // #1269: the CP520's board model starts with the SoC maker, which names no product.
        var output = "===BOARD===\n{ \"model\": \"Qualcomm Technologies, Inc. Reference Board\" }\n" +
                     "===RELEASE===\n===PORT:/dev/mhi_DUN===\n" + Rm551eAti;

        QuectelSshTransport.ParseDiscovery(output, configuredPort: null).Endpoint!.HostModel.Should().BeNull();
    }

    [Fact]
    public void ParseDiscovery_NoBoardCall_FallsBackToOpenWrtRelease()
    {
        var output = "===BOARD===\n===RELEASE===\nDISTRIB_DESCRIPTION=\"OpenWrt Chaos Calmer 15.05.1\"\n" +
                     "===PORT:/dev/mhi_DUN===\n" + Rm551eAti;

        QuectelSshTransport.ParseDiscovery(output, configuredPort: null).Endpoint!.HostVersion
            .Should().Be("OpenWrt Chaos Calmer 15.05.1");
    }

    [Fact]
    public void NoPortMessage_NamesWhatWasCheckedOrFound()
    {
        QuectelSshTransport.NoPortMessage(Array.Empty<string>(), "", "192.0.2.1")
            .Should().StartWith("No modem AT port was found on 192.0.2.1. Checked /dev/mhi_DUN, ");
        QuectelSshTransport.NoPortMessage(new[] { "/dev/ttyUSB2" }, "", "192.0.2.1")
            .Should().StartWith("No Quectel module answered ATI on /dev/ttyUSB2.");
        QuectelSshTransport.NoPortMessage(Array.Empty<string>(), "/dev/smd8", "192.0.2.1")
            .Should().Be("AT Port /dev/smd8 does not exist on 192.0.2.1.");
    }

    #endregion

    #region Command builders

    [Fact]
    public void BuildCommand_MhiKeepsTheTwoOpenFormWithTheTimeoutFallback()
    {
        QuectelSshTransport.BuildCommand("/dev/mhi_DUN", "AT+CGMR")
            .Should().Be(AtDeviceShell.BuildTwoOpenCommand("/dev/mhi_DUN", "AT+CGMR", "_to"));
    }

    [Theory]
    [InlineData("/dev/wwan0at0")]
    [InlineData("/dev/ttyUSB2")]
    [InlineData("/dev/smd9")]
    public void BuildCommand_OtherPortsOpenOnce(string device)
    {
        QuectelSshTransport.BuildCommand(device, "AT+CGMR")
            .Should().Be(AtDeviceShell.BuildSingleOpenCommand(device, "AT+CGMR"));
    }

    [Fact]
    public void BuildBatchScript_DefinesTheFallbackOnce_AndMarksEachCommand()
    {
        var script = QuectelSshTransport.BuildBatchScript("/dev/mhi_DUN", new[] { "AT+QENG=\"servingcell\"", "AT+COPS?" });

        script.Should().Be(
            AtDeviceShell.TimeoutPrelude +
            "; echo '===AT0==='; " + QuectelSshTransport.BuildCommand("/dev/mhi_DUN", "AT+QENG=\"servingcell\"") +
            "; echo '===AT1==='; " + QuectelSshTransport.BuildCommand("/dev/mhi_DUN", "AT+COPS?"));
        script.Should().NotContain("timeout 3", "the read always goes through _to, which picks timeout only when it works");
    }

    #endregion

    #region Provider

    private static QuectelSshAtResult PollResult(string servingCell) => new()
    {
        Success = true,
        Endpoint = new QuectelSshEndpoint("/dev/mhi_DUN", "Quectel", "RM551E-GL", "RM551EGLAAR01A01M4G",
            HostModel: null, HostVersion: "OpenWrt Chaos Calmer 15.05.1"),
        Sections = new Dictionary<string, string>
        {
            ["AT+QENG=\"servingcell\""] = servingCell,
            ["AT+COPS?"] = "\r\n+COPS: 0,0,\"T-Mobile\",13\r\n\r\nOK\r\n",
            ["AT+CGMR"] = "AT+CGMR\r\nRM551EGLAAR01A01M4G\r\n\r\nOK\r\n",
        },
    };

    [Fact]
    public void BuildStats_EndToEnd_ParsesSignalOperatorFirmwareAndIdentity()
    {
        // NR5G-SA line from QuectelAtParserTests.Parse_Nr5gSa_ParsesAllFields.
        var servingCell = "AT+QENG=\"servingcell\"\r\n" +
            "+QENG: \"servingcell\",\"NOCONN\",\"NR5G-SA\",\"FDD\",\"001\",\"01\",\"1A2B3C\",286,\"1234\",125530,\"n71\",20,-106,-19,-8.0,15,0\r\n" +
            "OK\r\n";

        var stats = QuectelSshModemProvider.BuildStats(PollResult(servingCell), "192.0.2.1", "Backup", "Quectel");

        stats.Should().NotBeNull();
        stats!.NetworkMode.Should().Be(CellularNetworkMode.Nr5gSa);
        stats.Nr5g!.Rsrp.Should().Be(-106);
        stats.ActiveBand!.BandClass.Should().Be("n71");
        stats.Carrier.Should().Be("T-Mobile");
        stats.SoftwareVersion.Should().Be("RM551EGLAAR01A01M4G");
        stats.ModemModel.Should().Be("RM551E-GL", "the card shows the module as the device");
        stats.ModuleVendor.Should().Be("Quectel");
        stats.ModuleModel.Should().Be("RM551E-GL");
        stats.HostVersion.Should().Be("OpenWrt Chaos Calmer 15.05.1");
        stats.HostModel.Should().BeNull();
    }

    [Fact]
    public void BuildStats_NoSignalData_ReturnsNull()
    {
        // What the GL provider saw in #1269 when timeout was missing: only the markers came back.
        QuectelSshModemProvider.BuildStats(PollResult(""), "192.0.2.1", "Backup", "Quectel").Should().BeNull();
    }

    [Fact]
    public void BuildStats_CgmrSilent_FallsBackToTheAtiRevision()
    {
        var servingCell = "+QENG: \"servingcell\",\"NOCONN\",\"LTE\",\"FDD\",001,01,\"0A1B2C3\",286,700,2,5,20,\"1234\",-99,-10,-68,19,12,30,0\r\nOK\r\n";
        var result = PollResult(servingCell);
        result.Sections["AT+CGMR"] = "";

        QuectelSshModemProvider.BuildStats(result, "192.0.2.1", "Backup", "Quectel")!.SoftwareVersion
            .Should().Be("RM551EGLAAR01A01M4G");
    }

    #endregion

    #region Provider key

    [Fact]
    public void ProviderKey_MatchesTheSettingsKey()
    {
        // ProviderKey reads no state, so an uninitialized instance is enough to check the wiring.
        var provider = (QuectelSshModemProvider)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(QuectelSshModemProvider));

        provider.ProviderKey.Should().Be("quectel-ssh");
        provider.DisplayName.Should().Be("Quectel modem (SSH)");
    }

    #endregion
}
