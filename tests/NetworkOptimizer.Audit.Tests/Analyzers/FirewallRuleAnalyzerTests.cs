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
    private readonly Mock<ILogger<FirewallRuleParser>> _parserLoggerMock;

    public FirewallRuleAnalyzerTests()
    {
        _loggerMock = new Mock<ILogger<FirewallRuleAnalyzer>>();
        _parserLoggerMock = new Mock<ILogger<FirewallRuleParser>>();
        var parser = new FirewallRuleParser(_parserLoggerMock.Object);
        _analyzer = new FirewallRuleAnalyzer(_loggerMock.Object, parser);
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
    public void AnalyzeManagementNetworkFirewallAccess_IsolatedMgmtWithNoRules_ReturnsAllIssues()
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
        issues.Should().HaveCount(3);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasUniFiAccessRule_ReturnsMissingAfcAndNtp()
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
        issues.Should().HaveCount(2);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasAfcAccessRule_ReturnsMissingUniFiAndNtp()
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
        issues.Should().HaveCount(2);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_HasAllThreeRules_ReturnsNoIssues()
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
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("Allow NTP", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ntp.org" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_NtpByPort123_ReturnsNoNtpIssue()
    {
        // Arrange - NTP via port 123 instead of domain
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
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("Allow NTP Port", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_SeparateRules_MatchesAll()
    {
        // Arrange - Separate rules for UniFi, AFC, and NTP
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("My Custom Rule Name", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("Another Rule", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("NTP Rule", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_CombinedRule_MatchesBothUniFiAndAfc()
    {
        // Arrange - Single rule combining UniFi, AFC, and NTP domains (common pattern)
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Wi-Fi AFC Traffic", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com", "location.qcs.qualcomm.com", "api.qcs.qualcomm.com", "ui.com", "ntp.org" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - Single rule satisfies both UniFi and AFC checks
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
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("Allow NTP", action: "allow", enabled: false,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - All 3 disabled rules don't count
        issues.Should().HaveCount(3);
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

        // Assert - Block rule doesn't satisfy, so all 3 issues present
        issues.Should().HaveCount(3);
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
    public void AnalyzeManagementNetworkFirewallAccess_No5GDevice_No5GIssue()
    {
        // Arrange - Without a 5G device, no 5G rule check should happen
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>();

        // Act - has5GDevice = false (default)
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: false);

        // Assert - Should have UniFi, AFC, and NTP issues, but not 5G
        issues.Should().HaveCount(3);
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Has5GDevice_Returns5GIssue()
    {
        // Arrange - With a 5G device present, should check for 5G rule
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>();

        // Act - has5GDevice = true
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - Should have UniFi, AFC, NTP, and 5G issues
        issues.Should().HaveCount(4);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Has5GRuleByConfig_No5GIssue()
    {
        // Arrange - 5G rule detected by config (source network + carrier domains)
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("UniFi Cloud", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("AFC Traffic", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("NTP", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123"),
            CreateFirewallRule("Modem Registration", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "trafficmanager.net", "t-mobile.com", "gsma.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - All rules satisfied
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Has5GRuleWithPartialDomains_No5GIssue()
    {
        // Arrange - 5G rule with just one of the carrier domains still satisfies the check
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("UniFi", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("AFC", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "qcs.qualcomm.com" }),
            CreateFirewallRule("NTP", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ntp.org" }),
            CreateFirewallRule("TMobile Only", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "t-mobile.com" })
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - All rules satisfied (t-mobile.com alone is enough for 5G check)
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

        // Assert - These are informational issues with no score impact (too strict for most users)
        foreach (var issue in issues)
        {
            issue.Severity.Should().Be(AuditSeverity.Informational);
            issue.ScoreImpact.Should().Be(0);
        }
    }

    #endregion

    #region DetectShadowedRules Tests

    [Fact]
    public void DetectShadowedRules_EmptyRules_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>();

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_SingleRule_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block All", action: "drop", index: 1)
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_AllowBeforeDeny_ReturnsSubvertIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All", action: "allow", index: 1, sourceType: "any", destType: "any"),
            CreateFirewallRule("Block IoT", action: "drop", index: 2, sourceType: "any", destType: "any")
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("ALLOW_SUBVERTS_DENY");
    }

    [Fact]
    public void DetectShadowedRules_DenyBeforeAllow_ReturnsShadowedIssue()
    {
        // Both rules must have same protocol scope for shadow detection
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block All", action: "drop", index: 1, sourceType: "any", destType: "any", protocol: "all"),
            CreateFirewallRule("Allow Specific", action: "allow", index: 2, sourceType: "any", destType: "any", protocol: "all")
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("DENY_SHADOWS_ALLOW");
    }

    [Fact]
    public void DetectShadowedRules_SameAction_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow A", action: "allow", index: 1),
            CreateFirewallRule("Allow B", action: "allow", index: 2)
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_DisabledRules_Ignored()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All", action: "allow", index: 1, enabled: false, sourceType: "any", destType: "any"),
            CreateFirewallRule("Block IoT", action: "drop", index: 2, sourceType: "any", destType: "any")
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_PredefinedRules_Ignored()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All", action: "allow", index: 1, predefined: true, sourceType: "any", destType: "any"),
            CreateFirewallRule("Block IoT", action: "drop", index: 2, sourceType: "any", destType: "any")
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_NarrowAllowBeforeBroadDeny_ReturnsExceptionPattern()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow DNS", action: "allow", index: 1, destPort: "53", sourceType: "any", destType: "any"),
            CreateFirewallRule("Block All", action: "drop", index: 2, sourceType: "any", destType: "any")
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Narrow exception before broad deny should be info-level exception pattern
        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Fact]
    public void DetectShadowedRules_BroadBlockToNetworkBeforeNarrowAllowToIp_ReturnsShadowedIssue()
    {
        // This tests the scenario where a broad BLOCK rule to NETWORKs eclipses
        // a narrow ALLOW rule to specific IPs. The allow rule may never match
        // because the block rule (which comes first) blocks all traffic to those networks,
        // including traffic to IPs within those networks.
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-rule",
                Name = "[CRITICAL] Block Access to Isolated VLANs",
                Action = "block",
                Enabled = true,
                Index = 10016,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "net-1", "net-2", "net-3", "net-4" }
            },
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow Device Screen Streaming",
                Action = "allow",
                Enabled = true,
                Index = 10017,
                Protocol = "all",
                SourceMatchingTarget = "CLIENT",
                SourceClientMacs = new List<string> { "aa:bb:cc:dd:ee:ff", "11:22:33:44:55:66" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.64.210-192.168.64.219" }
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should detect that the allow rule is shadowed by the earlier block rule
        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("DENY_SHADOWS_ALLOW");
        issue.Severity.Should().Be(AuditSeverity.Informational);
        issue.Message.Should().Contain("Allow Device Screen Streaming");
        issue.Message.Should().Contain("Block Access to Isolated VLANs");
    }

    #endregion

    #region DetectPermissiveRules Tests

    [Fact]
    public void DetectPermissiveRules_EmptyRules_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>();

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnyAnyAnyAccept_ReturnsCriticalIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All", action: "accept", sourceType: "any", destType: "any", protocol: "all")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("PERMISSIVE_RULE");
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(15);
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceAccept_ReturnsBroadRuleIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow From Any", action: "accept", sourceType: "any", destType: "network", dest: "corp-net")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("BROAD_RULE");
        issue.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void DetectPermissiveRules_AnyDestAccept_ReturnsBroadRuleIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow To Any", action: "accept", sourceType: "network", source: "corp-net", destType: "any")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("BROAD_RULE");
    }

    [Fact]
    public void DetectPermissiveRules_DisabledRule_Ignored()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All", action: "accept", enabled: false, sourceType: "any", destType: "any", protocol: "all")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_PredefinedRule_Ignored()
    {
        // Predefined rules (UniFi built-in like "Allow All Traffic", "Allow Return Traffic")
        // should be skipped since users can't change them
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow All Traffic", action: "accept", predefined: true, sourceType: "any", destType: "any", protocol: "all"),
            CreateFirewallRule("Allow Return Traffic", action: "accept", predefined: true, sourceType: "any", destType: "any", protocol: "all")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_DenyRule_NoIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Deny All", action: "drop", sourceType: "any", destType: "any", protocol: "all")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_SpecificSourceAndDest_NoIssue()
    {
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Specific", action: "accept", sourceType: "network", source: "corp-net", destType: "network", dest: "iot-net")
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    #region v2 API Format Tests

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_SpecificSourceIps_NotFlaggedAtAll()
    {
        // v2 API rule with specific source IPs should NOT be flagged at all
        // Having specific source IPs makes "any destination" acceptable
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-1",
                Name = "Allow Phone Access to IoT (Return)",
                Action = "ALLOW", // v2 API uses uppercase
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "IP", // v2 API format
                SourceIps = new List<string> { "192.168.64.0/24", "192.168.200.0/24" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Not flagged because specific source IPs make the rule restrictive
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_AnyDestWithSpecificPorts_NotFlaggedAsBroad()
    {
        // Rule with ANY destination but specific ports should NOT be flagged as broad
        // This matches the "Allow Select Access to Custom UniFi APIs" scenario
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-ports",
                Name = "Allow Select Access to Custom UniFi APIs",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.1.220", "192.168.1.10" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "8088-8089"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Not flagged because it has specific source IPs AND specific destination ports
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceWithSpecificPorts_NotFlaggedAsBroad()
    {
        // Rule with ANY source but specific destination ports should NOT be flagged as broad
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-any-src-specific-port",
                Name = "Allow SSH from Any",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.1.1" },
                DestinationPort = "22"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Not flagged because specific port makes "any source" acceptable for this use case
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_AnyAny_FlaggedAsPermissive()
    {
        // v2 API rule that IS truly any->any should be flagged
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-2",
                Name = "Allow All Traffic",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("PERMISSIVE_RULE");
        issues.First().Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_SpecificDestIps_NotFlaggedAsAnyAny()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-3",
                Name = "Allow Access to Specific IPs",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.1.100" }
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Should be BROAD_RULE (any source) not PERMISSIVE_RULE (any->any)
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("BROAD_RULE");
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_NetworkTarget_NotFlaggedAsAnyAny()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-4",
                Name = "Allow Access from Network",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "network-123" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Source is specific network, not "any"
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("BROAD_RULE"); // any destination
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_ClientMacs_NotFlaggedAsAnyAny()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-5",
                Name = "Allow from Specific Clients",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "CLIENT",
                SourceClientMacs = new List<string> { "aa:bb:cc:dd:ee:ff" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Source is specific client MACs, not "any"
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("BROAD_RULE"); // any destination
    }

    [Fact]
    public void DetectPermissiveRules_V2ApiFormat_SpecificProtocol_NotFlaggedAsAnyAny()
    {
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-rule-6",
                Name = "Allow TCP Only",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp", // specific protocol
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        // Protocol is specific, so not PERMISSIVE_RULE (any->any->any)
        // But still flagged as single BROAD_RULE (any source OR any dest triggers it)
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("BROAD_RULE");
    }

    [Fact]
    public void CheckInterVlanIsolation_V2ApiFormat_HasBlockRule_NoIssue()
    {
        // Test that v2 API format block rules are properly detected
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-block-rule",
                Name = "Block IoT to Corp",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_V2ApiFormat_ReverseDirection_NoIssue()
    {
        // Test that v2 API format block rules in reverse direction are properly detected
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "v2-block-rule",
                Name = "Block Corp to IoT",
                Action = "BLOCK",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "iot-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBetweenIsolatedNetworks_FlaggedAsBroadRule()
    {
        // Test that ALLOW rules between networks that should be isolated are flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "security-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-iot-to-security",
                Name = "[TEST] Any <-> Any",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "security-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag the ALLOW rule as problematic
        issues.Should().Contain(i => i.Type == "BROAD_RULE" && i.Message.Contains("[TEST] Any <-> Any"));
        var allowIssue = issues.First(i => i.Type == "BROAD_RULE");
        allowIssue.Message.Should().Contain("IoT").And.Contain("Security");
        allowIssue.RuleId.Should().Be("FW-ALLOW-ISOLATED");
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBetweenGuestAndCorporate_FlaggedAsBroadRule()
    {
        // Test Guest to Corporate allow rule is flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id"),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-guest-to-corp",
                Name = "Allow Guest to Corporate",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "guest-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "BROAD_RULE" && i.RuleId == "FW-ALLOW-ISOLATED");
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBetweenCorporateNetworks_NotFlagged()
    {
        // Test that ALLOW rules between two Corporate networks are NOT flagged
        // (Corporate to Corporate is fine)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate Main", NetworkPurpose.Corporate, id: "corp-main-id"),
            CreateNetwork("Corporate Branch", NetworkPurpose.Corporate, id: "corp-branch-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-corp-to-corp",
                Name = "Allow Corp to Corp",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-main-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-branch-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag allow rules between two corporate networks
        issues.Should().NotContain(i => i.RuleId == "FW-ALLOW-ISOLATED");
    }

    #endregion

    #endregion

    #region DetectOrphanedRules Tests

    [Fact]
    public void DetectOrphanedRules_EmptyRules_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo> { CreateNetwork("Corporate", NetworkPurpose.Corporate) };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectOrphanedRules_ValidNetworkReference_NoIssue()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-123");
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Corp", sourceType: "network", source: "corp-net-123")
        };
        var networks = new List<NetworkInfo> { network };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectOrphanedRules_InvalidSourceNetwork_ReturnsOrphanedIssue()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-123");
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Deleted", sourceType: "network", source: "deleted-net-456")
        };
        var networks = new List<NetworkInfo> { network };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("ORPHANED_RULE");
        issue.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Fact]
    public void DetectOrphanedRules_InvalidDestNetwork_ReturnsOrphanedIssue()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-123");
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow To Deleted", destType: "network", dest: "deleted-net-456")
        };
        var networks = new List<NetworkInfo> { network };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("ORPHANED_RULE");
    }

    [Fact]
    public void DetectOrphanedRules_DisabledRule_Ignored()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-123");
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Deleted", enabled: false, sourceType: "network", source: "deleted-net-456")
        };
        var networks = new List<NetworkInfo> { network };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectOrphanedRules_AnySourceType_NotOrphaned()
    {
        var network = CreateNetwork("Corporate", NetworkPurpose.Corporate);
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Any", sourceType: "any")
        };
        var networks = new List<NetworkInfo> { network };

        var issues = _analyzer.DetectOrphanedRules(rules, networks);

        issues.Should().BeEmpty();
    }

    #endregion

    #region CheckInterVlanIsolation Tests

    [Fact]
    public void CheckInterVlanIsolation_EmptyNetworks_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_IsolatedNetworkEnabled_NoIssue()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, networkIsolationEnabled: true),
            CreateNetwork("Corporate", NetworkPurpose.Corporate)
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // IoT has isolation enabled via system, so no need for manual firewall rule
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_NonIsolatedIoT_MissingRule_ReturnsIssue()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net")
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("MISSING_ISOLATION");
        issue.Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void CheckInterVlanIsolation_NonIsolatedIoT_HasDropRule_NoIssue()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net")
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block IoT to Corp", action: "drop", source: "iot-net", dest: "corp-net")
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_NonIsolatedGuest_MissingRule_ReturnsIssue()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest", NetworkPurpose.Guest, id: "guest-net", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net")
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("MISSING_ISOLATION");
    }

    [Fact]
    public void CheckInterVlanIsolation_DisabledDropRule_StillMissing()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net")
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block IoT to Corp", action: "drop", enabled: false, source: "iot-net", dest: "corp-net")
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().ContainSingle();
    }

    #endregion

    #region AnalyzeFirewallRules Tests

    [Fact]
    public void AnalyzeFirewallRules_EmptyInput_ReturnsNoIssues()
    {
        var rules = new List<FirewallRule>();
        var networks = new List<NetworkInfo>();

        var issues = _analyzer.AnalyzeFirewallRules(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeFirewallRules_CombinesAllChecks()
    {
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net"),
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // This should trigger PERMISSIVE_RULE
            CreateFirewallRule("Allow All", action: "accept", sourceType: "any", destType: "any", protocol: "all"),
            // This should trigger ORPHANED_RULE
            CreateFirewallRule("Allow Deleted", sourceType: "network", source: "deleted-net")
        };

        var issues = _analyzer.AnalyzeFirewallRules(rules, networks);

        // Should have PERMISSIVE_RULE, ORPHANED_RULE, and MISSING_ISOLATION
        issues.Should().Contain(i => i.Type == "PERMISSIVE_RULE");
        issues.Should().Contain(i => i.Type == "ORPHANED_RULE");
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION");
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
        List<string>? webDomains = null,
        string? destinationPort = null,
        int index = 1,
        string? sourceType = null,
        string? destType = null,
        string? source = null,
        string? dest = null,
        string? protocol = null,
        string? destPort = null,
        bool predefined = false)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Enabled = enabled,
            Index = index,
            SourceNetworkIds = sourceNetworkIds,
            WebDomains = webDomains,
            DestinationPort = destinationPort ?? destPort,
            SourceType = sourceType,
            DestinationType = destType,
            Source = source,
            Destination = dest,
            Protocol = protocol,
            Predefined = predefined
        };
    }

    #endregion
}
