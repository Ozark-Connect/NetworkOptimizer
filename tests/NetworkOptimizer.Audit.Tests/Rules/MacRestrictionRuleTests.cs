using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class MacRestrictionRuleTests
{
    private readonly MacRestrictionRule _rule;

    public MacRestrictionRuleTests()
    {
        _rule = new MacRestrictionRule();
    }

    #region Rule Properties

    [Fact]
    public void RuleId_ReturnsExpectedValue()
    {
        _rule.RuleId.Should().Be("MAC-RESTRICT-001");
    }

    [Fact]
    public void Severity_IsRecommended()
    {
        _rule.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void ScoreImpact_Is3()
    {
        _rule.ScoreImpact.Should().Be(3);
    }

    #endregion

    #region Evaluate Tests - Ports That Should Be Ignored

    [Fact]
    public void Evaluate_PortNotUp_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: false, forwardMode: "native");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_TrunkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "all");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", isUplink: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", isWan: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_SwitchDoesNotSupportMacAcls_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", maxMacAcls: 0);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    #endregion

    #region Evaluate Tests - Ports That Are Already Protected

    [Fact]
    public void Evaluate_PortSecurityEnabled_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native", portSecurityEnabled: true);

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_HasAllowedMacAddresses_ReturnsNull()
    {
        // Arrange
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            allowedMacs: new List<string> { "AA:BB:CC:DD:EE:FF" });

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CustomModeWithoutNativeNetwork_ReturnsNull()
    {
        // Custom mode without a native network set is a trunk/hybrid - skip it
        var port = CreatePort(isUp: true, forwardMode: "custom", nativeNetworkId: null);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_CustomModeWithNativeNetwork_ReturnsIssue()
    {
        // Custom mode WITH a native network set is an access port - should trigger
        var port = CreatePort(isUp: true, forwardMode: "custom", nativeNetworkId: "net-123");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
    }

    #endregion

    #region Network Fabric Device Detection

    [Theory]
    [InlineData("uap")]   // Access Point
    [InlineData("usw")]   // Switch
    [InlineData("ubb")]   // Building-to-Building Bridge
    [InlineData("ugw")]   // Gateway
    [InlineData("usg")]   // Security Gateway
    [InlineData("udm")]   // Dream Machine
    [InlineData("uxg")]   // Next-Gen Gateway
    [InlineData("ucg")]   // Cloud Gateway
    public void Evaluate_NetworkFabricDeviceConnected_ReturnsNull(string deviceType)
    {
        // Network fabric devices (AP, switch, bridge, gateway) should be skipped
        var port = CreatePort(isUp: true, forwardMode: "native", connectedDeviceType: deviceType);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("umbb")]  // Modem
    [InlineData("uck")]   // Cloud Key
    [InlineData("unvr")]  // NVR
    [InlineData("uph")]   // Phone
    [InlineData(null)]    // Unknown
    [InlineData("")]      // Empty
    public void Evaluate_EndpointDeviceConnected_ReturnsIssue(string? deviceType)
    {
        // Endpoint devices (modem, NVR, Cloud Key) SHOULD get MAC restriction recommendations
        var port = CreatePort(isUp: true, forwardMode: "native", connectedDeviceType: deviceType);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
    }

    #endregion

    #region Access Point Name Detection Fallback

    [Theory]
    [InlineData("AP-Lobby")]           // AP as word boundary
    [InlineData("Lobby AP")]           // AP at end
    [InlineData("WiFi-Upstairs")]      // Contains wifi
    [InlineData("Access Point 1")]     // Contains access point
    [InlineData("WAP-Office")]         // WAP as word boundary
    [InlineData("Office WAP")]         // WAP at end
    public void Evaluate_PortNameSuggestsAP_ReturnsNull(string portName)
    {
        // Fallback: if port name suggests an AP, skip it
        var port = CreatePort(isUp: true, forwardMode: "native", portName: portName);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Theory]
    [InlineData("Office PC")]
    [InlineData("Printer")]
    [InlineData("Camera-Front")]
    [InlineData("Port 1")]
    [InlineData("Laptop")]         // Contains "ap" but not as word boundary
    [InlineData("Application")]    // Contains "ap" but not as word boundary
    [InlineData("UAP-AC-Pro")]     // UAP is not "AP" as a word
    public void Evaluate_PortNameDoesNotSuggestAP_ReturnsIssue(string portName)
    {
        var port = CreatePort(isUp: true, forwardMode: "native", portName: portName);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
    }

    #endregion

    #region Evaluate Tests - Ports That Should Trigger Issue

    [Fact]
    public void Evaluate_UnprotectedAccessPort_ReturnsIssue()
    {
        // Arrange
        var port = CreatePort(isUp: true, forwardMode: "native");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
    }

    [Fact]
    public void Evaluate_UnprotectedAccessPort_IncludesPortDetails()
    {
        // Arrange
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            portIndex: 5,
            portName: "Office PC",
            switchName: "Switch-Lobby");

        // Act
        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        // Assert
        result.Should().NotBeNull();
        result!.DeviceName.Should().Be("Office PC on Switch-Lobby");
        result.Port.Should().Be("5");
        result.PortName.Should().Be("Office PC");
    }

    [Fact]
    public void Evaluate_UnprotectedAccessPort_IncludesNetworkName()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-123", Name = "Corporate LAN", VlanId = 10 }
        };
        var port = CreatePort(
            isUp: true,
            forwardMode: "native",
            nativeNetworkId: "net-123");

        // Act
        var result = _rule.Evaluate(port, networks);

        // Assert
        result.Should().NotBeNull();
        result!.Metadata.Should().ContainKey("network");
        result.Metadata!["network"].Should().Be("Corporate LAN");
    }

    #endregion

    #region 802.1X / RADIUS Authentication

    [Theory]
    [InlineData("auto")]      // 802.1X authentication
    [InlineData("mac_based")] // RADIUS MAC authentication
    public void Evaluate_Dot1xSecuredPort_ReturnsNull(string dot1xCtrl)
    {
        var port = CreatePort(isUp: true, forwardMode: "native", dot1xCtrl: dot1xCtrl);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull("port is secured via 802.1X/RADIUS authentication");
    }

    [Theory]
    [InlineData("force_authorized")]  // Bypass - not secured
    [InlineData("force_unauthorized")] // Block - different concern
    [InlineData(null)]                 // No 802.1X configured
    public void Evaluate_NonSecuredDot1xMode_ReturnsIssue(string? dot1xCtrl)
    {
        var port = CreatePort(isUp: true, forwardMode: "native", dot1xCtrl: dot1xCtrl);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull("port is not secured via 802.1X");
    }

    #endregion

    #region Intentional Unrestricted Profile Detection

    [Fact]
    public void Evaluate_PortWithUnrestrictedAccessProfile_ReturnsNull()
    {
        // Port has a profile that is an access port with MAC restriction explicitly disabled
        // and tagged VLANs blocked - this indicates intentional unrestricted access (like hotel RJ45 jacks)
        var profile = new UniFiPortProfile
        {
            Id = "profile-123",
            Name = "[Access] Unrestricted",
            Forward = "native",
            PortSecurityEnabled = false,
            TaggedVlanMgmt = "block_all"
        };
        var port = CreatePort(isUp: true, forwardMode: "native", assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull("port has an intentional unrestricted access profile");
    }

    [Fact]
    public void Evaluate_PortWithProfileAllowingTaggedVlans_ReturnsIssue()
    {
        // Profile has tagged VLANs set to auto (allow all) - not an intentional unrestricted profile
        var profile = new UniFiPortProfile
        {
            Id = "profile-789",
            Name = "[Access] Unrestricted",
            Forward = "native",
            PortSecurityEnabled = false,
            TaggedVlanMgmt = "auto"
        };
        var port = CreatePort(isUp: true, forwardMode: "native", assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull("profile allows all tagged VLANs, not a proper unrestricted access profile");
    }

    [Fact]
    public void Evaluate_PortWithProfileForwardCustomize_ReturnsIssue()
    {
        // Profile has forward=customize (not native) - not an intentional unrestricted profile
        // Port is native mode so it's evaluated as an access port
        var profile = new UniFiPortProfile
        {
            Id = "profile-abc",
            Name = "[Access] Unrestricted",
            Forward = "customize",
            PortSecurityEnabled = false,
            TaggedVlanMgmt = "auto"
        };
        var port = CreatePort(isUp: true, forwardMode: "native", assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull("profile has forward=customize, not an intentional unrestricted access profile");
    }

    [Fact]
    public void Evaluate_PortWithAccessProfileButSecurityEnabled_ReturnsNull()
    {
        // If profile has PortSecurityEnabled = true, the port would have PortSecurityEnabled resolved to true
        // and would pass the earlier check (port already has MAC restrictions)
        var profile = new UniFiPortProfile
        {
            Id = "profile-456",
            Name = "[Access] Restricted",
            Forward = "native",
            PortSecurityEnabled = true
        };
        // Port's PortSecurityEnabled is resolved from profile
        var port = CreatePort(isUp: true, forwardMode: "native", portSecurityEnabled: true, assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull("port has port security enabled via profile");
    }

    [Fact]
    public void Evaluate_PortWithTrunkProfileAndNoSecurity_ReturnsNull()
    {
        // Profile has forward=all (trunk) with no security - this is not an access port
        // The rule should already skip trunk ports via the forwardMode check
        var profile = new UniFiPortProfile
        {
            Id = "profile-789",
            Name = "[Trunk] All VLANs",
            Forward = "all",
            PortSecurityEnabled = false
        };
        var port = CreatePort(isUp: true, forwardMode: "all", assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull("trunk ports are skipped by the rule");
    }

    [Fact]
    public void Evaluate_PortWithNoProfile_ReturnsIssue()
    {
        // Port has no profile assigned - should still trigger the issue
        var port = CreatePort(isUp: true, forwardMode: "native", assignedProfile: null);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull("port without a profile should still be flagged");
    }

    #endregion

    #region Helper Methods

    private static PortInfo CreatePort(
        bool isUp = true,
        string forwardMode = "native",
        bool isUplink = false,
        bool isWan = false,
        bool portSecurityEnabled = false,
        List<string>? allowedMacs = null,
        int maxMacAcls = 32,
        int portIndex = 1,
        string portName = "Port 1",
        string switchName = "Test Switch",
        string? nativeNetworkId = null,
        string? connectedDeviceType = null,
        UniFiPortProfile? assignedProfile = null,
        string? dot1xCtrl = null)
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Capabilities = new SwitchCapabilities
            {
                MaxCustomMacAcls = maxMacAcls
            }
        };

        return new PortInfo
        {
            PortIndex = portIndex,
            Name = portName,
            IsUp = isUp,
            ForwardMode = forwardMode,
            IsUplink = isUplink,
            IsWan = isWan,
            PortSecurityEnabled = portSecurityEnabled,
            AllowedMacAddresses = allowedMacs,
            NativeNetworkId = nativeNetworkId,
            ConnectedDeviceType = connectedDeviceType,
            Dot1xCtrl = dot1xCtrl,
            Switch = switchInfo,
            AssignedPortProfile = assignedProfile
        };
    }

    #endregion
}
