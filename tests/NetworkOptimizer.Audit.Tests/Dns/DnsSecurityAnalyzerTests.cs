using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Dns;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Dns;

public class DnsSecurityAnalyzerTests
{
    private readonly DnsSecurityAnalyzer _analyzer;
    private readonly Mock<ILogger<DnsSecurityAnalyzer>> _loggerMock;

    public DnsSecurityAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<DnsSecurityAnalyzer>>();
        _analyzer = new DnsSecurityAnalyzer(_loggerMock.Object);
    }

    #region DeviceName on Issues Tests

    [Fact]
    public void Analyze_DnsIssues_HaveGatewayDeviceName()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Dream Machine Pro",
                IsGateway = true,
                Model = "UDM-Pro"
            }
        };

        // Act - analyze with no settings/firewall data to trigger DNS issues
        var result = _analyzer.Analyze(
            settingsData: null,
            firewallData: null,
            switches: switches,
            networks: null);

        // Assert - all issues should have DeviceName set to gateway
        result.Issues.Should().NotBeEmpty("DNS issues should be generated when no DoH/firewall config");

        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().Be("Dream Machine Pro",
                $"Issue type '{issue.Type}' should have DeviceName set to gateway");
        }
    }

    [Fact]
    public void Analyze_NoGateway_IssuesHaveNullDeviceName()
    {
        // Arrange - no switches provided
        var result = _analyzer.Analyze(
            settingsData: null,
            firewallData: null,
            switches: null,
            networks: null);

        // Assert - issues should still be generated, but DeviceName will be null
        result.Issues.Should().NotBeEmpty();

        // When no gateway is available, DeviceName should be null (not crash)
        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().BeNull(
                $"Issue type '{issue.Type}' should have null DeviceName when no gateway available");
        }
    }

    [Fact]
    public void Analyze_MultipleDevices_UsesGatewayName()
    {
        // Arrange - multiple devices, only one is gateway
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo
            {
                Name = "Office Switch",
                IsGateway = false,
                Model = "USW-24"
            },
            new SwitchInfo
            {
                Name = "Cloud Gateway Ultra",
                IsGateway = true,
                Model = "UCG-Ultra"
            },
            new SwitchInfo
            {
                Name = "Garage Switch",
                IsGateway = false,
                Model = "USW-Lite-8"
            }
        };

        // Act
        var result = _analyzer.Analyze(
            settingsData: null,
            firewallData: null,
            switches: switches,
            networks: null);

        // Assert - should use the gateway's name, not other switches
        result.Issues.Should().NotBeEmpty();

        foreach (var issue in result.Issues)
        {
            issue.DeviceName.Should().Be("Cloud Gateway Ultra");
        }
    }

    #endregion

    #region Issue Generation Tests

    [Fact]
    public void Analyze_NoDoHConfigured_GeneratesRecommendedIssue()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = _analyzer.Analyze(
            settingsData: null,
            firewallData: null,
            switches: switches,
            networks: null);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Type == "DNS_NO_DOH" &&
            i.Severity == AuditSeverity.Recommended);
    }

    [Fact]
    public void Analyze_NoPort53Block_GeneratesCriticalIssue()
    {
        // Arrange
        var switches = new List<SwitchInfo>
        {
            new SwitchInfo { Name = "Gateway", IsGateway = true }
        };

        // Act
        var result = _analyzer.Analyze(
            settingsData: null,
            firewallData: null,
            switches: switches,
            networks: null);

        // Assert
        result.Issues.Should().Contain(i =>
            i.Type == "DNS_NO_53_BLOCK" &&
            i.Severity == AuditSeverity.Critical &&
            i.DeviceName == "Gateway");
    }

    #endregion
}
