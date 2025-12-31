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
