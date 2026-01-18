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

    [Fact]
    public void DetectShadowedRules_ExceptionToExternalBlock_SetsExternalAccessDescription()
    {
        // Allow rule before deny rule that blocks external/WAN access
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow NAS DoH",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.10.50" },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "external-zone-1"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "[Block] Management Internet Access",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                SourceZoneId = "lan-zone-1",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "external-zone-1"
            }
        };

        // Pass the external zone ID so it can identify external access patterns
        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: "external-zone-1");

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        issue!.Description.Should().Be("External Access Exception");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToGatewayBlock_SetsFirewallExceptionDescription()
    {
        // Allow rule before deny rule that blocks Gateway zone access (NOT external)
        // Gateway zone blocks should NOT be categorized as "External Access Exception"
        // Using IP/ANY sources to avoid triggering "Cross-VLAN" categorization
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow SSH to Gateway",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.10.50" },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "gateway-zone-1",
                DestinationPort = "22"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "[Block] All Gateway Access",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "lan-zone-1",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "gateway-zone-1"
            }
        };

        // Pass the external zone ID - Gateway zone is different
        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: "external-zone-1");

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Gateway zone blocks should fall back to generic "Firewall Exception" (not "External Access Exception")
        issue!.Description.Should().Be("Firewall Exception");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToNetworkBlock_SetsCrossVlanDescription()
    {
        // Allow rule before deny rule that blocks network-to-network traffic
        // Both rules use network source for proper overlap detection
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow Printer Access",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-network-1" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.20.100" },
                DestinationPort = "631"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block IoT to Home",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-network-1" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "home-network-1" }
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Without networks info, no purpose suffix
        issue!.Description.Should().Be("Cross-VLAN Access Exception");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToIoTNetworkBlock_IncludesPurposeInDescription()
    {
        // Allow rule before deny rule that blocks traffic to IoT network
        var iotNetworkId = "iot-network-1";
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow Printer Access",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "home-network-1" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.20.100" },
                DestinationPort = "631"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Home to IoT",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "home-network-1" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { iotNetworkId }
            }
        };

        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId),
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-network-1")
        };

        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: null, networks: networks);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Should include IoT purpose suffix
        issue!.Description.Should().Be("Cross-VLAN Access Exception (IoT)");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToSecurityNetworkBlock_IncludesPurposeInDescription()
    {
        // Allow rule before deny rule that blocks traffic to Security network
        var securityNetworkId = "security-network-1";
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow Camera View",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "CLIENT",
                SourceClientMacs = new List<string> { "aa:bb:cc:dd:ee:ff" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.30.0/24" },
                DestinationPort = "443"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block All to Security",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { securityNetworkId }
            }
        };

        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: securityNetworkId)
        };

        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: null, networks: networks);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Should include Security purpose suffix
        issue!.Description.Should().Be("Cross-VLAN Access Exception (Security)");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionWithDestinationIp_LooksUpNetworkPurpose()
    {
        // Allow rule using destination IPs (not network IDs) - should still determine purpose from IP subnet
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow NAS HA - Camera",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.1.100" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.168.30.50" }, // IP in Security network
                DestinationPort = "443"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "[CRITICAL] Block Access to Isolated VLANs",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "iot-net", "security-net", "mgmt-net" }
            }
        };

        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net", vlanId: 20),
            new NetworkInfo
            {
                Id = "security-net",
                Name = "Security Cameras",
                Purpose = NetworkPurpose.Security,
                VlanId = 30,
                Subnet = "192.168.30.0/24",
                Gateway = "192.168.30.1"
            },
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", vlanId: 99)
        };

        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: null, networks: networks);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Should determine Security purpose from destination IP falling within Security network subnet
        issue!.Description.Should().Be("Cross-VLAN Access Exception (Security)");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToGenericBlock_SetsFirewallExceptionDescription()
    {
        // Allow rule before deny rule with non-network, non-external pattern
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow HTTP",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.1.100" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "10.0.0.0/8" },
                DestinationPort = "80"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block All IP Range",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.1.0/24" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "10.0.0.0/8" }
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        issue!.Description.Should().Be("Firewall Exception");
    }

    [Fact]
    public void DetectShadowedRules_UniFiDomainException_IsFiltered()
    {
        // UniFi domain exception should be filtered out (covered by MGMT_MISSING_UNIFI_ACCESS)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow UniFi Cloud",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY",
                WebDomains = new List<string> { "*.ui.com" }
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT create an exception pattern issue for UniFi domain rules
        issues.Should().NotContain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_AfcDomainException_IsFiltered()
    {
        // AFC domain exception should be filtered out (covered by MGMT_MISSING_AFC_ACCESS)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow AFC Traffic",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY",
                WebDomains = new List<string> { "afcapi.qcs.qualcomm.com" }
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT create an exception pattern issue for AFC domain rules
        issues.Should().NotContain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_NtpDomainException_IsFiltered()
    {
        // NTP domain exception should be filtered out (covered by MGMT_MISSING_NTP_ACCESS)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow NTP",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY",
                WebDomains = new List<string> { "pool.ntp.org" }
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT create an exception pattern issue for NTP domain rules
        issues.Should().NotContain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_NtpPortException_IsFiltered()
    {
        // NTP port 123 exception should be filtered out (covered by MGMT_MISSING_NTP_ACCESS)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow NTP Port",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "123",
                Protocol = "udp"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT create an exception pattern issue for NTP port rules
        issues.Should().NotContain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_5gDomainException_IsFiltered()
    {
        // 5G modem domain exception should be filtered out (covered by MGMT_MISSING_5G_ACCESS)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow 5G Registration",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY",
                WebDomains = new List<string> { "*.trafficmanager.net", "*.t-mobile.com" }
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT create an exception pattern issue for 5G domain rules
        issues.Should().NotContain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_NonMgmtServiceException_IsNotFiltered()
    {
        // Non-management service exceptions should still be reported
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule",
                Name = "Allow Custom Service",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.10.50" },
                DestinationMatchingTarget = "ANY",
                WebDomains = new List<string> { "custom-service.example.com" }
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Management Internet",
                Action = "drop",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should create an exception pattern issue for non-management service domains
        issues.Should().Contain(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
    }

    [Fact]
    public void DetectShadowedRules_FindsAllExceptionPatterns()
    {
        // Multiple exceptions to the same deny rule should all be found
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-rule-1",
                Name = "Allow Service A",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.10.50" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "443"
            },
            new FirewallRule
            {
                Id = "allow-rule-2",
                Name = "Allow Service B",
                Action = "allow",
                Enabled = true,
                Index = 2,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.10.51" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "8080"
            },
            new FirewallRule
            {
                Id = "deny-rule",
                Name = "Block Network Internet",
                Action = "drop",
                Enabled = true,
                Index = 3,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "network-1" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should find both exception patterns
        var exceptionIssues = issues.Where(i => i.Type == "ALLOW_EXCEPTION_PATTERN").ToList();
        exceptionIssues.Should().HaveCount(2);
        exceptionIssues.Should().Contain(i => i.Message.Contains("Allow Service A"));
        exceptionIssues.Should().Contain(i => i.Message.Contains("Allow Service B"));
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

        // Should flag the ALLOW rule as critical - actively bypassing isolation
        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.Message.Contains("[TEST] Any <-> Any"));
        var allowIssue = issues.First(i => i.Type == "ISOLATION_BYPASSED");
        allowIssue.Message.Should().Contain("IoT").And.Contain("Security");
        allowIssue.Severity.Should().Be(AuditSeverity.Critical);
        allowIssue.RuleId.Should().Be("FW-ISOLATION-BYPASS");
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBetweenGuestAndCorporate_FlaggedAsCritical()
    {
        // Test Guest to Corporate allow rule is flagged as critical
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

        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.RuleId == "FW-ISOLATION-BYPASS");
        issues.First(i => i.Type == "ISOLATION_BYPASSED").Severity.Should().Be(AuditSeverity.Critical);
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
        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-BYPASS");
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateToManagement_NoBlockRule_FlaggedAsCritical()
    {
        // Corporate to Management without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical);
        issues.First(i => i.Type == "MISSING_ISOLATION").Message.Should().Contain("Corporate").And.Contain("Management");
    }

    [Fact]
    public void CheckInterVlanIsolation_HomeToManagement_NoBlockRule_FlaggedAsCritical()
    {
        // Home to Management without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void CheckInterVlanIsolation_SecurityToManagement_NoBlockRule_FlaggedAsCritical()
    {
        // Security to Management without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", networkIsolationEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToCorporate_NoBlockRule_FlaggedAsCritical()
    {
        // Guest to Corporate without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToCorporate_NoBlockRule_FlaggedAsRecommended()
    {
        // IoT to Corporate without block rule should be Recommended (not Critical)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Recommended);
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleCorporateToManagement_FlaggedAsCritical()
    {
        // ALLOW rule from Corporate to Management should be flagged as Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-corp-to-mgmt",
                Name = "Allow Corporate to Management",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void CheckInterVlanIsolation_ManagementWithSystemIsolation_NotChecked()
    {
        // Management network WITH system isolation enabled should NOT be checked
        // (UniFi's "Isolated Networks" feature handles it)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: true) // System isolation ON
        };
        var rules = new List<FirewallRule>(); // No manual rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag missing isolation because system isolation handles it
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Management"));
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

    #region Source Network Match Opposite Tests

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_MatchOppositeNetworks_ExcludesSpecifiedNetwork()
    {
        // Arrange - Rule applies to all networks EXCEPT the one specified
        var mgmtNetworkId = "mgmt-network-123";
        var otherNetworkId = "other-network-456";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false),
            CreateNetwork("Other", NetworkPurpose.Corporate, id: otherNetworkId)
        };

        // Rule with Match Opposite: applies to all networks EXCEPT "other-network-456"
        // This means it SHOULD apply to mgmtNetworkId
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-1",
                Name = "Allow UniFi Access (Match Opposite)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { otherNetworkId }, // Excludes other, so applies to mgmt
                SourceMatchOppositeNetworks = true,
                WebDomains = new List<string> { "ui.com" }
            },
            new FirewallRule
            {
                Id = "rule-2",
                Name = "Allow AFC (Match Opposite)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { otherNetworkId },
                SourceMatchOppositeNetworks = true,
                WebDomains = new List<string> { "qcs.qualcomm.com" }
            },
            new FirewallRule
            {
                Id = "rule-3",
                Name = "Allow NTP (Match Opposite)",
                Action = "allow",
                Enabled = true,
                Protocol = "udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { otherNetworkId },
                SourceMatchOppositeNetworks = true,
                DestinationPort = "123"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - All rules should match management network via Match Opposite
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_MatchOppositeNetworks_ExcludesMgmtNetwork_NoMatch()
    {
        // Arrange - Rule applies to all networks EXCEPT the management network
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };

        // Rule with Match Opposite: excludes mgmt network, so it does NOT apply to mgmt
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-1",
                Name = "Allow UniFi Access (Excludes Mgmt)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId }, // Excludes mgmt, so does NOT apply to mgmt
                SourceMatchOppositeNetworks = true,
                WebDomains = new List<string> { "ui.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - Rule excludes mgmt network, so all 3 issues should be present
        issues.Should().HaveCount(3);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_NormalNetworkMatch_OnlyAppliesToSpecified()
    {
        // Arrange - Rule applies ONLY to specified networks (normal mode, not match opposite)
        var mgmtNetworkId = "mgmt-network-123";
        var otherNetworkId = "other-network-456";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false),
            CreateNetwork("Other", NetworkPurpose.Corporate, id: otherNetworkId)
        };

        // Rule with normal matching: applies ONLY to "other-network-456", NOT to mgmt
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-1",
                Name = "Allow UniFi Access (Other Only)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { otherNetworkId }, // Only applies to other, not mgmt
                SourceMatchOppositeNetworks = false, // Normal mode
                WebDomains = new List<string> { "ui.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - Rule doesn't apply to mgmt, so all 3 issues should be present
        issues.Should().HaveCount(3);
    }

    [Fact]
    public void CheckInterVlanIsolation_MatchOppositeSource_BlocksAllExceptSpecified()
    {
        // Arrange - Block rule with Match Opposite source
        var iotNetworkId = "iot-net-id";
        var corpNetworkId = "corp-net-id";
        var guestNetworkId = "guest-net-id";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId, networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: corpNetworkId),
            CreateNetwork("Guest", NetworkPurpose.Guest, id: guestNetworkId, networkIsolationEnabled: false)
        };

        // Block rule: from all networks EXCEPT guest, to corporate
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-to-corp",
                Name = "Block to Corp (except Guest)",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { guestNetworkId }, // Excludes guest
                SourceMatchOppositeNetworks = true,
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { corpNetworkId }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // IoT to Corporate should be covered (Match Opposite excludes Guest, so includes IoT)
        // Guest to Corporate should NOT be covered (Guest is excluded from the rule)
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Guest"));
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_MatchOppositeDestination_BlocksToAllExceptSpecified()
    {
        // Arrange - Block rule with Match Opposite destination
        var iotNetworkId = "iot-net-id";
        var corpNetworkId = "corp-net-id";
        var mgmtNetworkId = "mgmt-net-id";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId, networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: corpNetworkId),
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: false)
        };

        // Block rule: from IoT to all networks EXCEPT corporate
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-from-iot",
                Name = "Block from IoT (except to Corp)",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { iotNetworkId },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { corpNetworkId }, // Excludes corp
                DestinationMatchOppositeNetworks = true
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // IoT to Management should be covered (Match Opposite excludes Corp, so includes Mgmt)
        // IoT to Corporate should NOT be covered (Corp is excluded from the block rule)
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BothMatchOpposite_ComplexScenario()
    {
        // Arrange - Block rule with both source and destination Match Opposite
        var iotNetworkId = "iot-net-id";
        var corpNetworkId = "corp-net-id";
        var guestNetworkId = "guest-net-id";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId, networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: corpNetworkId),
            CreateNetwork("Guest", NetworkPurpose.Guest, id: guestNetworkId, networkIsolationEnabled: false)
        };

        // Block rule: from all EXCEPT IoT, to all EXCEPT Guest
        // This covers: Corp->Corp, Corp->IoT, Guest->Corp, Guest->IoT (one direction for each pair)
        // CheckInterVlanIsolation checks BOTH directions, so any pair with one direction blocked is covered
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "complex-block",
                Name = "Complex Block Rule",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { iotNetworkId }, // Excludes IoT
                SourceMatchOppositeNetworks = true,
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { guestNetworkId }, // Excludes Guest
                DestinationMatchOppositeNetworks = true
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // All pairs have at least one direction blocked:
        // - IoT<->Corporate: Corp->IoT is blocked
        // - Guest<->Corporate: Guest->Corp is blocked
        // - Guest<->IoT: Guest->IoT is blocked
        // So no MISSING_ISOLATION issues
        issues.Where(i => i.Type == "MISSING_ISOLATION").Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_MatchOpposite_ExcludesBothDirections_FlagsMissing()
    {
        // Arrange - Rule that excludes IoT from BOTH source AND destination
        // This means no traffic involving IoT is blocked at all
        var iotNetworkId = "iot-net-id";
        var corpNetworkId = "corp-net-id";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId, networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: corpNetworkId)
        };

        // Rule excludes IoT from both source and destination
        // So neither IoT->Corp nor Corp->IoT is blocked
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-except-iot",
                Name = "Block (except IoT)",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { iotNetworkId }, // Excludes IoT from source
                SourceMatchOppositeNetworks = true,
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { iotNetworkId }, // Excludes IoT from destination
                DestinationMatchOppositeNetworks = true
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // IoT<->Corporate has NEITHER direction blocked (IoT excluded from both source and dest)
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_AnySourceMatchingTarget_AppliesToAllNetworks()
    {
        // Arrange - Rule with ANY source should apply to all networks
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };

        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-1",
                Name = "Allow UniFi Access (Any Source)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "ANY", // Matches all sources
                WebDomains = new List<string> { "ui.com" }
            },
            new FirewallRule
            {
                Id = "rule-2",
                Name = "Allow AFC (Any Source)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "ANY",
                WebDomains = new List<string> { "qcs.qualcomm.com" }
            },
            new FirewallRule
            {
                Id = "rule-3",
                Name = "Allow NTP (Any Source)",
                Action = "allow",
                Enabled = true,
                Protocol = "udp",
                SourceMatchingTarget = "ANY",
                DestinationPort = "123"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - ANY source should match management network
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_IpSourceMatchingTarget_DoesNotMatchByNetworkId()
    {
        // Arrange - Rule with IP source should NOT match by network ID
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };

        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-1",
                Name = "Allow UniFi Access (IP Source)",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "IP", // IP type, not NETWORK
                SourceIps = new List<string> { "192.168.1.0/24" },
                WebDomains = new List<string> { "ui.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - IP source type should not match by network ID
        issues.Should().HaveCount(3);
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
