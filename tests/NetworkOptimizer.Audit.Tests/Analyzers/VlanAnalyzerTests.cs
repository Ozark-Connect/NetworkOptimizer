using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class VlanAnalyzerTests
{
    private readonly VlanAnalyzer _analyzer;
    private readonly Mock<ILogger<VlanAnalyzer>> _loggerMock;

    public VlanAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<VlanAnalyzer>>();
        _analyzer = new VlanAnalyzer(_loggerMock.Object);
    }

    #region AnalyzeNetworkIsolation Tests

    [Fact]
    public void AnalyzeNetworkIsolation_SecurityNetworkNotIsolated_ReturnsCriticalIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Devices", NetworkPurpose.Security, vlanId: 42, networkIsolationEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("SECURITY_NETWORK_NOT_ISOLATED");
        issues[0].Severity.Should().Be(AuditSeverity.Critical);
        issues[0].ScoreImpact.Should().Be(15);
    }

    [Fact]
    public void AnalyzeNetworkIsolation_SecurityNetworkIsolated_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Devices", NetworkPurpose.Security, vlanId: 42, networkIsolationEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_ManagementNetworkNotIsolated_ReturnsCriticalIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, networkIsolationEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("MGMT_NETWORK_NOT_ISOLATED");
        issues[0].Severity.Should().Be(AuditSeverity.Critical);
        issues[0].ScoreImpact.Should().Be(15);
    }

    [Fact]
    public void AnalyzeNetworkIsolation_ManagementNetworkIsolated_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, networkIsolationEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_IoTNetworkNotIsolated_ReturnsRecommendedIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 64, networkIsolationEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("IOT_NETWORK_NOT_ISOLATED");
        issues[0].Severity.Should().Be(AuditSeverity.Recommended);
        issues[0].ScoreImpact.Should().Be(10);
    }

    [Fact]
    public void AnalyzeNetworkIsolation_IoTNetworkIsolated_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 64, networkIsolationEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_NativeVlan_SkipsCheck()
    {
        // Arrange - Native VLAN (ID 1) should be skipped even if not isolated
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Main Home Network", NetworkPurpose.Home, vlanId: 1, networkIsolationEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeNetworkIsolation_MultipleNetworks_ReturnsAllIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Devices", NetworkPurpose.Security, vlanId: 42, networkIsolationEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, networkIsolationEnabled: false),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 64, networkIsolationEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeNetworkIsolation(networks);

        // Assert
        issues.Should().HaveCount(3);
        issues.Should().Contain(i => i.Type == "SECURITY_NETWORK_NOT_ISOLATED");
        issues.Should().Contain(i => i.Type == "MGMT_NETWORK_NOT_ISOLATED");
        issues.Should().Contain(i => i.Type == "IOT_NETWORK_NOT_ISOLATED");
    }

    #endregion

    #region ClassifyNetwork Tests

    [Theory]
    [InlineData("IoT Devices", NetworkPurpose.IoT)]
    [InlineData("Smart Home", NetworkPurpose.IoT)]
    [InlineData("Home Automation", NetworkPurpose.IoT)]
    [InlineData("Zero Trust", NetworkPurpose.IoT)]
    public void ClassifyNetwork_IoTPatterns_ReturnsIoT(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Entertainment", NetworkPurpose.IoT)]
    [InlineData("Entertainment VLAN", NetworkPurpose.IoT)]
    [InlineData("Streaming Devices", NetworkPurpose.IoT)]
    [InlineData("Home Theater", NetworkPurpose.IoT)]
    [InlineData("Theatre Room", NetworkPurpose.IoT)]
    [InlineData("Recreation Room", NetworkPurpose.IoT)]
    [InlineData("Living Room", NetworkPurpose.IoT)]
    public void ClassifyNetwork_EntertainmentPatterns_ReturnsIoT(string networkName, NetworkPurpose expected)
    {
        // Entertainment networks should classify as IoT - isolated but internet-enabled
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Media Room")]        // Word boundary match for "media"
    [InlineData("Media Devices")]     // Word boundary match for "media"
    [InlineData("AV Equipment")]      // Word boundary match for "av"
    [InlineData("A/V Room")]          // Contains "av" as word
    [InlineData("TV Network")]        // Word boundary match for "tv"
    [InlineData("Smart TV")]          // Word boundary match for "tv"
    public void ClassifyNetwork_EntertainmentWordBoundary_ReturnsIoT(string networkName)
    {
        // Entertainment patterns with word boundary should match IoT
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData("Dave's Network")]    // "Dave" contains "av" but shouldn't match due to word boundary
    [InlineData("AVLAN")]             // "AVLAN" contains "av" but not as a word
    [InlineData("SocialMedia")]       // "SocialMedia" contains "media" but not as a word
    public void ClassifyNetwork_FalsePositivePatterns_DoesNotMatchIoT(string networkName)
    {
        // These patterns should NOT match IoT due to word boundary requirements
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().NotBe(NetworkPurpose.IoT);
    }

    [Theory]
    [InlineData("Cameras", NetworkPurpose.Security)]
    [InlineData("Security", NetworkPurpose.Security)]
    [InlineData("NVR Network", NetworkPurpose.Security)]
    [InlineData("Surveillance", NetworkPurpose.Security)]
    [InlineData("Protect", NetworkPurpose.Security)]
    [InlineData("NoT", NetworkPurpose.Security)]  // Network of Things
    [InlineData("NoT Network", NetworkPurpose.Security)]
    [InlineData("My-NoT-VLAN", NetworkPurpose.Security)]
    public void ClassifyNetwork_SecurityPatterns_ReturnsSecurity(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Fact]
    public void ClassifyNetwork_HotspotDoesNotMatchNoT_ReturnsGuest()
    {
        // "Hotspot" contains "not" but should NOT match as Security due to word boundary check
        // Instead it should match as Guest due to "hotspot" pattern
        var result = _analyzer.ClassifyNetwork("Hotspot");
        result.Should().Be(NetworkPurpose.Guest);
    }

    [Theory]
    [InlineData("Management", NetworkPurpose.Management)]
    [InlineData("MGMT", NetworkPurpose.Management)]
    [InlineData("Admin Network", NetworkPurpose.Management)]
    [InlineData("Infrastructure", NetworkPurpose.Management)]
    public void ClassifyNetwork_ManagementPatterns_ReturnsManagement(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Guest", NetworkPurpose.Guest)]
    [InlineData("Visitors", NetworkPurpose.Guest)]
    [InlineData("Hotspot", NetworkPurpose.Guest)]
    [InlineData("WiFi Hotspot", NetworkPurpose.Guest)]
    public void ClassifyNetwork_GuestPatterns_ReturnsGuest(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Corporate", NetworkPurpose.Corporate)]
    [InlineData("Office", NetworkPurpose.Corporate)]
    [InlineData("Business", NetworkPurpose.Corporate)]
    public void ClassifyNetwork_CorporatePatterns_ReturnsCorporate(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Home", NetworkPurpose.Home)]
    [InlineData("Main", NetworkPurpose.Home)]
    [InlineData("Primary", NetworkPurpose.Home)]
    [InlineData("Family", NetworkPurpose.Home)]
    public void ClassifyNetwork_HomePatterns_ReturnsHome(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Gaming", NetworkPurpose.Home)]
    [InlineData("Gaming VLAN", NetworkPurpose.Home)]
    [InlineData("Gamers Network", NetworkPurpose.Home)]
    [InlineData("Xbox Network", NetworkPurpose.Home)]
    [InlineData("PlayStation VLAN", NetworkPurpose.Home)]
    [InlineData("Nintendo Devices", NetworkPurpose.Home)]
    [InlineData("Console Network", NetworkPurpose.Home)]
    [InlineData("LAN Party", NetworkPurpose.Home)]
    public void ClassifyNetwork_GamingPatterns_ReturnsHome(string networkName, NetworkPurpose expected)
    {
        // Gaming networks should classify as Home because game consoles need UPnP and full network access
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Game Room")]      // Word boundary match for "game"
    [InlineData("Games")]          // Word boundary match for "game"
    [InlineData("Game Network")]   // Word boundary match for "game"
    public void ClassifyNetwork_GameWordBoundary_ReturnsHome(string networkName)
    {
        // "Game" with word boundary should match Home
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_GameChangerCompany_DoesNotMatchHome()
    {
        // "GameChanger" should NOT match "game" due to word boundary requirement
        // It should fall through to Unknown since there's no other pattern match
        var result = _analyzer.ClassifyNetwork("GameChanger Corp");
        result.Should().Be(NetworkPurpose.Unknown);
    }

    [Fact]
    public void ClassifyNetwork_ExplicitGuestPurpose_ReturnsGuest()
    {
        var result = _analyzer.ClassifyNetwork("Any Name", purpose: "guest");
        result.Should().Be(NetworkPurpose.Guest);
    }

    [Fact]
    public void ClassifyNetwork_Vlan1WithUnknownName_ReturnsManagement()
    {
        // VLAN 1 with unknown name defaults to Management (enterprise native VLAN convention)
        var result = _analyzer.ClassifyNetwork("MyVlan", vlanId: 1, dhcpEnabled: true);
        result.Should().Be(NetworkPurpose.Management);
    }

    [Fact]
    public void ClassifyNetwork_Vlan1WithHomeName_ReturnsHome()
    {
        // VLAN 1 with home-like name returns Home (residential setup)
        var result = _analyzer.ClassifyNetwork("Home Network", vlanId: 1, dhcpEnabled: true);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("Default")]
    [InlineData("Default Network")]
    public void ClassifyNetwork_DefaultName_ReturnsHome(string networkName)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_LanName_ReturnsHome()
    {
        var result = _analyzer.ClassifyNetwork("LAN");
        result.Should().Be(NetworkPurpose.Home);
    }

    [Theory]
    [InlineData("Main")]
    [InlineData("main")]
    [InlineData("Main Network")]
    public void ClassifyNetwork_MainName_ReturnsHome(string networkName)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_UnknownName_ReturnsUnknown()
    {
        // Use a name that doesn't match any patterns (avoid "work", "home", "guest", etc.)
        var result = _analyzer.ClassifyNetwork("MyCustomVlan");
        result.Should().Be(NetworkPurpose.Unknown);
    }

    #endregion

    #region Flag-Based Classification Adjustment Tests

    // Home/Corporate networks with no internet should be reclassified

    [Fact]
    public void ClassifyNetwork_HomeNameNoInternetAndIsolated_ReturnsSecurity()
    {
        // A network named "Home" but with no internet and isolated is probably a misnamed security VLAN
        var result = _analyzer.ClassifyNetwork("Home Network",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_HomeNameNoInternetNotIsolated_ReturnsUnknown()
    {
        // A network named "Home" but with no internet and not isolated - unusual, can't determine
        var result = _analyzer.ClassifyNetwork("Home Network",
            networkIsolationEnabled: false, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Unknown);
    }

    [Fact]
    public void ClassifyNetwork_CorporateNameNoInternetAndIsolated_ReturnsSecurity()
    {
        // A network named "Corporate" but with no internet and isolated is probably a misnamed security VLAN
        var result = _analyzer.ClassifyNetwork("Corporate LAN",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_CorporateNameNoInternetNotIsolated_ReturnsUnknown()
    {
        // A network named "Corporate" but with no internet and not isolated - unusual
        var result = _analyzer.ClassifyNetwork("Corporate LAN",
            networkIsolationEnabled: false, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Unknown);
    }

    [Fact]
    public void ClassifyNetwork_PrivateCamerasNoInternetIsolated_ReturnsSecurity()
    {
        // "Private" matches Home pattern, but no internet + isolated = Security
        // This is a common naming pattern for camera VLANs
        var result = _analyzer.ClassifyNetwork("Private Cameras",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_TrustedDevicesNoInternetIsolated_ReturnsSecurity()
    {
        // "Trusted" matches Home pattern, but no internet + isolated = Security
        var result = _analyzer.ClassifyNetwork("Trusted Devices",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    // Home/Corporate with internet should remain unchanged

    [Fact]
    public void ClassifyNetwork_HomeNameWithInternet_ReturnsHome()
    {
        // Home network with internet enabled should stay Home
        var result = _analyzer.ClassifyNetwork("Home Network",
            networkIsolationEnabled: false, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_CorporateNameWithInternet_ReturnsCorporate()
    {
        // Corporate network with internet enabled should stay Corporate
        var result = _analyzer.ClassifyNetwork("Corporate LAN",
            networkIsolationEnabled: false, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.Corporate);
    }

    // Unknown networks with isolation flags should be inferred

    [Fact]
    public void ClassifyNetwork_UnknownNameIsolatedNoInternet_ReturnsSecurity()
    {
        // Unknown name + isolated + no internet = likely security/camera VLAN
        var result = _analyzer.ClassifyNetwork("VLAN42",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_UnknownNameIsolatedWithInternet_ReturnsIoT()
    {
        // Unknown name + isolated + internet = likely IoT (needs internet for updates/cloud)
        var result = _analyzer.ClassifyNetwork("VLAN42",
            networkIsolationEnabled: true, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void ClassifyNetwork_UnknownNameNotIsolated_ReturnsUnknown()
    {
        // Unknown name + not isolated = still unknown
        var result = _analyzer.ClassifyNetwork("VLAN42",
            networkIsolationEnabled: false, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.Unknown);
    }

    // Name patterns should still take precedence when flags match expected behavior

    [Fact]
    public void ClassifyNetwork_SecurityNameIsolatedNoInternet_ReturnsSecurity()
    {
        // Security name + isolated + no internet = Security (flags confirm)
        var result = _analyzer.ClassifyNetwork("Security Cameras",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_IoTNameIsolatedWithInternet_ReturnsIoT()
    {
        // IoT name + isolated + internet = IoT (flags confirm)
        var result = _analyzer.ClassifyNetwork("IoT Devices",
            networkIsolationEnabled: true, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.IoT);
    }

    [Fact]
    public void ClassifyNetwork_ManagementNameIsolated_ReturnsManagement()
    {
        // Management name + isolated = Management (flags confirm)
        var result = _analyzer.ClassifyNetwork("Management",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Management);
    }

    // Null flags should not affect classification

    [Fact]
    public void ClassifyNetwork_HomeNameNullFlags_ReturnsHome()
    {
        // When flags are null, classification should be based on name only
        var result = _analyzer.ClassifyNetwork("Home Network",
            networkIsolationEnabled: null, internetAccessEnabled: null);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_UnknownNameNullFlags_ReturnsUnknown()
    {
        // Unknown name with null flags stays Unknown
        var result = _analyzer.ClassifyNetwork("VLAN42",
            networkIsolationEnabled: null, internetAccessEnabled: null);
        result.Should().Be(NetworkPurpose.Unknown);
    }

    [Fact]
    public void ClassifyNetwork_HomeNameInternetNullIsolationTrue_ReturnsHome()
    {
        // Internet flag is null but isolation is true - no reclassification without internet flag
        var result = _analyzer.ClassifyNetwork("Home Network",
            networkIsolationEnabled: true, internetAccessEnabled: null);
        result.Should().Be(NetworkPurpose.Home);
    }

    // Guest networks should not be affected by flags

    [Fact]
    public void ClassifyNetwork_GuestNameNoInternet_ReturnsGuest()
    {
        // Guest networks are identified by name, flags don't override
        var result = _analyzer.ClassifyNetwork("Guest WiFi",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Guest);
    }

    [Fact]
    public void ClassifyNetwork_ExplicitGuestPurposeNoInternet_ReturnsGuest()
    {
        // UniFi explicit guest purpose takes highest priority
        var result = _analyzer.ClassifyNetwork("Any Network", purpose: "guest",
            networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Guest);
    }

    // Edge case: Name strongly suggests one type but flags contradict

    [Fact]
    public void ClassifyNetwork_SecurityNameNotIsolatedWithInternet_ReturnsSecurity()
    {
        // Security name should still classify as Security even with "wrong" flags
        // (the audit rules will flag this as a configuration issue)
        var result = _analyzer.ClassifyNetwork("Security Cameras",
            networkIsolationEnabled: false, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_IoTNameNotIsolatedNoInternet_ReturnsIoT()
    {
        // IoT name should still classify as IoT even with unusual flags
        var result = _analyzer.ClassifyNetwork("Smart Home Devices",
            networkIsolationEnabled: false, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.IoT);
    }

    // VLAN 1 special handling with flags - VLAN 1 is always Management (enterprise convention)

    [Fact]
    public void ClassifyNetwork_Vlan1DefaultNameNoInternetIsolated_ReturnsManagement()
    {
        // VLAN 1 with no internet + isolated still becomes Management (enterprise native VLAN)
        var result = _analyzer.ClassifyNetwork("Default",
            vlanId: 1, networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Management);
    }

    [Fact]
    public void ClassifyNetwork_Vlan1HomeNameNoInternetIsolated_ReturnsManagement()
    {
        // VLAN 1 with Home-like name but unusual flags still becomes Management
        var result = _analyzer.ClassifyNetwork("Home Network",
            vlanId: 1, networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Management);
    }

    [Fact]
    public void ClassifyNetwork_NonVlan1DefaultNameNoInternetIsolated_ReturnsSecurity()
    {
        // Non-VLAN-1 "Default" with no internet + isolated = Security (misnamed camera VLAN)
        var result = _analyzer.ClassifyNetwork("Default",
            vlanId: 50, networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Security);
    }

    [Fact]
    public void ClassifyNetwork_DefaultNameWithInternet_ReturnsHome()
    {
        // "Default" on VLAN 1 with internet stays Home
        var result = _analyzer.ClassifyNetwork("Default",
            vlanId: 1, networkIsolationEnabled: false, internetAccessEnabled: true);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_Vlan1NoPatternMatchNoInternetIsolated_ReturnsManagement()
    {
        // VLAN 1 with no pattern match becomes Management regardless of flags
        var result = _analyzer.ClassifyNetwork("MyNetwork",
            vlanId: 1, networkIsolationEnabled: true, internetAccessEnabled: false);
        result.Should().Be(NetworkPurpose.Management);
    }

    #endregion

    #region Network Type Check Tests

    [Theory]
    [InlineData("IoT Devices", true)]
    [InlineData("Smart Home", true)]
    [InlineData("Entertainment", true)]       // Entertainment patterns classify as IoT
    [InlineData("Streaming Devices", true)]   // Streaming patterns classify as IoT
    [InlineData("Media Room", true)]          // Media word boundary match
    [InlineData("TV Network", true)]          // TV word boundary match
    [InlineData("Corporate", false)]
    [InlineData("Gaming", false)]             // Gaming is Home, not IoT
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsIoTNetwork_VariousInputs_ReturnsExpected(string? networkName, bool expected)
    {
        var result = _analyzer.IsIoTNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Home", true)]
    [InlineData("Main Network", true)]
    [InlineData("Gaming", true)]              // Gaming patterns classify as Home
    [InlineData("Game Room", true)]           // Game word boundary match
    [InlineData("Xbox Network", true)]        // Xbox is gaming = Home
    [InlineData("PlayStation", true)]         // PlayStation is gaming = Home
    [InlineData("Console VLAN", true)]        // Console is gaming = Home
    [InlineData("Corporate", false)]
    [InlineData("IoT", false)]
    [InlineData("Entertainment", false)]      // Entertainment is IoT, not Home
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsHomeNetwork_VariousInputs_ReturnsExpected(string? networkName, bool expected)
    {
        var result = _analyzer.IsHomeNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Cameras", true)]
    [InlineData("Security", true)]
    [InlineData("NVR", true)]
    [InlineData("NoT", true)]  // Network of Things
    [InlineData("NoT Network", true)]
    [InlineData("Hotspot", false)]  // Contains "not" but word boundary prevents match
    [InlineData("Corporate", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsSecurityNetwork_VariousInputs_ReturnsExpected(string? networkName, bool expected)
    {
        var result = _analyzer.IsSecurityNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Management", true)]
    [InlineData("MGMT", true)]
    [InlineData("Admin", true)]
    [InlineData("Corporate", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsManagementNetwork_VariousInputs_ReturnsExpected(string? networkName, bool expected)
    {
        var result = _analyzer.IsManagementNetwork(networkName);
        result.Should().Be(expected);
    }

    #endregion

    #region Find Network Tests

    [Fact]
    public void FindIoTNetwork_WithIoTNetwork_ReturnsNetwork()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 20),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 30)
        };

        var result = _analyzer.FindIoTNetwork(networks);

        result.Should().NotBeNull();
        result!.Name.Should().Be("IoT");
    }

    [Fact]
    public void FindIoTNetwork_WithoutIoTNetwork_ReturnsNull()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 30)
        };

        var result = _analyzer.FindIoTNetwork(networks);

        result.Should().BeNull();
    }

    [Fact]
    public void FindSecurityNetwork_WithSecurityNetwork_ReturnsNetwork()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate),
            CreateNetwork("Cameras", NetworkPurpose.Security, vlanId: 20),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 30)
        };

        var result = _analyzer.FindSecurityNetwork(networks);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Cameras");
    }

    [Fact]
    public void FindSecurityNetwork_WithoutSecurityNetwork_ReturnsNull()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 30)
        };

        var result = _analyzer.FindSecurityNetwork(networks);

        result.Should().BeNull();
    }

    #endregion

    #region GetNetworkDisplay Tests

    [Fact]
    public void GetNetworkDisplay_RegularVlan_ReturnsNameAndVlan()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate, vlanId: 10);

        var result = _analyzer.GetNetworkDisplay(network);

        result.Should().Be("Corporate (10)");
    }

    [Fact]
    public void GetNetworkDisplay_NativeVlan_ReturnsNameVlanAndNative()
    {
        var network = CreateNetwork("Default", NetworkPurpose.Home, vlanId: 1);

        var result = _analyzer.GetNetworkDisplay(network);

        result.Should().Be("Default (1 (native))");
    }

    #endregion

    #region AnalyzeDnsConfiguration Tests

    [Fact]
    public void AnalyzeDnsConfiguration_SharedDns_ReturnsIssue()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate, DnsServers = new List<string> { "192.168.1.1" } },
            new() { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT, DnsServers = new List<string> { "192.168.1.1" } }
        };

        var result = _analyzer.AnalyzeDnsConfiguration(networks);

        result.Should().NotBeEmpty();
        result.First().Type.Should().Be("DNS_LEAKAGE");
    }

    [Fact]
    public void AnalyzeDnsConfiguration_DifferentDns_ReturnsNoIssues()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Corporate", VlanId = 10, Purpose = NetworkPurpose.Corporate, DnsServers = new List<string> { "192.168.1.1" } },
            new() { Id = "2", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT, DnsServers = new List<string> { "8.8.8.8" } }
        };

        var result = _analyzer.AnalyzeDnsConfiguration(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeDnsConfiguration_NoDnsServers_ReturnsNoIssues()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 20)
        };

        var result = _analyzer.AnalyzeDnsConfiguration(networks);

        result.Should().BeEmpty();
    }

    #endregion

    #region AnalyzeManagementVlanDhcp Tests

    [Fact]
    public void AnalyzeManagementVlanDhcp_DhcpEnabled_ReturnsIssue()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, dhcpEnabled: true)
        };

        var result = _analyzer.AnalyzeManagementVlanDhcp(networks);

        result.Should().NotBeEmpty();
        result.First().Type.Should().Be("MGMT_DHCP_ENABLED");
        result.First().Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void AnalyzeManagementVlanDhcp_DhcpDisabled_ReturnsNoIssues()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, dhcpEnabled: false)
        };

        var result = _analyzer.AnalyzeManagementVlanDhcp(networks);

        result.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementVlanDhcp_NativeVlan_SkipsCheck()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1, dhcpEnabled: true)
        };

        var result = _analyzer.AnalyzeManagementVlanDhcp(networks);

        result.Should().BeEmpty();
    }

    #endregion

    #region AnalyzeGatewayConfiguration Tests

    [Fact]
    public void AnalyzeGatewayConfiguration_IoTWithRouting_ReturnsIssue()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT, AllowsRouting = true }
        };

        var result = _analyzer.AnalyzeGatewayConfiguration(networks);

        result.Should().NotBeEmpty();
        result.First().Type.Should().Be("ROUTING_ENABLED");
    }

    [Fact]
    public void AnalyzeGatewayConfiguration_GuestWithRouting_ReturnsIssue()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest, AllowsRouting = true }
        };

        var result = _analyzer.AnalyzeGatewayConfiguration(networks);

        result.Should().NotBeEmpty();
        result.First().Type.Should().Be("ROUTING_ENABLED");
    }

    [Fact]
    public void AnalyzeGatewayConfiguration_NoRouting_ReturnsNoIssues()
    {
        var networks = new List<NetworkInfo>
        {
            new() { Id = "1", Name = "IoT", VlanId = 40, Purpose = NetworkPurpose.IoT, AllowsRouting = false },
            new() { Id = "2", Name = "Guest", VlanId = 50, Purpose = NetworkPurpose.Guest, AllowsRouting = false }
        };

        var result = _analyzer.AnalyzeGatewayConfiguration(networks);

        result.Should().BeEmpty();
    }

    #endregion

    #region AnalyzeInternetAccess Tests

    [Fact]
    public void AnalyzeInternetAccess_SecurityNetworkHasInternet_ReturnsCriticalIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Devices", NetworkPurpose.Security, vlanId: 42, internetAccessEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("SECURITY_NETWORK_HAS_INTERNET");
        issues[0].Severity.Should().Be(AuditSeverity.Critical);
        issues[0].ScoreImpact.Should().Be(15);
    }

    [Fact]
    public void AnalyzeInternetAccess_SecurityNetworkNoInternet_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Devices", NetworkPurpose.Security, vlanId: 42, internetAccessEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_ManagementNetworkHasInternet_ReturnsRecommendedIssue()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, internetAccessEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("MGMT_NETWORK_HAS_INTERNET");
        issues[0].Severity.Should().Be(AuditSeverity.Recommended);
        issues[0].ScoreImpact.Should().Be(5);
    }

    [Fact]
    public void AnalyzeInternetAccess_ManagementNetworkNoInternet_ReturnsNoIssues()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 99, internetAccessEnabled: false)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_IoTNetworkHasInternet_ReturnsNoIssues()
    {
        // Arrange - IoT networks are allowed to have internet access
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 64, internetAccessEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_NativeVlan_SkipsCheck()
    {
        // Arrange - Native VLAN should be skipped
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Main Home Network", NetworkPurpose.Security, vlanId: 1, internetAccessEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInternetAccess_HomeNetworkHasInternet_ReturnsNoIssues()
    {
        // Arrange - Home networks are expected to have internet
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Main Home Network", NetworkPurpose.Home, vlanId: 10, internetAccessEnabled: true)
        };

        // Act
        var issues = _analyzer.AnalyzeInternetAccess(networks);

        // Assert
        issues.Should().BeEmpty();
    }

    #endregion

    #region Helper Methods

    private static NetworkInfo CreateNetwork(
        string name,
        NetworkPurpose purpose,
        int vlanId = 10,
        bool networkIsolationEnabled = false,
        bool internetAccessEnabled = true,
        bool dhcpEnabled = true)
    {
        return new NetworkInfo
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = $"192.168.{vlanId}.0/24",
            Gateway = $"192.168.{vlanId}.1",
            DhcpEnabled = dhcpEnabled,
            NetworkIsolationEnabled = networkIsolationEnabled,
            InternetAccessEnabled = internetAccessEnabled
        };
    }

    #endregion

    #region AnalyzeInfrastructureVlanPlacement Tests

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_IdealNetwork_SwitchOnManagement_NoIssues()
    {
        // Arrange - Ideal sequential VLAN setup
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1),
            CreateNetwork("Home", NetworkPurpose.Home, vlanId: 2),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 3),
            CreateNetwork("Security", NetworkPurpose.Security, vlanId: 4),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 5)
        };

        // Switch on Management VLAN (192.168.1.x)
        var deviceJson = CreateDeviceJson("usw", "Core Switch", "192.168.1.10");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_IdealNetwork_SwitchOnHomeVlan_FlagsCritical()
    {
        // Arrange - Ideal sequential VLAN setup
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1),
            CreateNetwork("Home", NetworkPurpose.Home, vlanId: 2),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 3),
            CreateNetwork("Security", NetworkPurpose.Security, vlanId: 4),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 5)
        };

        // Switch on Home VLAN (192.168.2.x) - wrong!
        var deviceJson = CreateDeviceJson("usw", "Desk Switch", "192.168.2.15");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.InfraNotOnMgmt);
        issues[0].Severity.Should().Be(AuditSeverity.Critical);
        issues[0].Message.Should().Contain("Switch");
        issues[0].Message.Should().Contain("Home");
        issues[0].RecommendedNetwork.Should().Be("Management");
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_NonIdealVlans_APOnIoTVlan_FlagsCritical()
    {
        // Arrange - Non-sequential VLANs like real-world setups (99, 1, 42, 64)
        var networks = new List<NetworkInfo>
        {
            new() { Id = "mgmt", Name = "Management", VlanId = 99, Purpose = NetworkPurpose.Management, Subnet = "192.168.99.0/24", Gateway = "192.168.99.1" },
            new() { Id = "home", Name = "Main Network", VlanId = 1, Purpose = NetworkPurpose.Home, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "sec", Name = "Cameras", VlanId = 42, Purpose = NetworkPurpose.Security, Subnet = "192.168.42.0/24", Gateway = "192.168.42.1" },
            new() { Id = "iot", Name = "Smart Devices", VlanId = 64, Purpose = NetworkPurpose.IoT, Subnet = "192.168.64.0/24", Gateway = "192.168.64.1" }
        };

        // AP on IoT VLAN (192.168.64.x) - wrong!
        var deviceJson = CreateDeviceJson("uap", "Living Room AP", "192.168.64.20");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.InfraNotOnMgmt);
        issues[0].Message.Should().Contain("Access Point");
        issues[0].Message.Should().Contain("Smart Devices");
        issues[0].CurrentNetwork.Should().Be("Smart Devices");
        issues[0].CurrentVlan.Should().Be(64);
        issues[0].RecommendedNetwork.Should().Be("Management");
        issues[0].RecommendedVlan.Should().Be(99);
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_GatewayOnAnyVlan_NoIssues()
    {
        // Arrange - Gateway devices are skipped
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1),
            CreateNetwork("Home", NetworkPurpose.Home, vlanId: 2)
        };

        // Gateway on Home VLAN - still OK because gateways are exempt
        var deviceJson = CreateDeviceJson("udm", "Dream Machine", "192.168.2.1");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_MultipleDevicesWrongVlan_FlagsAll()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1),
            CreateNetwork("Home", NetworkPurpose.Home, vlanId: 2),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 3)
        };

        // Multiple devices on wrong VLANs
        var deviceJson = CreateMultipleDevicesJson(
            ("usw", "Switch A", "192.168.2.10"),  // Home VLAN - wrong
            ("uap", "AP A", "192.168.3.10"),      // IoT VLAN - wrong
            ("usw", "Switch B", "192.168.1.20")   // Management - OK
        );

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(2);
        issues.Should().Contain(i => i.Message.Contains("Switch A"));
        issues.Should().Contain(i => i.Message.Contains("AP A"));
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_NoManagementNetwork_NoIssues()
    {
        // Arrange - No Management network defined
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, vlanId: 1),
            CreateNetwork("IoT", NetworkPurpose.IoT, vlanId: 2)
        };

        var deviceJson = CreateDeviceJson("usw", "Switch", "192.168.2.10");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert - Can't flag if no Management network exists
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_CellularModemOnGuestVlan_FlagsCritical()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, vlanId: 1),
            CreateNetwork("Guest", NetworkPurpose.Guest, vlanId: 50)
        };

        // Cellular modem on Guest VLAN - wrong!
        var deviceJson = CreateDeviceJson("umbb", "LTE Backup", "192.168.50.5");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Message.Should().Contain("Cellular Modem");
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_Vlan1AsDefault_SwitchOnVlan1_NoIssues()
    {
        // Arrange - VLAN 1 named "Default" but classified as Management (common UniFi setup)
        var networks = new List<NetworkInfo>
        {
            new() { Id = "default", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Management, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "home", Name = "Home Network", VlanId = 10, Purpose = NetworkPurpose.Home, Subnet = "192.168.10.0/24", Gateway = "192.168.10.1" },
            new() { Id = "iot", Name = "IoT", VlanId = 20, Purpose = NetworkPurpose.IoT, Subnet = "192.168.20.0/24", Gateway = "192.168.20.1" }
        };

        // Switch on VLAN 1 (Default/Management) - should be OK
        var deviceJson = CreateDeviceJson("usw", "Core Switch", "192.168.1.50");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_Vlan1AsLan_SwitchOnVlan1_NoIssues()
    {
        // Arrange - VLAN 1 named "LAN" but classified as Management
        var networks = new List<NetworkInfo>
        {
            new() { Id = "lan", Name = "LAN", VlanId = 1, Purpose = NetworkPurpose.Management, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "guest", Name = "Guest", VlanId = 30, Purpose = NetworkPurpose.Guest, Subnet = "192.168.30.0/24", Gateway = "192.168.30.1" }
        };

        // AP on VLAN 1 (LAN/Management) - should be OK
        var deviceJson = CreateDeviceJson("uap", "Office AP", "192.168.1.100");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_Vlan1AsDefault_SwitchOnHomeVlan_FlagsCritical()
    {
        // Arrange - VLAN 1 is "Default" (Management), but switch is on Home VLAN
        var networks = new List<NetworkInfo>
        {
            new() { Id = "default", Name = "Default", VlanId = 1, Purpose = NetworkPurpose.Management, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "home", Name = "Home Network", VlanId = 10, Purpose = NetworkPurpose.Home, Subnet = "192.168.10.0/24", Gateway = "192.168.10.1" }
        };

        // Switch on Home VLAN - wrong!
        var deviceJson = CreateDeviceJson("usw", "Desk Switch", "192.168.10.25");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.InfraNotOnMgmt);
        issues[0].CurrentNetwork.Should().Be("Home Network");
        issues[0].RecommendedNetwork.Should().Be("Default");
        issues[0].RecommendedVlan.Should().Be(1);
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_Vlan1NotManagement_UsesExplicitMgmtVlan()
    {
        // Arrange - VLAN 1 is Home, VLAN 99 is explicit Management (user's setup style)
        var networks = new List<NetworkInfo>
        {
            new() { Id = "home", Name = "Main Network", VlanId = 1, Purpose = NetworkPurpose.Home, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "mgmt", Name = "Management", VlanId = 99, Purpose = NetworkPurpose.Management, Subnet = "192.168.99.0/24", Gateway = "192.168.99.1" },
            new() { Id = "iot", Name = "IoT", VlanId = 64, Purpose = NetworkPurpose.IoT, Subnet = "192.168.64.0/24", Gateway = "192.168.64.1" }
        };

        // Switch on VLAN 1 (Home, not Management) - should be flagged
        var deviceJson = CreateDeviceJson("usw", "Living Room Switch", "192.168.1.30");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].CurrentNetwork.Should().Be("Main Network");
        issues[0].CurrentVlan.Should().Be(1);
        issues[0].RecommendedNetwork.Should().Be("Management");
        issues[0].RecommendedVlan.Should().Be(99);
    }

    [Fact]
    public void AnalyzeInfrastructureVlanPlacement_Vlan1NotManagement_SwitchOnMgmtVlan99_NoIssues()
    {
        // Arrange - VLAN 1 is Home, VLAN 99 is Management
        var networks = new List<NetworkInfo>
        {
            new() { Id = "home", Name = "Main Network", VlanId = 1, Purpose = NetworkPurpose.Home, Subnet = "192.168.1.0/24", Gateway = "192.168.1.1" },
            new() { Id = "mgmt", Name = "Management", VlanId = 99, Purpose = NetworkPurpose.Management, Subnet = "192.168.99.0/24", Gateway = "192.168.99.1" }
        };

        // Switch correctly on Management VLAN 99
        var deviceJson = CreateDeviceJson("usw", "Core Switch", "192.168.99.10");

        // Act
        var issues = _analyzer.AnalyzeInfrastructureVlanPlacement(deviceJson, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    private static System.Text.Json.JsonElement CreateDeviceJson(string type, string name, string ip)
    {
        var json = $$"""
        {
            "data": [
                {
                    "type": "{{type}}",
                    "name": "{{name}}",
                    "ip": "{{ip}}",
                    "mac": "aa:bb:cc:dd:ee:ff"
                }
            ]
        }
        """;
        return System.Text.Json.JsonDocument.Parse(json).RootElement;
    }

    private static System.Text.Json.JsonElement CreateMultipleDevicesJson(params (string type, string name, string ip)[] devices)
    {
        var deviceJsons = devices.Select(d => $$"""
                {
                    "type": "{{d.type}}",
                    "name": "{{d.name}}",
                    "ip": "{{d.ip}}",
                    "mac": "{{Guid.NewGuid():N}}"
                }
        """);

        var json = $$"""
        {
            "data": [
                {{string.Join(",\n", deviceJsons)}}
            ]
        }
        """;
        return System.Text.Json.JsonDocument.Parse(json).RootElement;
    }

    #endregion
}
