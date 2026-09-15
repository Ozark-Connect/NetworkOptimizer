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

    [Fact]
    public void Evaluate_ServerNetwork_NoDot1x_ReturnsNull()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-server", Name = "Service", VlanId = 1000, Purpose = NetworkPurpose.Server }
        };
        var port = CreatePort(isUp: true, forwardMode: "native", nativeNetworkId: "net-server", dot1xPortCtrlEnabled: false);

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("switch doesn't support 802.1X, and MAC restriction is impractical for servers");
    }

    [Fact]
    public void Evaluate_ServerNetwork_Dot1xAvailable_ReturnsIssue()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-server", Name = "Service", VlanId = 1000, Purpose = NetworkPurpose.Server }
        };
        var port = CreatePort(isUp: true, forwardMode: "native", nativeNetworkId: "net-server", dot1xPortCtrlEnabled: true);

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull("802.1X is available and should be recommended for server ports");
        result!.RecommendedAction.Should().Contain("802.1X");
    }

    [Fact]
    public void Evaluate_ServerNetwork_Dot1xAlreadySecured_ReturnsNull()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "net-server", Name = "Service", VlanId = 1000, Purpose = NetworkPurpose.Server }
        };
        var port = CreatePort(isUp: true, forwardMode: "native", nativeNetworkId: "net-server",
            dot1xPortCtrlEnabled: true, dot1xCtrl: "multi_host");

        var result = _rule.Evaluate(port, networks);

        result.Should().BeNull("port is already secured via 802.1X");
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

    #region Lock Port to UniFi Device

    [Fact]
    public void Evaluate_PortLocked_ReturnsNull()
    {
        // A lock is the restriction; no MAC list is wanted on top of it
        var port = CreatePort(isUp: true, forwardMode: "native", lockedToDeviceMac: "aa:bb:cc:dd:ee:ff");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_LockAvailable_ReturnsNull()
    {
        // PortLockRule owns this port when the versions allow the lock
        _rule.SetNetworkApplicationVersion("10.6.106");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_LockUnavailable_ReturnsIssueNamingTheLock()
    {
        _rule.SetNetworkApplicationVersion("10.5.120");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Message.Should().Contain("AI Key (UniFi Protect)");
        result.Message.Should().Contain("Lock Port to UniFi Device");
        result.RecommendedAction.Should().Contain("10.6.101");
        result.RecommendedAction.Should().Contain("7.6.2");
        result.RecommendedAction.Should().Contain("'Restricted'");
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_OldFirmware_ReturnsIssueNamingTheLock()
    {
        _rule.SetNetworkApplicationVersion("10.6.106");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.5.15.17146");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Message.Should().Contain("Lock Port to UniFi Device");
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_ProfileBlocksLock_ReturnsIssueNamingTheProfile()
    {
        // Versions allow the lock, but an assigned profile (not an intentional unrestricted one) blocks it
        _rule.SetNetworkApplicationVersion("10.6.106");
        var profile = new UniFiPortProfile { Id = "prof-1", Name = "Camera", Forward = "native", PortSecurityEnabled = false, TaggedVlanMgmt = "auto" };
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186", assignedProfile: profile);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Message.Should().Contain("Ethernet Port Profile removed");
        result.RecommendedAction.Should().Contain("cannot be combined with an Ethernet Port Profile");
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_SharedPort_LockAvailable_ReturnsRecommendedWithSharedCopy()
    {
        // Several clients on the port: MAC restriction is the tool, and this rule keeps the score
        _rule.SetNetworkApplicationVersion("10.6.106");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186",
            seenMacs: ["aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"]);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
        result.Message.Should().Be("Port carries AI Key (UniFi Protect) but 3 devices have used it; Lock Port to UniFi Device fits only if AI Key (UniFi Protect) is the sole occupant");
        result.RecommendedAction.Should().Be(
            "3 different devices have used this port in the last 7 days. If more than one device needs it, " +
            "use Restricted with each allowed MAC address instead. " +
            "If AI Key (UniFi Protect) is the only one that belongs here, enable Lock Port to UniFi Device in Port Manager.");
        result.Metadata!["devices_seen"].Should().Be(3);
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_SharedPort_LockUnavailable_ReturnsPlainMacCopy()
    {
        // No lock on offer: the lock is not mentioned on a shared port
        _rule.SetNetworkApplicationVersion("10.5.120");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186",
            seenMacs: ["aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:01"]);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.Message.Should().Be("Port should be set to Restricted w/ an Allowed MAC Address or restricted via an Ethernet Port Profile in UniFi Network");
        result.RecommendedAction.Should().NotContain("Lock Port to UniFi Device");
    }

    [Theory]
    [InlineData("uxg", "6.0.5.35344")]
    [InlineData("uap", "8.8.8.20113")]
    public void Evaluate_UniFiDeviceOnGatewayOrApPort_ReturnsPlainMacCopy(string switchType, string firmware)
    {
        // The lock is USW only: no "upgrade to get it" copy on a device that never offers it
        _rule.SetNetworkApplicationVersion("10.6.106");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), switchType: switchType, firmwareVersion: firmware);

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Message.Should().Be("Port should be set to Restricted w/ an Allowed MAC Address or restricted via an Ethernet Port Profile in UniFi Network");
        result.RecommendedAction.Should().NotContain("Lock Port to UniFi Device");
    }

    [Fact]
    public void Evaluate_UniFiDeviceOnLag_ReturnsPlainMacCopy()
    {
        // Versions allow the lock, but a LAG never takes it: plain MAC restriction, no lock mention
        _rule.SetNetworkApplicationVersion("10.6.106");
        var lag = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186");
        var port = new PortInfo
        {
            PortIndex = lag.PortIndex, Name = lag.Name, IsUp = true, ForwardMode = "native", OpMode = "aggregate",
            ConnectedClient = lag.ConnectedClient, Switch = lag.Switch
        };

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Type.Should().Be("MAC-RESTRICT-001");
        result.Message.Should().Be("Port should be set to Restricted w/ an Allowed MAC Address or restricted via an Ethernet Port Profile in UniFi Network");
        result.RecommendedAction.Should().NotContain("Lock Port to UniFi Device");
    }

    [Fact]
    public void Evaluate_EndpointDevice_LockAvailable_ReturnsNull()
    {
        // A non-fabric Network device (modem) defers to the lock rule too
        _rule.SetNetworkApplicationVersion("10.6.106");
        var port = CreatePort(isUp: true, forwardMode: "native", connectedDeviceType: "umbb", firmwareVersion: "7.6.2.17186");

        var result = _rule.Evaluate(port, new List<NetworkInfo>());

        result.Should().BeNull();
    }

    [Fact]
    public void Evaluate_UniFiDeviceClient_RecentlyUsedDownPort_KeepsInactiveVerbiage()
    {
        // The lock needs a live device; a down port gets the usual disable-or-restrict copy
        var port = CreatePort(isUp: true, forwardMode: "native", connectedClient: ProtectClient(), firmwareVersion: "7.6.2.17186");
        var downPort = new PortInfo
        {
            PortIndex = port.PortIndex, Name = port.Name, IsUp = false, ForwardMode = "native",
            LastConnectionSeen = 1700000000, ConnectedClient = ProtectClient(), Switch = port.Switch
        };

        var result = _rule.Evaluate(downPort, new List<NetworkInfo>());

        result.Should().NotBeNull();
        result!.Message.Should().Contain("Port is not in use");
    }

    private static UniFiClientResponse ProtectClient() => new()
    {
        Mac = "aa:bb:cc:dd:ee:ff",
        Name = "AI Key",
        IsWired = true,
        ProductLine = "unifi-protect",
        ProductModel = "AI Key"
    };

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
        string? dot1xCtrl = null,
        bool dot1xPortCtrlEnabled = false,
        string? lockedToDeviceMac = null,
        UniFiClientResponse? connectedClient = null,
        string? firmwareVersion = null,
        string[]? seenMacs = null,
        string? switchType = "usw")
    {
        var switchInfo = new SwitchInfo
        {
            Name = switchName,
            Type = switchType,
            FirmwareVersion = firmwareVersion,
            Capabilities = new SwitchCapabilities
            {
                MaxCustomMacAcls = maxMacAcls,
                Dot1xPortCtrlEnabled = dot1xPortCtrlEnabled
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
            ConnectedClient = connectedClient,
            SeenDeviceMacs = new HashSet<string>(seenMacs ?? [], StringComparer.OrdinalIgnoreCase),
            LockedToDeviceMac = lockedToDeviceMac,
            PortProfileId = assignedProfile?.Id,
            Dot1xCtrl = dot1xCtrl,
            Switch = switchInfo,
            AssignedPortProfile = assignedProfile
        };
    }

    #endregion
}
