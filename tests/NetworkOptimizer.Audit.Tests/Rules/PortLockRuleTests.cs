using FluentAssertions;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Rules;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Rules;

public class PortLockRuleTests
{
    private const string SupportedApp = "10.6.106";
    private const string SupportedFirmware = "7.6.2.17186";

    private readonly PortLockRule _rule;

    public PortLockRuleTests()
    {
        _rule = new PortLockRule();
        _rule.SetNetworkApplicationVersion(SupportedApp);
    }

    #region Rule Properties

    [Fact]
    public void RuleId_IsPortLock()
    {
        _rule.RuleId.Should().Be(IssueTypes.PortLock);
        _rule.RuleId.Should().Be("PORT-LOCK-001");
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

    #region Ports That Should Be Ignored

    [Fact]
    public void Evaluate_PortDown_ReturnsNull()
    {
        var port = CreatePort(isUp: false, client: ProtectClient());

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Theory]
    [InlineData("disabled")]
    public void Evaluate_DisabledPort_ReturnsNull(string forwardMode)
    {
        var port = CreatePort(forwardMode: forwardMode, client: ProtectClient());

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_UplinkPort_ReturnsNull()
    {
        var port = CreatePort(isUplink: true, connectedDeviceType: "usw");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_WanPort_ReturnsNull()
    {
        var port = CreatePort(isWan: true, connectedDeviceType: "umbb");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MirrorDestination_ReturnsNull()
    {
        var port = CreatePort(opMode: "mirror", client: ProtectClient());

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_OrdinaryClient_ReturnsNull()
    {
        var client = new UniFiClientResponse { Mac = "aa:bb:cc:dd:ee:01", Name = "Desktop", IsWired = true };
        var port = CreatePort(client: client);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_NoClientNoDevice_ReturnsNull()
    {
        var port = CreatePort();

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_AlreadyLocked_ReturnsNull()
    {
        var port = CreatePort(client: ProtectClient(), lockedToDeviceMac: "aa:bb:cc:dd:ee:ff");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MacRestricted_ReturnsInformationalWithNoScoreImpact()
    {
        var port = CreatePort(client: ProtectClient(), portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff"]);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-LOCK-001");
        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0);
        result.Message.Should().Contain("MAC-restricted for AI Key (UniFi Protect)");
        result.Message.Should().Contain("can replace the MAC list");
        result.RecommendedAction.Should().Contain("If you prefer it");
    }

    [Fact]
    public void Evaluate_MacListWithSeveralMacs_ReturnsNull()
    {
        // The list admits more than the UniFi device; a lock would block the rest
        var port = CreatePort(client: ProtectClient(), portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:01"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MacRestrictedSharedPort_ReturnsNull()
    {
        var port = CreatePort(client: ProtectClient(), portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff"],
            seenMacs: ["aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:01"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_PortSecurityEnabledNoMacs_ReturnsInformational()
    {
        var port = CreatePort(client: ProtectClient(), portSecurityEnabled: true);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Theory]
    [InlineData("10.6.100", "7.6.2.17186")]
    [InlineData("10.6.106", "7.5.15.17146")]
    public void Evaluate_MacRestricted_LockUnavailable_ReturnsNull(string appVersion, string firmware)
    {
        // Nothing to offer when the lock is not available on this switch
        _rule.SetNetworkApplicationVersion(appVersion);
        var port = CreatePort(client: ProtectClient(), portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff"], firmwareVersion: firmware);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MacRestrictedOrdinaryClient_ReturnsNull()
    {
        // A MAC list on a non-UniFi device has no lock alternative
        var client = new UniFiClientResponse { Mac = "aa:bb:cc:dd:ee:01", Name = "Printer", IsWired = true };
        var port = CreatePort(client: client, portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:01"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("mac_based")]
    [InlineData("multi_host")]
    public void Evaluate_Dot1xSecured_ReturnsNull(string dot1xCtrl)
    {
        var port = CreatePort(client: ProtectClient(), dot1xCtrl: dot1xCtrl);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Theory]
    [InlineData("all", "uap")]        // AP on a trunk profile
    [InlineData("custom", "uap")]     // AP on a custom profile with a native network (access port shape)
    [InlineData("all", "usw")]        // downstream switch
    [InlineData("all", "umbb")]       // endpoint UniFi device, but on a trunk the MAC rule never covers
    public void Evaluate_ProfiledPortOutsideMacRule_ReturnsInformationalNamingProfile(string forwardMode, string deviceType)
    {
        // The profile blocks the lock, and nothing else suggests port security here
        var port = CreatePort(forwardMode: forwardMode, nativeNetworkId: "net-1", connectedDeviceType: deviceType, portProfileId: "prof-trunk");

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-LOCK-001");
        result.Severity.Should().Be(AuditSeverity.Informational);
        result.ScoreImpact.Should().Be(0);
        // Profile id only (not resolved to a profile): generic wording
        result.Message.Should().StartWith("Port could be locked to ");
        result.Message.Should().EndWith(" if its Ethernet Port Profile is removed");
        result.RecommendedAction.Should().Be(
            "Lock Port to UniFi Device can't be enabled on a port that uses an Ethernet Port Profile. " +
            "If you'd rather lock this port than share the profile with other ports, remove the profile in Port Manager, " +
            "then enable Lock Port to UniFi Device.");
    }

    [Fact]
    public void Evaluate_ProfiledApPort_NamesDeviceAndProfile()
    {
        var profile = new UniFiPortProfile { Id = "prof-trunk", Name = "AP Trunk", Forward = "all" };
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap", assignedProfile: profile);
        port.ConnectedDeviceName = "[AP] Back Yard";

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Be("Port could be locked to [AP] Back Yard (UniFi access point) if the \"AP Trunk\" Ethernet Port Profile is removed");
    }

    [Theory]
    [InlineData("uxg", "7.6.2.17186")]   // gateway, even if its firmware number cleared the check
    [InlineData("ucg", "8.0.1")]
    [InlineData("udm", "9.0.0")]
    [InlineData("uap", "8.8.8.20113")]   // in-wall AP with switch ports: 8.x would pass the version check
    [InlineData(null, "7.6.2.17186")]
    public void Evaluate_NonSwitchDevice_ReturnsNull(string? switchType, string firmware)
    {
        // Lock Port to UniFi Device is USW only
        var port = CreatePort(client: ProtectClient(), switchType: switchType, firmwareVersion: firmware);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ProfiledApPortOnGateway_ReturnsNull()
    {
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap", portProfileId: "prof-trunk", switchType: "uxg", firmwareVersion: "7.6.2");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ProfiledUplinkPort_ReturnsNull()
    {
        // Uplinks cannot take the lock, so their profile is never questioned
        var port = CreatePort(forwardMode: "all", isUplink: true, connectedDeviceType: "usw", portProfileId: "prof-uplink");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ProfiledAccessPortWithEndpointDevice_ReturnsNull()
    {
        // MacRestrictionRule carries this port with its remove-the-profile copy
        var port = CreatePort(forwardMode: "native", client: ProtectClient(), portProfileId: "prof-camera");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ProfiledAndMacRestricted_ReturnsNull()
    {
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap", portProfileId: "prof-trunk",
            portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ProfiledApPort_OldFirmware_ReturnsNull()
    {
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap", portProfileId: "prof-trunk", firmwareVersion: "7.5.15.17146");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_ApPortNoProfile_ReturnsRecommended()
    {
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap");

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
        result.Message.Should().Be("Port should be locked to the connected UniFi access point with Lock Port to UniFi Device");
    }

    [Fact]
    public void Evaluate_IntentionalUnrestrictedProfile_ReturnsNull()
    {
        var profile = new UniFiPortProfile { Id = "prof-1", Name = "Any Device", Forward = "native", PortSecurityEnabled = false, TaggedVlanMgmt = "block_all" };
        var port = CreatePort(client: ProtectClient(), assignedProfile: profile);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    #endregion

    #region Version Gate

    [Theory]
    [InlineData("10.6.100")]
    [InlineData("10.6.97")]
    [InlineData("10.5.120")]
    [InlineData(null)]
    [InlineData("")]
    public void Evaluate_NetworkApplicationTooOldOrUnknown_ReturnsNull(string? appVersion)
    {
        _rule.SetNetworkApplicationVersion(appVersion);
        var port = CreatePort(client: ProtectClient());

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Theory]
    [InlineData("7.6.1.17000")]
    [InlineData("7.5.15.17146")]
    [InlineData("2.1.6.762")]
    [InlineData(null)]
    public void Evaluate_SwitchFirmwareTooOldOrUnknown_ReturnsNull(string? firmware)
    {
        var port = CreatePort(client: ProtectClient(), firmwareVersion: firmware);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MinimumVersionsExactly_ReturnsIssue()
    {
        _rule.SetNetworkApplicationVersion("10.6.101");
        var port = CreatePort(client: ProtectClient(), firmwareVersion: "7.6.2");

        _rule.Evaluate(port, []).Should().NotBeNull();
    }

    #endregion

    #region Ports That Should Trigger Issue

    [Fact]
    public void Evaluate_ProtectDeviceUnlocked_ReturnsIssue()
    {
        var networks = new List<NetworkInfo> { new() { Id = "net-1", Name = "Security", VlanId = 42 } };
        var port = CreatePort(client: ProtectClient(), nativeNetworkId: "net-1");

        var result = _rule.Evaluate(port, networks);

        result.Should().NotBeNull();
        result!.Type.Should().Be("PORT-LOCK-001");
        result.Severity.Should().Be(AuditSeverity.Recommended);
        result.ScoreImpact.Should().Be(3);
        result.Port.Should().Be("3");
        result.Message.Should().Contain("Lock Port to UniFi Device");
        result.Message.Should().Contain("AI Key (UniFi Protect)");
        result.RecommendedAction.Should().StartWith("Only AI Key (UniFi Protect) has used this port.");
        result.RecommendedAction.Should().Contain("Lock Port to UniFi Device");
        result.Metadata!["network"].Should().Be("Security");
        result.Metadata["device_type"].Should().Be("protect");
    }

    [Fact]
    public void Evaluate_LagParent_ReturnsNull()
    {
        // UniFi does not offer Lock Port to UniFi Device on a LAG
        var port = CreatePort(forwardMode: "all", opMode: "aggregate", connectedDeviceType: "usw");

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_MacRestrictedLagParent_ReturnsNull()
    {
        var port = CreatePort(opMode: "aggregate", client: ProtectClient(), portSecurityEnabled: true, allowedMacs: ["aa:bb:cc:dd:ee:ff"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_SharedPortHistory_ReturnsNull()
    {
        // Three distinct MACs in the window: MacRestrictionRule carries this port and its score
        var port = CreatePort(client: ProtectClient(), seenMacs: ["aa:bb:cc:dd:ee:ff", "aa:bb:cc:dd:ee:01", "aa:bb:cc:dd:ee:02"]);

        _rule.Evaluate(port, []).Should().BeNull();
    }

    [Fact]
    public void Evaluate_SingleDeviceHistory_StaysRecommended()
    {
        var port = CreatePort(client: ProtectClient(), seenMacs: ["aa:bb:cc:dd:ee:ff"]);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void Evaluate_NetworkClientDevice_NamesTheApp()
    {
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:02", Name = "Console", IsWired = true, ProductLine = "unifi-network", ProductModel = "CloudKey+"
        };
        var port = CreatePort(client: client);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("Console (UniFi Network)");
    }

    [Fact]
    public void Evaluate_UcoreDriveDevice_NamesTheApp()
    {
        // NAS surfaced through the ucore block with the uppercase product line
        var client = new UniFiClientResponse
        {
            Mac = "aa:bb:cc:dd:ee:03", Name = "Storage", IsWired = true,
            UnifiDeviceInfoFromUcore = new UniFiUcoreDeviceInfo { ProductLine = "DRIVE", ProductShortname = "UNAS Pro" }
        };
        var port = CreatePort(client: client);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("Storage (UniFi Drive)");
        result.Metadata!["device_type"].Should().Be("drive");
    }

    [Fact]
    public void Evaluate_AccessPointOnTrunkPort_ReturnsIssue()
    {
        // AP downlinks are trunks; the lock does not depend on VLAN mode
        var port = CreatePort(forwardMode: "all", connectedDeviceType: "uap");

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("the connected UniFi access point");
        result.Metadata!["device_type"].Should().Be("uap");
    }

    [Theory]
    [InlineData("usw", "switch")]
    [InlineData("ubb", "bridge")]
    [InlineData("uxg", "gateway")]
    [InlineData("umbb", "modem")]
    [InlineData("uck", "Cloud Key")]
    [InlineData("uas", "Cloud Key")]
    [InlineData("usp", "power device")]
    [InlineData("unas", "NAS")]
    [InlineData("unvr", "device")]
    public void Evaluate_NetworkDeviceTypes_DescribedByRole(string deviceType, string role)
    {
        var port = CreatePort(connectedDeviceType: deviceType);

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Contain($"the connected UniFi {role}");
    }

    [Fact]
    public void Evaluate_ClientNamePreferredOverDeviceType()
    {
        // A device in the uplink table that also shows up as a named client
        var client = new UniFiClientResponse { Mac = "aa:bb:cc:dd:ee:04", Name = "Modem", IsWired = true };
        var port = CreatePort(client: client, connectedDeviceType: "umbb");

        var result = _rule.Evaluate(port, []);

        result.Should().NotBeNull();
        result!.Message.Should().Contain("locked to Modem with");
    }

    #endregion

    #region Helpers

    private static UniFiClientResponse ProtectClient() => new()
    {
        Mac = "aa:bb:cc:dd:ee:ff",
        Name = "AI Key",
        Hostname = "ai-key",
        IsWired = true,
        ProductLine = "unifi-protect",
        ProductModel = "AI Key"
    };

    private static PortInfo CreatePort(
        bool isUp = true,
        string forwardMode = "native",
        bool isUplink = false,
        bool isWan = false,
        string? opMode = "switch",
        bool portSecurityEnabled = false,
        List<string>? allowedMacs = null,
        string? lockedToDeviceMac = null,
        string? dot1xCtrl = null,
        string? nativeNetworkId = null,
        string? connectedDeviceType = null,
        UniFiClientResponse? client = null,
        string? firmwareVersion = SupportedFirmware,
        UniFiPortProfile? assignedProfile = null,
        string? portProfileId = null,
        string[]? seenMacs = null,
        string? switchType = "usw")
    {
        var switchInfo = new SwitchInfo
        {
            Name = "Test Switch",
            MacAddress = "00:11:22:33:44:55",
            Type = switchType,
            FirmwareVersion = firmwareVersion,
            Capabilities = new SwitchCapabilities { MaxCustomMacAcls = 32 }
        };

        return new PortInfo
        {
            PortIndex = 3,
            Name = "Port 3",
            IsUp = isUp,
            ForwardMode = forwardMode,
            OpMode = opMode,
            IsUplink = isUplink,
            IsWan = isWan,
            PortSecurityEnabled = portSecurityEnabled,
            AllowedMacAddresses = allowedMacs,
            LockedToDeviceMac = lockedToDeviceMac,
            Dot1xCtrl = dot1xCtrl,
            NativeNetworkId = nativeNetworkId,
            ConnectedDeviceType = connectedDeviceType,
            ConnectedClient = client,
            SeenDeviceMacs = new HashSet<string>(seenMacs ?? (client?.Mac is { } m ? [m] : []), StringComparer.OrdinalIgnoreCase),
            AssignedPortProfile = assignedProfile,
            PortProfileId = portProfileId ?? assignedProfile?.Id,
            Switch = switchInfo
        };
    }

    #endregion
}
