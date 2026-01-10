using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using Xunit;

using AuditSeverity = NetworkOptimizer.Audit.Models.AuditSeverity;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class VlanSubnetMismatchRuleTests
{
    private readonly VlanSubnetMismatchRule _rule;

    public VlanSubnetMismatchRuleTests()
    {
        _rule = new VlanSubnetMismatchRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("WIFI-VLAN-SUBNET-001");
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
        _rule.RuleName.Should().Be("VLAN Subnet Mismatch");
    }

    #endregion

    #region Skip Cases - No Override

    [Fact]
    public void Evaluate_NoVirtualNetworkOverride_ReturnsNull()
    {
        // Arrange - Device without override enabled should be skipped
        var network = CreateNetwork("10.1.0.0/24");
        var client = CreateWirelessClient(
            ip: "10.1.0.100",
            networkOverrideEnabled: false,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoClientIp_ReturnsNull()
    {
        // Arrange - Device with override but no IP should be skipped
        var network = CreateNetwork("10.5.0.0/24");
        var client = CreateWirelessClient(
            ip: null,
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoNetworkSubnet_ReturnsNull()
    {
        // Arrange - Network without subnet info should be skipped
        var network = new NetworkInfo { Id = "net1", Name = "Cameras", VlanId = 5, Purpose = NetworkPurpose.Security, Subnet = null };
        var client = CreateWirelessClient(
            ip: "10.5.0.100",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(
            ip: "10.5.0.142",
            networkOverrideEnabled: true,
            networkOverrideId: "cameras-net",
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IpMatchesSubnet_ClassB_ReturnsNull()
    {
        // Arrange - /16 subnet
        var network = CreateNetwork("172.16.0.0/16", vlanId: 10);
        var client = CreateWirelessClient(
            ip: "172.16.50.100",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IpMatchesSubnet_SmallSubnet_ReturnsNull()
    {
        // Arrange - /28 subnet
        var network = CreateNetwork("192.168.1.0/28", vlanId: 20);
        var client = CreateWirelessClient(
            ip: "192.168.1.10",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_FixedIpMatchesSubnet_ReturnsNull()
    {
        // Arrange - Uses fixed_ip when ip is empty
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateWirelessClient(
            ip: null,
            fixedIp: "10.5.0.70",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(
            ip: "10.3.0.64",  // IOT subnet IP
            networkOverrideEnabled: true,
            networkOverrideId: "cameras-net",
            network: camerasNetwork,
            clientName: "Front Door");
        var networks = new List<NetworkInfo> { camerasNetwork };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("WIFI-VLAN-SUBNET-001");
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
        var client = CreateWirelessClient(
            ip: null,
            fixedIp: "10.1.0.100",  // Wrong subnet
            useFixedIp: true,
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedAction.Should().Contain("Update fixed IP");
    }

    [Fact]
    public void Evaluate_IpOutsideSmallSubnet_ReturnsIssue()
    {
        // Arrange - /28 subnet (192.168.1.0-15), IP outside range
        var network = CreateNetwork("192.168.1.0/28", vlanId: 20);
        var client = CreateWirelessClient(
            ip: "192.168.1.20",  // Outside /28 range
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(
            ip: "10.3.0.64",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("clientIp");
        result.Metadata!["clientIp"].Should().Be("10.3.0.64");
        result.Metadata.Should().ContainKey("expectedSubnet");
        result.Metadata["expectedSubnet"].Should().Be("10.5.0.0/24");
        result.Metadata.Should().ContainKey("assignedVlan");
        result.Metadata["assignedVlan"].Should().Be(5);
        result.Metadata.Should().ContainKey("virtualNetworkOverrideEnabled");
        result.Metadata["virtualNetworkOverrideEnabled"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_IssueIncludesFixedIpInMetadata_WhenPresent()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5);
        var client = CreateWirelessClient(
            ip: "10.3.0.64",
            fixedIp: "10.3.0.64",
            useFixedIp: true,
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(
            ip: "10.3.0.64",
            fixedIp: "10.3.0.64",
            useFixedIp: true,
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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
        var client = CreateWirelessClient(
            ip: "10.3.0.64",
            useFixedIp: false,
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.RecommendedAction.Should().Contain("Reconnect");
        result.RecommendedAction.Should().Contain("DHCP");
    }

    [Fact]
    public void Evaluate_IssueIncludesClientDetails()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras");
        var client = CreateWirelessClient(
            ip: "10.3.0.64",
            networkOverrideEnabled: true,
            network: network,
            clientName: "Front Door Camera",
            clientMac: "AA:BB:CC:DD:EE:FF",
            apName: "AP-Outdoor");
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.ClientName.Should().Be("Front Door Camera");
        result.ClientMac.Should().Be("AA:BB:CC:DD:EE:FF");
        result.AccessPoint.Should().Be("AP-Outdoor");
        result.CurrentNetwork.Should().Be("Cameras");
        result.CurrentVlan.Should().Be(5);
        result.IsWireless.Should().BeTrue();
    }

    #endregion

    #region Network Lookup by VLAN

    [Fact]
    public void Evaluate_FindsNetworkByVlan_WhenNetworkNull()
    {
        // Arrange - Client has VLAN number but no network object
        var camerasNetwork = CreateNetwork("10.5.0.0/24", vlanId: 5, name: "Cameras", id: "cameras-net");
        var client = CreateWirelessClient(
            ip: "10.3.0.64",  // Wrong subnet
            networkOverrideEnabled: true,
            vlan: 5,
            network: null);  // No network set
        var networks = new List<NetworkInfo> { camerasNetwork };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().NotBeNull();
        result!.CurrentNetwork.Should().Be("Cameras");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_InvalidIpAddress_ReturnsNull()
    {
        // Arrange
        var network = CreateNetwork("10.5.0.0/24");
        var client = CreateWirelessClient(
            ip: "invalid-ip",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_InvalidSubnetFormat_ReturnsNull()
    {
        // Arrange - Malformed subnet
        var network = new NetworkInfo { Id = "net1", Name = "Test", VlanId = 5, Subnet = "invalid-subnet" };
        var client = CreateWirelessClient(
            ip: "10.5.0.100",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_EmptySubnet_ReturnsNull()
    {
        // Arrange
        var network = new NetworkInfo { Id = "net1", Name = "Test", VlanId = 5, Subnet = "" };
        var client = CreateWirelessClient(
            ip: "10.5.0.100",
            networkOverrideEnabled: true,
            network: network);
        var networks = new List<NetworkInfo> { network };

        // Act
        var result = _rule.Evaluate(client, networks);

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

    private static WirelessClientInfo CreateWirelessClient(
        string? ip = "10.5.0.100",
        string? fixedIp = null,
        bool useFixedIp = false,
        bool networkOverrideEnabled = false,
        string? networkOverrideId = null,
        int? vlan = null,
        NetworkInfo? network = null,
        string clientName = "Test Device",
        string clientMac = "00:11:22:33:44:55",
        string? apName = null)
    {
        var client = new UniFiClientResponse
        {
            Mac = clientMac,
            Name = clientName,
            Ip = ip ?? string.Empty,
            FixedIp = fixedIp,
            UseFixedIp = useFixedIp,
            IsWired = false,
            NetworkId = network?.Id ?? string.Empty,
            VirtualNetworkOverrideEnabled = networkOverrideEnabled,
            VirtualNetworkOverrideId = networkOverrideId ?? network?.Id,
            Vlan = vlan ?? network?.VlanId
        };

        var detection = new DeviceDetectionResult
        {
            Category = ClientDeviceCategory.Camera,
            Source = DetectionSource.UniFiFingerprint,
            ConfidenceScore = 90
        };

        return new WirelessClientInfo
        {
            Client = client,
            Network = network,
            Detection = detection,
            AccessPointName = apName
        };
    }

    #endregion
}
