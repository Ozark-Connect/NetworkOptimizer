using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class FirewallRuleAnalyzerTests
{
    private readonly FirewallRuleAnalyzer _analyzer;
    private readonly Mock<ILogger<FirewallRuleAnalyzer>> _loggerMock;

    public FirewallRuleAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<FirewallRuleAnalyzer>>();
        _analyzer = new FirewallRuleAnalyzer(_loggerMock.Object);
    }

    #region AnalyzeManagementNetworkFirewallAccess Tests

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_NoIsolatedMgmtNetworks_ReturnsNoIssues()
    {
        // Arrange - Management network has internet access, so no firewall holes needed
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>();

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_IsolatedMgmtWithNoRules_ReturnsBothIssues()
    {
        // Arrange - Isolated management network with no internet and no firewall rules
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>();

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().HaveCount(2);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasUniFiAccessRule_ReturnsOnlyAfcIssue()
    {
        // Arrange
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow UniFi Access", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasAfcAccessRule_ReturnsOnlyUniFiIssue()
    {
        // Arrange
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow AFC Traffic", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be("MGMT_MISSING_UNIFI_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasBothRules_ReturnsNoIssues()
    {
        // Arrange
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow UniFi Access", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("Allow AFC Traffic", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_RuleByNamePattern_MatchesUniFi()
    {
        // Arrange - Rule detected by name containing "UniFi" even without specific network ID match
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Mgmt Network to Access UniFi", action: "allow"),
            CreateFirewallRule("Allow Wi-Fi AFC Traffic", action: "allow")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_DisabledRule_NotCounted()
    {
        // Arrange - Disabled rules should not count
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow UniFi Access", action: "allow", enabled: false,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("Allow AFC Traffic", action: "allow", enabled: false,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().HaveCount(2);
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_BlockRule_NotCounted()
    {
        // Arrange - Block rules should not satisfy the requirement
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block UniFi Access", action: "block",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().HaveCount(2);
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_NonManagementNetwork_Ignored()
    {
        // Arrange - IoT networks should not be checked for management access rules
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>();

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_SeverityAndScoreImpact_Correct()
    {
        // Arrange
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>();

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        foreach (var issue in issues)
        {
            issue.Severity.Should().Be(AuditSeverity.Recommended);
            issue.ScoreImpact.Should().Be(5);
        }
    }

    #endregion

    #region Helper Methods

    private static NetworkInfo CreateNetwork(
        string name,
        NetworkPurpose purpose,
        string? id = null,
        int vlanId = 99,
        bool networkIsolationEnabled = false,
        bool internetAccessEnabled = true)
    {
        return new NetworkInfo
        {
            Id = id ?? Guid.NewGuid().ToString(),
            Name = name,
            VlanId = vlanId,
            Purpose = purpose,
            Subnet = $"192.168.{vlanId}.0/24",
            Gateway = $"192.168.{vlanId}.1",
            DhcpEnabled = false,
            NetworkIsolationEnabled = networkIsolationEnabled,
            InternetAccessEnabled = internetAccessEnabled
        };
    }

    private static FirewallRule CreateFirewallRule(
        string name,
        string action = "allow",
        bool enabled = true,
        List<string>? sourceNetworkIds = null,
        List<string>? webDomains = null)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Enabled = enabled,
            Index = 1,
            SourceNetworkIds = sourceNetworkIds,
            WebDomains = webDomains
        };
    }

    #endregion
}
