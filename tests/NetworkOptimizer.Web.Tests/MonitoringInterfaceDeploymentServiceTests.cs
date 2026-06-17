using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class MonitoringInterfaceDeploymentServiceTests
{
    private static MonitoringInterface Valid(int? vlan = null) => new()
    {
        Name = "modem0",
        WanIfName = "eth1",
        WanVlanId = vlan,
        TargetIp = "192.168.100.1",
        GatewayLocalIp = "192.168.100.2",
        SubnetPrefix = 24,
        WatchdogIntervalMinutes = 5,
    };

    [Fact]
    public void Validate_NoVlan_Ok()
        => MonitoringInterfaceDeploymentService.Validate(Valid()).Should().BeNull();

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4094)]
    public void Validate_VlanInRange_Ok(int vlan)
        => MonitoringInterfaceDeploymentService.Validate(Valid(vlan)).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4095)]
    [InlineData(9999)]
    public void Validate_VlanOutOfRange_Rejected(int vlan)
        => MonitoringInterfaceDeploymentService.Validate(Valid(vlan)).Should().Contain("VLAN");

    [Theory]
    [InlineData("1")]
    [InlineData("192.168.100")]
    [InlineData("192.168.100.1.1")]
    public void Validate_ShorthandOrMalformedTargetIp_Rejected(string targetIp)
    {
        var mi = Valid();
        mi.TargetIp = targetIp;
        MonitoringInterfaceDeploymentService.Validate(mi).Should().Contain("Modem/ONT IP");
    }

    [Fact]
    public void BootScript_NoVlan_LeavesVlanIdEmpty()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid());
        script.Should().Contain("VLAN_ID=\"\"");
        script.Should().Contain("WAN_IF=\"eth1\"");
    }

    [Fact]
    public void BootScript_WithVlan_SetsVlanIdAndRidesTheSubinterface()
    {
        var script = MonitoringInterfaceDeploymentService.GenerateBootScript(Valid(100));
        script.Should().Contain("VLAN_ID=\"100\"");
        // The subinterface is created from the physical port + VLAN id, and the macvlan
        // rides the resolved parent ($PARENT) rather than the bare port.
        script.Should().Contain("type vlan id \"$VLAN_ID\"");
        script.Should().Contain("link \"$PARENT\" type macvlan");
    }
}
