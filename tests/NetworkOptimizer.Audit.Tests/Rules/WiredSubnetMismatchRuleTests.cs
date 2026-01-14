using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class WiredSubnetMismatchRuleTests
{
    private readonly WiredSubnetMismatchRule _rule;

    public WiredSubnetMismatchRuleTests()
    {
        _rule = new WiredSubnetMismatchRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("PORT-SUBNET-001");
    }

    [Fact]
    public void Severity_IsCritical()
    {
        _rule.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void ScoreImpact_Is10()
    {
        _rule.ScoreImpact.Should().Be(10);
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("Wired Subnet Mismatch");
    }

    #endregion

    #region Skip Cases - No Client or Wrong Port Type

    [Fact]
    public void Evaluate_NoConnectedClient_ReturnsNull()
    {
        // Arrange - Port without a connected client should be skipped
        var network = CreateNetwork("10.1.0.0/24");
        var port = CreatePort(network, connectedClient: null);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange - Uplink ports should be skipped
        var network = CreateNetwork("10.1.0.0/24");
        var client = CreateClient(ip: "10.1.0.100");
        var port = CreatePort(network, connectedClient: client, isUplink: true);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        // Arrange - WAN ports should be skipped
        var network = CreateNetwork("10.1.0.0/24");
        var client = CreateClient(ip: "10.1.0.100");
        var port = CreatePort(network, connectedClient: client, isWan: true);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ReturnsNull()
    {
        // Arrange - Trunk ports should be skipped
        var network = CreateNetwork("10.1.0.0/24");
        var client = CreateClient(ip: "10.1.0.100");
        var port = CreatePort(network, connectedClient: client, forwardMode: "all");
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoClientIp_ReturnsNull()
    {
        // Arrange - Client with no IP should be skipped
        var network = CreateNetwork("10.5.0.0/24");
        var client = CreateClient(ip: null);
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoNetworkSubnet_ReturnsNull()
    {
        // Arrange - Network without subnet info should be skipped
        var network = new NetworkInfo { Id = "net1", Name = "Cameras", VlanId = 5, Purpose = NetworkPurpose.Security, Subnet = null };
        var client = CreateClient(ip: "10.5.0.100");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoNetworkFound_ReturnsNull()
    {
        // Arrange - Port with network ID that doesn't exist
        var network = CreateNetwork("10.5.0.0/24", id: "net1");
        var client = CreateClient(ip: "10.5.0.100");
        var port = CreatePort(network, connectedClient: client);
        port = CreatePortWithDifferentNetworkId(port, "nonexistent-net-id");
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region IP Matches Subnet - No Issue

    [Fact]
    public void Evaluate_IpMatchesSubnet_ReturnsNull()
    {
        // Arrange - Device with correct IP for its VLAN
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateClient(ip: "10.5.0.142");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IpMatchesSubnet_ClassB_ReturnsNull()
    {
        // Arrange - /16 subnet
        var network = CreateNetwork("172.16.0.0/16", vlanId: 10);
        var client = CreateClient(ip: "172.16.50.100");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IpMatchesSubnet_SmallSubnet_ReturnsNull()
    {
        // Arrange - /28 subnet
        var network = CreateNetwork("192.168.1.0/28", vlanId: 20);
        var client = CreateClient(ip: "192.168.1.10");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FixedIpMatchesSubnet_ReturnsNull()
    {
        // Arrange - Uses fixed_ip when ip is empty
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: null, fixedIp: "10.5.0.70");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region IP Does NOT Match Subnet - Issue Found

    [Fact]
    public void Evaluate_IpDoesNotMatchSubnet_ReturnsIssue()
    {
        // Arrange - Device on Cameras VLAN but with IOT subnet IP
        var camerasNetwork = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras", id: "cameras-net");
        var client = CreateClient(ip: "10.3.0.64", name: "Front Door");  // IOT subnet IP
        var port = CreatePort(camerasNetwork, connectedClient: client);
        var networks = new List<NetworkInfo> { camerasNetwork };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-SUBNET-001");
        result.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(10);
        result.Message.Should().Contain("10.3.0.64");
        result.Message.Should().Contain("10.5.0.0/24");
    }

    [Fact]
    public void Evaluate_FixedIpDoesNotMatchSubnet_ReturnsIssue()
    {
        // Arrange - Stale fixed IP from previous VLAN
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateClient(ip: null, fixedIp: "10.1.0.100", useFixedIp: true);  // Wrong subnet
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedAction.Should().Contain("Update fixed IP");
    }

    [Fact]
    public void Evaluate_IpOutsideSmallSubnet_ReturnsIssue()
    {
        // Arrange - /28 subnet (192.168.1.0-15), IP outside range
        var network = CreateNetwork("192.168.1.0/28", vlanId: 20);
        var client = CreateClient(ip: "192.168.1.20");  // Outside /28 range
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
    }

    #endregion

    #region Issue Details

    [Fact]
    public void Evaluate_IssueIncludesMetadata()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateClient(ip: "10.3.0.64");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("clientIp");
        result.Metadata!["clientIp"].Should().Be("10.3.0.64");
        result.Metadata.Should().ContainKey("expectedSubnet");
        result.Metadata["expectedSubnet"].Should().Be("10.5.0.0/24");
        result.Metadata.Should().ContainKey("assignedVlan");
        result.Metadata["assignedVlan"].Should().Be(5);
    }

    [Fact]
    public void Evaluate_IssueIncludesFixedIpInMetadata_WhenPresent()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", fixedIp: "10.3.0.64", useFixedIp: true);
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("hasFixedIp");
        result.Metadata!["hasFixedIp"].Should().Be(true);
        result.Metadata.Should().ContainKey("fixedIp");
        result.Metadata["fixedIp"].Should().Be("10.3.0.64");
    }

    [Fact]
    public void Evaluate_RecommendedAction_ForFixedIp()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateClient(ip: "10.3.0.64", fixedIp: "10.3.0.64", useFixedIp: true);
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedAction.Should().Contain("Update fixed IP");
        result.RecommendedAction.Should().Contain("10.5.0.0/24");
    }

    [Fact]
    public void Evaluate_RecommendedAction_ForDhcp()
    {
        // Arrange - No fixed IP, just DHCP
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", useFixedIp: false);
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedAction.Should().Contain("Reconnect");
        result.RecommendedAction.Should().Contain("DHCP");
    }

    [Fact]
    public void Evaluate_IssueIncludesPortDetails()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateClient(ip: "10.3.0.64", name: "Test Device");
        var port = CreatePort(network, connectedClient: client, portIndex: 7, portName: "Camera Port");
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Port.Should().Be("7");
        result.PortName.Should().Be("Camera Port");
        result.CurrentNetwork.Should().Be("Cameras");
        result.CurrentVlan.Should().Be(5);
    }

    [Fact]
    public void Evaluate_DeviceName_UsesClientName()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", name: "My Device");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("My Device");
    }

    [Fact]
    public void Evaluate_DeviceName_UsesHostname_WhenNoName()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", name: null, hostname: "device-hostname");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("device-hostname");
    }

    [Fact]
    public void Evaluate_DeviceName_UsesOuiAndMac_WhenNoNameOrHostname()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", name: null, hostname: null, mac: "AA:BB:CC:DD:EE:FF", oui: "Ubiquiti");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("Ubiquiti");
        result.DeviceName.Should().Contain("EE:FF");
    }

    [Fact]
    public void Evaluate_DeviceName_UsesMac_WhenNoOtherInfo()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateClient(ip: "10.3.0.64", name: null, hostname: null, mac: "AA:BB:CC:DD:EE:FF", oui: null);
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Contain("AA:BB:CC:DD:EE:FF");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_InvalidIpAddress_ReturnsNull()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24");
        var client = CreateClient(ip: "invalid-ip");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_InvalidSubnetFormat_ReturnsNull()
    {
        // Arrange - Malformed subnet
        var network = new NetworkInfo { Id = "net1", Name = "Test", VlanId = 5, Subnet = "invalid-subnet" };
        var client = CreateClient(ip: "10.5.0.100");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_EmptySubnet_ReturnsNull()
    {
        // Arrange
        var network = new NetworkInfo { Id = "net1", Name = "Test", VlanId = 5, Subnet = "" };
        var client = CreateClient(ip: "10.5.0.100");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IPv6Address_ReturnsNull()
    {
        // Arrange - IPv6 not supported yet
        var network = CreateNetwork("10.5.0.0/24");
        var client = CreateClient(ip: "2001:db8::1");
        var port = CreatePort(network, connectedClient: client);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Helper Methods

    private static NetworkInfo CreateNetwork(
        string subnet,
        int vlanId = 5,
        string name = "Test Network",
        string id = "net-id",
        NetworkPurpose purpose = NetworkPurpose.Security)
    {
        return new NetworkInfo
        {
            Id = id,
            Name = name,
            VlanId = vlanId,
            Subnet = subnet,
            Purpose = purpose
        };
    }

    private static UniFiClientResponse CreateClient(
        string? ip = "10.5.0.100",
        string? fixedIp = null,
        bool useFixedIp = false,
        string? name = "Test Device",
        string? hostname = null,
        string mac = "00:11:22:33:44:55",
        string? oui = null)
    {
        return new UniFiClientResponse
        {
            Mac = mac,
            Name = name,
            Hostname = hostname,
            Oui = oui,
            Ip = ip ?? string.Empty,
            FixedIp = fixedIp,
            UseFixedIp = useFixedIp,
            IsWired = true
        };
    }

    private static SwitchInfo CreateSwitch(string name = "Switch 1", string mac = "AA:BB:CC:DD:EE:00")
    {
        return new SwitchInfo
        {
            Name = name,
            MacAddress = mac,
            Model = "USW-24",
            Ports = new List<PortInfo>()
        };
    }

    private static PortInfo CreatePort(
        NetworkInfo network,
        UniFiClientResponse? connectedClient = null,
        int portIndex = 1,
        string? portName = null,
        bool isUplink = false,
        bool isWan = false,
        string forwardMode = "native")
    {
        var switchInfo = CreateSwitch();
        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName ?? $"Port {portIndex}",
            IsUp = connectedClient != null,
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = network.Id,
            ConnectedClient = connectedClient,
            Switch = switchInfo
        };
    }

    private static PortInfo CreatePortWithDifferentNetworkId(PortInfo original, string networkId)
    {
        return new PortInfo
        {
            PortIndex = original.PortIndex,
            Name = original.Name,
            IsUp = original.IsUp,
            ForwardMode = original.ForwardMode,
            IsUplink = original.IsUplink,
            IsWan = original.IsWan,
            NativeNetworkId = networkId,
            ConnectedClient = original.ConnectedClient,
            Switch = original.Switch
        };
    }

    #endregion
}
