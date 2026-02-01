using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class AccessPortVlanRuleTests
{
    private readonly AccessPortVlanRule _rule;

    public AccessPortVlanRuleTests()
    {
        _rule = new AccessPortVlanRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("ACCESS-VLAN-001");
    }

    [Fact]
    public void RuleName_ReturnsExpectedValue()
    {
        _rule.RuleName.Should().Be("Access Port VLAN Exposure");
    }

    [Fact]
    public void Severity_IsCritical()
    {
        _rule.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void ScoreImpact_Is8()
    {
        _rule.ScoreImpact.Should().Be(8);
    }

    #endregion

    #region Ports That Should Be Skipped

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        var port = CreatePortWithClient(isUplink: true);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        var port = CreatePortWithClient(isWan: true);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("uap")]   // Access Point
    [InlineData("usw")]   // Switch
    [InlineData("ugw")]   // Gateway
    [InlineData("usg")]   // Security Gateway
    [InlineData("udm")]   // Dream Machine
    [InlineData("uxg")]   // Next-Gen Gateway
    [InlineData("ucg")]   // Cloud Gateway
    [InlineData("ubb")]   // Building-to-Building Bridge
    public void Evaluate_NetworkFabricDeviceConnected_ReturnsNull(string deviceType)
    {
        // Network fabric devices legitimately need multiple VLANs
        var port = CreatePortWithClient(connectedDeviceType: deviceType, excludedNetworkIds: null);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoConnectedClientAndNoOfflineData_ReturnsNull()
    {
        // No evidence of a single device - could be unused or trunk
        var port = CreatePort(excludedNetworkIds: null); // Allow All, but no device data
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoVlanNetworks_ReturnsNull()
    {
        var port = CreatePortWithClient(excludedNetworkIds: null);
        var networks = new List<NetworkInfo>(); // No VLANs

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_OnlyVlan0Networks_ReturnsNull()
    {
        var port = CreatePortWithClient(excludedNetworkIds: null);
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-0", Name = "Default", VlanId = 0 }
        };

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region VLAN Count Threshold Tests

    [Fact]
    public void Evaluate_OneTaggedVlan_ReturnsNull()
    {
        // 1 VLAN is fine (e.g., just native)
        var networks = CreateVlanNetworks(5);
        var excludeAllButOne = networks.Skip(1).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButOne);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TwoTaggedVlans_ReturnsNull()
    {
        // 2 VLANs is acceptable (native + voice)
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ThreeTaggedVlans_ReturnsIssue()
    {
        // 3 VLANs is excessive for a single device
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("tagged_vlan_count");
        result.Metadata!["tagged_vlan_count"].Should().Be(3);
        result.Metadata.Should().ContainKey("allows_all_vlans");
        result.Metadata["allows_all_vlans"].Should().Be(false);
    }

    [Fact]
    public void Evaluate_FiveTaggedVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: new List<string>()); // Allow all 5

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(5);
    }

    #endregion

    #region Allow All VLANs Detection

    [Fact]
    public void Evaluate_AllowAllVlans_NullExcludedList_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null); // null = Allow All

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
        result.Metadata["tagged_vlan_count"].Should().Be(5);
    }

    [Fact]
    public void Evaluate_AllowAllVlans_EmptyExcludedList_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: new List<string>()); // empty = Allow All

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
    }

    #endregion

    #region Single Device Detection - Connected Client

    [Fact]
    public void Evaluate_ConnectedClient_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_ConnectedClient_WithAcceptableVlans_ReturnsNull()
    {
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region Single Device Detection - Offline Data

    [Fact]
    public void Evaluate_LastConnectionMac_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithLastConnectionMac(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_AllowedMacAddresses_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithAllowedMacs(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_LastConnectionMac_WithAcceptableVlans_ReturnsNull()
    {
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreatePortWithLastConnectionMac(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region Endpoint Devices (Should Trigger)

    [Theory]
    [InlineData("umbb")]  // Modem
    [InlineData("uck")]   // Cloud Key
    [InlineData("unvr")]  // NVR
    [InlineData("uph")]   // Phone
    [InlineData(null)]    // Unknown/regular client
    [InlineData("")]      // Empty
    public void Evaluate_EndpointDeviceWithExcessiveVlans_ReturnsIssue(string? deviceType)
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(connectedDeviceType: deviceType, excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    #endregion

    #region Issue Details

    [Fact]
    public void Evaluate_IssueContainsCorrectRuleId()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Type.Should().Be("ACCESS-VLAN-001");
        result.RuleId.Should().Be("ACCESS-VLAN-001");
    }

    [Fact]
    public void Evaluate_IssueContainsCorrectSeverityAndScore()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Critical);
        result.ScoreImpact.Should().Be(8);
    }

    [Fact]
    public void Evaluate_IssueContainsPortDetails()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(
            portIndex: 7,
            portName: "Office Workstation",
            switchName: "Switch-Floor2",
            excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Port.Should().Be("7");
        result.PortName.Should().Be("Office Workstation");
        result.DeviceName.Should().Contain("Switch-Floor2");
    }

    [Fact]
    public void Evaluate_IssueContainsNetworkName()
    {
        var networks = CreateVlanNetworks(3);
        var port = CreatePortWithClient(
            nativeNetworkId: "net-1",
            excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("network");
        result.Metadata!["network"].Should().Be("VLAN 20");
    }

    [Fact]
    public void Evaluate_IssueContainsRecommendation()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("recommendation");
        ((string)result.Metadata!["recommendation"]).Should().Contain("native");
    }

    [Fact]
    public void Evaluate_IssueMessageDescribesAllVlans()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("all VLANs");
    }

    [Fact]
    public void Evaluate_IssueMessageDescribesVlanCount()
    {
        var networks = CreateVlanNetworks(5);
        var excludeTwo = networks.Take(2).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeTwo); // 3 VLANs allowed

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("3 VLANs");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_ExcludedNetworkNotInList_HandlesGracefully()
    {
        var networks = CreateVlanNetworks(5);
        var excludeWithUnknown = new List<string>
        {
            "net-0", // valid
            "unknown-network-id", // invalid - should be ignored
            "another-unknown"
        };
        var port = CreatePortWithClient(excludedNetworkIds: excludeWithUnknown);

        var result = _rule.Evaluate(port, networks);

        // 5 networks - 1 valid excluded = 4 VLANs (above threshold)
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(4);
    }

    [Fact]
    public void Evaluate_MixOfVlan0AndRealVlans_OnlyCountsRealVlans()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-default", Name = "Default", VlanId = 0 },
            new() { Id = "net-1", Name = "VLAN 10", VlanId = 10 },
            new() { Id = "net-2", Name = "VLAN 20", VlanId = 20 },
            new() { Id = "net-3", Name = "VLAN 30", VlanId = 30 }
        };
        var port = CreatePortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        // Only 3 real VLAN networks (10, 20, 30), which is above threshold
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(3);
    }

    [Fact]
    public void Evaluate_ExactlyAtThreshold_ReturnsNull()
    {
        // Threshold is 2, so exactly 2 VLANs should NOT trigger
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_JustAboveThreshold_ReturnsIssue()
    {
        // Threshold is 2, so 3 VLANs should trigger
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreatePortWithClient(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    #endregion

    #region Helper Methods

    private static List<NetworkInfo> CreateVlanNetworks(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new NetworkInfo
            {
                Id = $"net-{i}",
                Name = $"VLAN {(i + 1) * 10}",
                VlanId = (i + 1) * 10
            })
            .ToList();
    }

    private static PortInfo CreatePort(
        List<string>? excludedNetworkIds = null,
        bool isUplink = false,
        bool isWan = false,
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch",
        string? nativeNetworkId = null,
        string? connectedDeviceType = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Capabilities = new SwitchCapabilities()
        };

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = true,
            ForwardMode = "native",
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = nativeNetworkId,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedDeviceType = connectedDeviceType,
            ConnectedClient = null,
            LastConnectionMac = null,
            AllowedMacAddresses = null,
            Switch = switchInfo
        };
    }

    private static PortInfo CreatePortWithClient(
        List<string>? excludedNetworkIds = null,
        bool isUplink = false,
        bool isWan = false,
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch",
        string? nativeNetworkId = null,
        string? connectedDeviceType = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Capabilities = new SwitchCapabilities()
        };

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = true,
            ForwardMode = "native",
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = nativeNetworkId,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedDeviceType = connectedDeviceType,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Name = "Test Device"
            },
            LastConnectionMac = null,
            AllowedMacAddresses = null,
            Switch = switchInfo
        };
    }

    private static PortInfo CreatePortWithLastConnectionMac(
        List<string>? excludedNetworkIds = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = "Test Switch",
            Capabilities = new SwitchCapabilities()
        };

        return new PortInfo
        {
            PortIndex = 1,
            Name = "Port 1",
            IsUp = true,
            ForwardMode = "native",
            IsUplink = false,
            IsWan = false,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedClient = null,
            LastConnectionMac = "aa:bb:cc:dd:ee:ff", // Offline device data
            AllowedMacAddresses = null,
            Switch = switchInfo
        };
    }

    private static PortInfo CreatePortWithAllowedMacs(
        List<string>? excludedNetworkIds = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = "Test Switch",
            Capabilities = new SwitchCapabilities()
        };

        return new PortInfo
        {
            PortIndex = 1,
            Name = "Port 1",
            IsUp = true,
            ForwardMode = "native",
            IsUplink = false,
            IsWan = false,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedClient = null,
            LastConnectionMac = null,
            AllowedMacAddresses = new List<string> { "aa:bb:cc:dd:ee:ff" }, // MAC restriction = single device
            Switch = switchInfo
        };
    }

    #endregion
}
