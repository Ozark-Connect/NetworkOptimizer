using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class AccessPortVlanRuleTests
{
    private readonly AccessPortVlanRule _rule;
    private readonly DeviceTypeDetectionService _detectionService;

    public AccessPortVlanRuleTests()
    {
        _rule = new AccessPortVlanRule();
        _detectionService = new DeviceTypeDetectionService();
        _rule.SetDetectionService(_detectionService);
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
    public void Severity_IsRecommended()
    {
        _rule.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void ScoreImpact_Is6()
    {
        _rule.ScoreImpact.Should().Be(6);
    }

    #endregion

    #region Ports That Should Be Skipped - Infrastructure

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        var port = CreateTrunkPortWithClient(isUplink: true);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        var port = CreateTrunkPortWithClient(isWan: true);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region Ports That Should Be Skipped - Access Ports (Not Trunk)

    [Fact]
    public void Evaluate_AccessPort_NativeMode_ReturnsNull()
    {
        // Access ports (native mode) don't have tagged VLANs - not a misconfiguration
        var port = CreateAccessPortWithClient(forwardMode: "native");
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("Access ports in native mode don't have tagged VLANs");
    }

    [Fact]
    public void Evaluate_AccessPort_DisabledMode_ReturnsNull()
    {
        var port = CreateAccessPortWithClient(forwardMode: "disabled");
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_AccessPort_EmptyForwardMode_ReturnsNull()
    {
        var port = CreateAccessPortWithClient(forwardMode: "");
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_AccessPort_NullForwardMode_ReturnsNull()
    {
        var port = CreateAccessPortWithClient(forwardMode: null);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region Ports That Should Be Skipped - Network Fabric Devices

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
        var port = CreateTrunkPortWithClient(connectedDeviceType: deviceType, excludedNetworkIds: null);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region No Device Evidence - Should Still Trigger for Trunk Ports

    [Fact]
    public void Evaluate_TrunkPort_NoConnectedClient_NoOfflineData_WithExcessiveVlans_ReturnsIssue()
    {
        // Trunk port with no device evidence but excessive VLANs - should flag
        var port = CreateTrunkPort(excludedNetworkIds: null);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("Trunk port with no device and excessive VLANs should be flagged");
        result!.Message.Should().Contain("no device");
        result.Metadata!["has_device_evidence"].Should().Be(false);
    }

    [Fact]
    public void Evaluate_TrunkPort_NoDevice_WithAcceptableVlans_ReturnsNull()
    {
        // Trunk port with no device but only 2 VLANs (at threshold) - should not flag
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPort(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("Trunk port with 2 VLANs is at threshold and should not trigger");
    }

    [Fact]
    public void Evaluate_TrunkPort_NoDevice_AllowAll_ReturnsIssue()
    {
        // Trunk port with no device and "Allow All" VLANs - should flag
        var port = CreateTrunkPort(excludedNetworkIds: null);
        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
        result.Metadata["has_device_evidence"].Should().Be(false);
        result.RecommendedAction.Should().Contain("Disable the port");
    }

    [Fact]
    public void Evaluate_TrunkPort_NoDevice_ThreeVlans_ReturnsIssue()
    {
        // Trunk port with no device and 3 VLANs (above threshold) - should flag
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPort(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(3);
        result.Metadata["has_device_evidence"].Should().Be(false);
    }

    [Fact]
    public void Evaluate_NoVlanNetworks_ReturnsNull()
    {
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);
        var networks = new List<NetworkInfo>(); // No VLANs

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SingleNetwork_AllowAll_ReturnsIssue()
    {
        // Even with just 1 network, "Allow All" is flagged because it's a blanket permission
        // that will automatically include any future VLANs added to the network
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-1", Name = "Default", VlanId = 1 }
        };

        var result = _rule.Evaluate(port, networks);

        // "Allow All" always triggers - it's the permissive config, not the current count, that's the issue
        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
        result.Metadata["tagged_vlan_count"].Should().Be(1);
    }

    #endregion

    #region Trunk Port Modes That Should Trigger

    [Theory]
    [InlineData("custom")]
    [InlineData("customize")]
    [InlineData("all")]
    public void Evaluate_TrunkPortMode_WithSingleDevice_ExcessiveVlans_ReturnsIssue(string forwardMode)
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(forwardMode: forwardMode, excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull($"Trunk port in '{forwardMode}' mode with single device and all VLANs should trigger");
    }

    #endregion

    #region VLAN Count Threshold Tests

    [Fact]
    public void Evaluate_TrunkPort_OneTaggedVlan_ReturnsNull()
    {
        // 1 VLAN is fine
        var networks = CreateVlanNetworks(5);
        var excludeAllButOne = networks.Skip(1).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButOne);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_TwoTaggedVlans_ReturnsNull()
    {
        // 2 VLANs is acceptable
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ThreeTaggedVlans_ReturnsIssue()
    {
        // 3 VLANs is excessive for a single device
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("tagged_vlan_count");
        result.Metadata!["tagged_vlan_count"].Should().Be(3);
        result.Metadata.Should().ContainKey("allows_all_vlans");
        result.Metadata["allows_all_vlans"].Should().Be(false);
    }

    [Fact]
    public void Evaluate_TrunkPort_FiveTaggedVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: new List<string>()); // Allow all 5

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(5);
    }

    #endregion

    #region Allow All VLANs Detection

    [Fact]
    public void Evaluate_TrunkPort_AllowAllVlans_NullExcludedList_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null); // null = Allow All

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
        result.Metadata["tagged_vlan_count"].Should().Be(5);
    }

    [Fact]
    public void Evaluate_TrunkPort_AllowAllVlans_EmptyExcludedList_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: new List<string>()); // empty = Allow All

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["allows_all_vlans"].Should().Be(true);
    }

    #endregion

    #region Single Device Detection - Connected Client

    [Fact]
    public void Evaluate_TrunkPort_ConnectedClient_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata!["has_device_evidence"].Should().Be(true);
        result.Message.Should().Contain("single device");
    }

    [Fact]
    public void Evaluate_TrunkPort_ConnectedClient_WithAcceptableVlans_ReturnsNull()
    {
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    #endregion

    #region Single Device Detection - Offline Data

    [Fact]
    public void Evaluate_TrunkPort_LastConnectionMac_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithLastConnectionMac(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_AllowedMacAddresses_WithExcessiveVlans_ReturnsIssue()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithAllowedMacs(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_LastConnectionMac_WithAcceptableVlans_ReturnsNull()
    {
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithLastConnectionMac(excludedNetworkIds: excludeAllButTwo);

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
    public void Evaluate_TrunkPort_EndpointDeviceWithExcessiveVlans_ReturnsIssue(string? deviceType)
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(connectedDeviceType: deviceType, excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    #endregion

    #region Issue Details

    [Fact]
    public void Evaluate_IssueContainsCorrectRuleId()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Type.Should().Be("ACCESS-VLAN-001");
        result.RuleId.Should().Be("ACCESS-VLAN-001");
    }

    [Fact]
    public void Evaluate_IssueContainsCorrectSeverityAndScore()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(6);
    }

    [Fact]
    public void Evaluate_IssueContainsPortDetails()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(
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
        var port = CreateTrunkPortWithClient(
            nativeNetworkId: "net-1",
            excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("network");
        result.Metadata!["network"].Should().Be("VLAN 20");
    }

    [Fact]
    public void Evaluate_IssueContainsRecommendation_AllowAll()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null); // Allow All

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.RecommendedAction.Should().NotBeNullOrEmpty();
        result.RecommendedAction.Should().Contain("Allow All");
    }

    [Fact]
    public void Evaluate_IssueContainsRecommendation_ThresholdExceeded()
    {
        var networks = CreateVlanNetworks(5);
        var excludeTwo = networks.Take(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeTwo); // 3 VLANs allowed

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.RecommendedAction.Should().NotBeNullOrEmpty();
        result.RecommendedAction.Should().Contain("single-device port");
    }

    [Fact]
    public void Evaluate_IssueMessageDescribesAllVlans()
    {
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("all VLANs");
    }

    [Fact]
    public void Evaluate_IssueMessageDescribesVlanCount()
    {
        var networks = CreateVlanNetworks(5);
        var excludeTwo = networks.Take(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeTwo); // 3 VLANs allowed

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("3 VLANs tagged");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Evaluate_TrunkPort_ExcludedNetworkNotInList_HandlesGracefully()
    {
        var networks = CreateVlanNetworks(5);
        var excludeWithUnknown = new List<string>
        {
            "net-0", // valid
            "unknown-network-id", // invalid - should be ignored
            "another-unknown"
        };
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeWithUnknown);

        var result = _rule.Evaluate(port, networks);

        // 5 networks - 1 valid excluded = 4 VLANs (above threshold)
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(4);
    }

    [Fact]
    public void Evaluate_TrunkPort_MultipleVlans_CountsAllNetworks()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-1", Name = "Default", VlanId = 1 },
            new() { Id = "net-10", Name = "VLAN 10", VlanId = 10 },
            new() { Id = "net-20", Name = "VLAN 20", VlanId = 20 },
            new() { Id = "net-30", Name = "VLAN 30", VlanId = 30 }
        };
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        // All 4 networks count, which is above threshold
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(4);
    }

    [Fact]
    public void Evaluate_TrunkPort_ExactlyAtThreshold_ReturnsNull()
    {
        // Threshold is 2, so exactly 2 VLANs should NOT trigger
        var networks = CreateVlanNetworks(5);
        var excludeAllButTwo = networks.Skip(2).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButTwo);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_JustAboveThreshold_ReturnsIssue()
    {
        // Threshold is 2, so 3 VLANs should trigger
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_CountsDisabledNetworks()
    {
        // Disabled networks should count because if enabled later,
        // the tagged VLANs would suddenly be active on this port
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-0", Name = "Active", VlanId = 10, Enabled = true },
            new() { Id = "net-1", Name = "Disabled1", VlanId = 20, Enabled = false },
            new() { Id = "net-2", Name = "Disabled2", VlanId = 30, Enabled = false },
            new() { Id = "net-3", Name = "Disabled3", VlanId = 40, Enabled = false }
        };
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        // All 4 networks counted (including disabled), which is above threshold
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(4);
    }

    [Fact]
    public void Evaluate_TrunkPort_NativeVlanExcludedFromTaggedCount()
    {
        // 4 networks total, 1 is native, so tagged count should be 3 (above threshold)
        var networks = CreateVlanNetworks(4);
        var port = CreateTrunkPortWithClient(
            nativeNetworkId: "net-0", // This is the native VLAN (untagged)
            excludedNetworkIds: new List<string>()); // Allow all = triggers issue

        var result = _rule.Evaluate(port, networks);

        // Should trigger because allow-all, but tagged count should be 3 (4 - 1 native)
        result.Should().NotBeNull();
        result!.Metadata!["tagged_vlan_count"].Should().Be(3,
            "native VLAN should not count as tagged");
        result.Metadata["allows_all_vlans"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_TrunkPort_WithNative_AtThreshold_ReturnsNull()
    {
        // 3 networks total, 1 is native, so tagged count = 2 (at threshold)
        // With explicit exclusions (not allow-all), this should NOT trigger
        var networks = CreateVlanNetworks(3);
        var port = CreateTrunkPortWithClient(
            nativeNetworkId: "net-0",
            excludedNetworkIds: new List<string> { "net-0" }); // Exclude the native

        var result = _rule.Evaluate(port, networks);

        // 2 tagged VLANs (net-1, net-2), native excluded - at threshold, should not trigger
        result.Should().BeNull();
    }

    #endregion

    #region Server Device Higher Threshold

    [Fact]
    public void Evaluate_ServerDevice_FiveVlans_AtThreshold_ReturnsNull()
    {
        // 5 VLANs is at the server threshold (5) - should not trigger
        var networks = CreateVlanNetworks(10);
        var excludeAllButFive = networks.Skip(5).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithServerClient("proxmox-host", excludedNetworkIds: excludeAllButFive);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("5 VLANs is at the server threshold");
    }

    [Fact]
    public void Evaluate_ServerDevice_SixVlans_ReturnsIssue()
    {
        // 6 VLANs exceeds the server threshold (5)
        var networks = CreateVlanNetworks(10);
        var excludeAllButSix = networks.Skip(6).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithServerClient("proxmox-host", excludedNetworkIds: excludeAllButSix);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("6 VLANs exceeds the server threshold of 5");
        result!.Message.Should().Contain("Server port");
        result.Metadata!["is_server_device"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_ServerDevice_AllowAll_ReturnsIssue()
    {
        // Even servers should not have "Allow All" VLANs
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithServerClient("proxmox-host", excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("'Allow All' should still trigger for servers");
        result!.Message.Should().Contain("Server port");
        result.Metadata!["is_server_device"].Should().Be(true);
        result.Metadata["allows_all_vlans"].Should().Be(true);
    }

    [Theory]
    [InlineData("proxmox-host")]
    [InlineData("esxi-server")]
    [InlineData("truenas-storage")]
    [InlineData("unraid-server")]
    [InlineData("docker-host")]
    [InlineData("my-server")]
    public void Evaluate_ServerDeviceByName_WithModerateVlans_ReturnsNull(string hostname)
    {
        // Various server-like hostnames should all get the higher threshold
        var networks = CreateVlanNetworks(10);
        var excludeAllButFive = networks.Skip(5).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithServerClient(hostname, excludedNetworkIds: excludeAllButFive);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull($"'{hostname}' should be detected as a server and get higher threshold");
    }

    [Fact]
    public void Evaluate_NonServerDevice_ThreeVlans_StillTriggersNormalThreshold()
    {
        // Non-server devices should still use the normal threshold (2)
        var networks = CreateVlanNetworks(5);
        var excludeAllButThree = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButThree);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("non-server devices use the lower threshold of 2");
    }

    [Fact]
    public void Evaluate_ServerDevice_WithFingerprint_DevCat56_ReturnsNull()
    {
        // Server detected via UniFi fingerprint (dev_cat=56 = Server)
        var networks = CreateVlanNetworks(10);
        var excludeAllButFive = networks.Skip(5).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButFive);
        port.ConnectedClient!.DevCat = 56; // Server category in UniFi fingerprint

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("device with dev_cat=56 (Server) should get higher threshold");
    }

    [Fact]
    public void Evaluate_ServerDevice_WithFingerprint_DevCat182_ReturnsNull()
    {
        // Server detected via UniFi fingerprint (dev_cat=182 = Virtual Machine)
        var networks = CreateVlanNetworks(10);
        var excludeAllButFive = networks.Skip(5).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeAllButFive);
        port.ConnectedClient!.DevCat = 182; // Virtual Machine category

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("device with dev_cat=182 (Virtual Machine) should get higher threshold");
    }

    #endregion

    #region 802.1X / RADIUS Dynamic VLAN Assignment

    [Fact]
    public void Evaluate_Dot1x_MacBased_CustomVlanSet_ReturnsNull()
    {
        // 802.1X mac_based with curated VLAN list - trust admin intent
        var networks = CreateVlanNetworks(5);
        var excludeSome = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeSome, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("802.1X mac_based with custom VLAN set should be trusted");
    }

    [Fact]
    public void Evaluate_Dot1x_Auto_CustomVlanSet_ReturnsNull()
    {
        // 802.1X auto with curated VLAN list - trust admin intent
        var networks = CreateVlanNetworks(5);
        var excludeSome = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeSome, dot1xCtrl: "auto");

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("802.1X auto with custom VLAN set should be trusted");
    }

    [Fact]
    public void Evaluate_Dot1x_MacBased_AllowAll_ReturnsInformational()
    {
        // 802.1X mac_based with Allow All - downgrade to Informational
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("802.1X with Allow All should still flag");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(2);
        result.Message.Should().Contain("802.1X");
        result.Metadata!["is_dot1x_secured"].Should().Be(true);
        result.Metadata["allows_all_vlans"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_Dot1x_Auto_AllowAll_ReturnsInformational()
    {
        // 802.1X auto with Allow All - downgrade to Informational
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "auto");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("802.1X with Allow All should still flag");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(2);
        result.Metadata!["is_dot1x_secured"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_Dot1x_ForceAuthorized_NormalRuleApplies()
    {
        // force_authorized is not IsDot1xSecured - normal rule applies
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "force_authorized");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("force_authorized is not 802.1X secured");
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(6);
    }

    [Fact]
    public void Evaluate_Dot1x_Null_NormalRuleApplies()
    {
        // null Dot1xCtrl - normal rule applies (same as default)
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("null Dot1xCtrl means no 802.1X - normal rule");
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(6);
    }

    [Fact]
    public void Evaluate_Dot1x_NoConnectedClient_AllowAll_ReturnsInformational()
    {
        // 802.1X trunk port with no connected client - should still trigger 802.1X path
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPort(excludedNetworkIds: null, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("802.1X with Allow All should flag even without connected client");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(2);
        result.Metadata!["is_dot1x_secured"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_Dot1x_NoConnectedClient_CustomVlanSet_ReturnsNull()
    {
        // 802.1X trunk port, no client, curated VLAN list - trust admin
        var networks = CreateVlanNetworks(5);
        var excludeSome = networks.Skip(3).Select(n => n.Id).ToList();
        var port = CreateTrunkPort(excludedNetworkIds: excludeSome, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("802.1X with custom VLAN set should be trusted even without client");
    }

    [Fact]
    public void Evaluate_Dot1x_PortDown_AllowAll_ReturnsInformational()
    {
        // Down 802.1X port with Allow All - rule doesn't gate on IsUp, so should still flag
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "mac_based");
        // CreateTrunkPortWithClient sets IsUp=true, but the rule doesn't check IsUp for 802.1X path.
        // For a down port we need to use CreateTrunkPort (no client) since down ports typically have no client.
        var downPort = CreateTrunkPort(excludedNetworkIds: null, dot1xCtrl: "auto");

        var result = _rule.Evaluate(downPort, networks);

        result.Should().NotBeNull("down 802.1X port with Allow All should still flag");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(2);
    }

    [Fact]
    public void Evaluate_Dot1x_ZeroNetworks_ReturnsNull()
    {
        // 802.1X port with no networks at all - nothing to evaluate
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "mac_based");
        var networks = new List<NetworkInfo>();

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("no networks means nothing to evaluate, even with 802.1X");
    }

    [Fact]
    public void Evaluate_Dot1x_EmptyExcludedList_ReturnsInformational()
    {
        // Empty excluded list is also "Allow All" (same as null) - should trigger 802.1X path
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithClient(
            excludedNetworkIds: new List<string>(), dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("empty excluded list means Allow All on 802.1X port");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(2);
        result.Metadata!["allows_all_vlans"].Should().Be(true);
    }

    [Fact]
    public void Evaluate_Dot1x_AllNetworksParameter_UsesAllNetworks()
    {
        // When allNetworks is explicitly passed, 802.1X path should use it for VLAN counting
        var enabledNetworks = CreateVlanNetworks(2);
        var allNetworks = CreateVlanNetworks(8); // More networks including disabled ones
        var port = CreateTrunkPortWithClient(excludedNetworkIds: null, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, enabledNetworks, allNetworks);

        result.Should().NotBeNull("802.1X with Allow All should flag using allNetworks count");
        result!.Severity.Should().Be(AuditSeverity.Informational);
        result.Metadata!["tagged_vlan_count"].Should().Be(8,
            "should count VLANs from allNetworks, not just enabled networks");
    }

    [Fact]
    public void Evaluate_Dot1x_AllNetworksParameter_CustomVlanSet_ReturnsNull()
    {
        // 802.1X with custom VLAN set should use allNetworks for determining if "Allow All"
        var enabledNetworks = CreateVlanNetworks(2);
        var allNetworks = CreateVlanNetworks(8);
        var excludeSome = allNetworks.Skip(4).Select(n => n.Id).ToList();
        var port = CreateTrunkPortWithClient(excludedNetworkIds: excludeSome, dot1xCtrl: "auto");

        var result = _rule.Evaluate(port, enabledNetworks, allNetworks);

        result.Should().BeNull("802.1X with custom VLAN set should be trusted even with allNetworks");
    }

    [Fact]
    public void Evaluate_Dot1x_ServerDevice_AllowAll_ReturnsInformational()
    {
        // 802.1X takes priority over server detection - should return Informational, not Recommended
        var networks = CreateVlanNetworks(5);
        var port = CreateTrunkPortWithServerClient("proxmox-host",
            excludedNetworkIds: null, dot1xCtrl: "mac_based");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("802.1X with Allow All should flag even on server");
        result!.Severity.Should().Be(AuditSeverity.Informational,
            "802.1X path should take priority over server detection");
        result.ScoreImpact.Should().Be(2);
        result.Message.Should().Contain("802.1X");
        result.Metadata!["is_dot1x_secured"].Should().Be(true);
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

    /// <summary>
    /// Create an access port (native mode) - should NOT trigger the rule
    /// </summary>
    private static PortInfo CreateAccessPortWithClient(
        string? forwardMode = "native",
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch")
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
            ForwardMode = forwardMode,
            IsUplink = false,
            IsWan = false,
            NativeNetworkId = null,
            ExcludedNetworkIds = null,
            ConnectedDeviceType = null,
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

    /// <summary>
    /// Create a trunk port WITHOUT device data - will trigger if excessive VLANs (no device evidence)
    /// </summary>
    private static PortInfo CreateTrunkPort(
        List<string>? excludedNetworkIds = null,
        string forwardMode = "custom",
        string? dot1xCtrl = null)
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
            ForwardMode = forwardMode,
            IsUplink = false,
            IsWan = false,
            NativeNetworkId = null,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedDeviceType = null,
            ConnectedClient = null,
            LastConnectionMac = null,
            AllowedMacAddresses = null,
            Dot1xCtrl = dot1xCtrl,
            Switch = switchInfo
        };
    }

    /// <summary>
    /// Create a trunk port WITH a connected client (single device evidence)
    /// </summary>
    private static PortInfo CreateTrunkPortWithClient(
        List<string>? excludedNetworkIds = null,
        bool isUplink = false,
        bool isWan = false,
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch",
        string? nativeNetworkId = null,
        string? connectedDeviceType = null,
        string forwardMode = "custom",
        string? dot1xCtrl = null)
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
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            NativeNetworkId = nativeNetworkId,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedDeviceType = connectedDeviceType,
            Dot1xCtrl = dot1xCtrl,
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

    /// <summary>
    /// Create a trunk port with LastConnectionMac (offline device evidence)
    /// </summary>
    private static PortInfo CreateTrunkPortWithLastConnectionMac(
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
            ForwardMode = "custom",
            IsUplink = false,
            IsWan = false,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedClient = null,
            LastConnectionMac = "aa:bb:cc:dd:ee:ff", // Offline device data
            AllowedMacAddresses = null,
            Switch = switchInfo
        };
    }

    /// <summary>
    /// Create a trunk port with AllowedMacAddresses (MAC restriction = single device evidence)
    /// </summary>
    private static PortInfo CreateTrunkPortWithAllowedMacs(
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
            ForwardMode = "custom",
            IsUplink = false,
            IsWan = false,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedClient = null,
            LastConnectionMac = null,
            AllowedMacAddresses = new List<string> { "aa:bb:cc:dd:ee:ff" },
            Switch = switchInfo
        };
    }

    /// <summary>
    /// Create a trunk port with a connected client that has a server-like hostname.
    /// The DeviceTypeDetectionService should detect it as a Server category.
    /// </summary>
    private static PortInfo CreateTrunkPortWithServerClient(
        string hostname,
        List<string>? excludedNetworkIds = null,
        string? dot1xCtrl = null)
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
            ForwardMode = "custom",
            IsUplink = false,
            IsWan = false,
            ExcludedNetworkIds = excludedNetworkIds,
            ConnectedDeviceType = null,
            Dot1xCtrl = dot1xCtrl,
            ConnectedClient = new UniFiClientResponse
            {
                Mac = "aa:bb:cc:dd:ee:ff",
                Hostname = hostname,
                Name = hostname
            },
            LastConnectionMac = null,
            AllowedMacAddresses = null,
            Switch = switchInfo
        };
    }

    [Fact]
    public void Evaluate_ForwardAllWithBlockAllTaggedVlans_NoIssue()
    {
        // UDB-style ports have forward="all" but tagged_vlan_mgmt="block_all",
        // meaning all tagged VLANs are blocked. This is effectively an access port.
        var switchInfo = new SwitchInfo
        {
            Name = "UDB Backyard",
            Capabilities = new SwitchCapabilities()
        };

        var port = new PortInfo
        {
            PortIndex = 1,
            Name = "Port 1",
            IsUp = false,
            ForwardMode = "all",
            TaggedVlanMgmt = "block_all",
            IsUplink = false,
            IsWan = false,
            NativeNetworkId = "net-1",
            ExcludedNetworkIds = null,
            ConnectedDeviceType = null,
            ConnectedClient = null,
            LastConnectionMac = null,
            AllowedMacAddresses = null,
            Switch = switchInfo
        };

        var networks = CreateVlanNetworks(5);

        var result = _rule.Evaluate(port, networks, networks);

        result.Should().BeNull("port with tagged_vlan_mgmt=block_all is effectively an access port");
    }

    #endregion
}
