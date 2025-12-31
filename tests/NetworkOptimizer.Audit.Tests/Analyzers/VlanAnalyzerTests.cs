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
    [InlineData("Cameras", NetworkPurpose.Security)]
    [InlineData("Security", NetworkPurpose.Security)]
    [InlineData("NVR Network", NetworkPurpose.Security)]
    [InlineData("Surveillance", NetworkPurpose.Security)]
    [InlineData("Protect", NetworkPurpose.Security)]
    public void ClassifyNetwork_SecurityPatterns_ReturnsSecurity(string networkName, NetworkPurpose expected)
    {
        var result = _analyzer.ClassifyNetwork(networkName);
        result.Should().Be(expected);
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

    [Fact]
    public void ClassifyNetwork_ExplicitGuestPurpose_ReturnsGuest()
    {
        var result = _analyzer.ClassifyNetwork("Any Name", purpose: "guest");
        result.Should().Be(NetworkPurpose.Guest);
    }

    [Fact]
    public void ClassifyNetwork_Vlan1WithDhcp_ReturnsHome()
    {
        // Use a name that doesn't match any patterns ("work" is a corporate pattern!)
        var result = _analyzer.ClassifyNetwork("MyVlan", vlanId: 1, dhcpEnabled: true);
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_DefaultName_ReturnsHome()
    {
        var result = _analyzer.ClassifyNetwork("default");
        result.Should().Be(NetworkPurpose.Home);
    }

    [Fact]
    public void ClassifyNetwork_LanName_ReturnsHome()
    {
        var result = _analyzer.ClassifyNetwork("LAN");
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

    #region Network Type Check Tests

    [Theory]
    [InlineData("IoT Devices", true)]
    [InlineData("Smart Home", true)]
    [InlineData("Corporate", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsIoTNetwork_VariousInputs_ReturnsExpected(string? networkName, bool expected)
    {
        var result = _analyzer.IsIoTNetwork(networkName);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Cameras", true)]
    [InlineData("Security", true)]
    [InlineData("NVR", true)]
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
}
