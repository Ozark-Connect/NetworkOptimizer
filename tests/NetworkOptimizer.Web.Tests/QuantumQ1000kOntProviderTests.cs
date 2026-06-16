using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class QuantumQ1000kOntProviderTests
{
    [Fact]
    public void ApplyConnectionStatus_ConnectedGponRates_SetsUpAndGpon()
    {
        var stats = new OntStats();
        var json = """
            {"wan_status":{"device":"pon","connname":"wan","connifname":"pon",
            "ipaddr":"203.0.113.5/24","link_state":"connected","net_state":"bridged",
            "type":"2","uplink_rate":"1244000","downlink_rate":"2488000",
            "is_bridged":"false","is_walled_pppcred":"false","is_captived":"false"}}
            """;

        QuantumQ1000kOntProvider.ApplyConnectionStatus(json, stats);

        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Up");
        stats.PonType.Should().Be("GPON");
    }

    [Fact]
    public void ApplyConnectionStatus_DisconnectedLink_SetsDown()
    {
        var stats = new OntStats();
        var json = """{"wan_status":{"link_state":"disconnected","downlink_rate":"0"}}""";

        QuantumQ1000kOntProvider.ApplyConnectionStatus(json, stats);

        stats.OperationalStatus.Should().Be("Down");
        stats.LinkState.Should().Be("Down");
    }

    [Fact]
    public void ApplyConnectionStatus_TenGigDownstream_SetsXgsPon()
    {
        var stats = new OntStats();
        var json = """{"wan_status":{"link_state":"connected","downlink_rate":"9953000"}}""";

        QuantumQ1000kOntProvider.ApplyConnectionStatus(json, stats);

        stats.PonType.Should().Be("XGS-PON");
    }

    [Fact]
    public void ApplyDeviceInfo_ParsesModelAndSerial()
    {
        var stats = new OntStats { DeviceModel = "Quantum Q1000K" };
        var json = """
            {"Objects":[{"ObjName":"Device.DeviceInfo.","Param":[
            {"ParamName":"HardwareVersion","ParamValue":"1.0"},
            {"ParamName":"ModelName","ParamValue":"Q1000K"},
            {"ParamName":"SerialNumber","ParamValue":"ABC123456789"},
            {"ParamName":"SoftwareVersion","ParamValue":"QKX001-06.00.44.00"}]}]}
            """;

        QuantumQ1000kOntProvider.ApplyDeviceInfo(json, stats);

        stats.DeviceModel.Should().Be("Quantum Q1000K");
        stats.VendorPn.Should().Be("Q1000K");
        stats.VendorSn.Should().Be("ABC123456789");
    }

    [Fact]
    public void ApplyOpticalInterface_GoodLineStatus_SetsUpWhenLinkUnknown()
    {
        var stats = new OntStats();
        var json = """
            {"Objects":[{"ObjName":"Device.Optical.Interface.1.","Param":[
            {"ParamName":"X_AXON_LineStatus","ParamValue":"GOOD"}]}]}
            """;

        QuantumQ1000kOntProvider.ApplyOpticalInterface(json, stats);

        stats.LinkState.Should().Be("Up");
        stats.OperationalStatus.Should().Be("Up");
    }

    [Fact]
    public void ApplyOpticalInterface_DoesNotOverrideExistingLinkState()
    {
        var stats = new OntStats { LinkState = "Down", OperationalStatus = "Down" };
        var json = """
            {"Objects":[{"ObjName":"Device.Optical.Interface.1.","Param":[
            {"ParamName":"X_AXON_LineStatus","ParamValue":"GOOD"}]}]}
            """;

        QuantumQ1000kOntProvider.ApplyOpticalInterface(json, stats);

        stats.LinkState.Should().Be("Down");
        stats.OperationalStatus.Should().Be("Down");
    }

    [Fact]
    public void Parsers_InvalidJson_DoNotThrow()
    {
        var stats = new OntStats();
        var act = () =>
        {
            QuantumQ1000kOntProvider.ApplyConnectionStatus("not json", stats);
            QuantumQ1000kOntProvider.ApplyDeviceInfo("{}", stats);
            QuantumQ1000kOntProvider.ApplyOpticalInterface("[]", stats);
        };

        act.Should().NotThrow();
    }
}
