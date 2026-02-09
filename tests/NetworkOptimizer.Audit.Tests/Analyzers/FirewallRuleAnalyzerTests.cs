using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
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
                destinationPort: "123",
                protocol: "udp")
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
        // Arrange - Single rule combining UniFi and AFC domains, plus separate NTP port rule
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow Wi-Fi AFC Traffic", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com", "location.qcs.qualcomm.com", "api.qcs.qualcomm.com", "ui.com" }),
            CreateFirewallRule("Allow NTP", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123",
                protocol: "udp")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks);

        // Assert - Combined rule satisfies UniFi and AFC, separate rule satisfies NTP
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
                destinationPort: "123",
                protocol: "udp"),
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
    public void AnalyzeManagementNetworkFirewallAccess_Has5GRuleByIp_No5GIssue()
    {
        // Arrange - 5G rule targets modem by specific IP address
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
                destinationPort: "123",
                protocol: "udp"),
            // 5G modem registration rule by specific IP (modem at 192.168.99.5)
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "5G Modem Registration",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.5" },
                WebDomains = new List<string> { "trafficmanager.net", "t-mobile.com", "gsma.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - 5G rule by IP satisfies the requirement
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Has5GRuleByMac_No5GIssue()
    {
        // Arrange - 5G rule targets modem by specific MAC address
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
                destinationPort: "123",
                protocol: "udp"),
            // 5G modem registration rule by specific MAC
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "5G Modem Registration",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "CLIENT",
                SourceClientMacs = new List<string> { "aa:bb:cc:dd:ee:ff" },
                WebDomains = new List<string> { "t-mobile.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - 5G rule by MAC satisfies the requirement
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_Has5GRuleByAnySource_No5GIssue()
    {
        // Arrange - 5G rule with ANY source (allows all devices including modem)
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
                destinationPort: "123",
                protocol: "udp"),
            // 5G modem registration rule with ANY source
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Allow Carrier Access",
                Action = "allow",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "ANY",
                WebDomains = new List<string> { "gsma.com" }
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true);

        // Assert - 5G rule with ANY source satisfies the requirement
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

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_InternetBlockedViaFirewallRule_ReturnsAllIssues()
    {
        // Arrange - Management network has internet enabled in config, but blocked via firewall rule
        // This should still trigger the Info checks because the network effectively has no internet
        var externalZoneId = "external-zone-123";
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            // Block Internet Access firewall rule
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Block Management Internet",
                Action = "block",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - Should detect that internet is blocked and fire all 3 Info checks
        issues.Should().HaveCount(3);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_InternetBlockedViaFirewallRule_WithAllowRules_ReturnsNoIssues()
    {
        // Arrange - Management network blocked via firewall rule, but has allow rules for required services
        // Real-world setup: allow rules have lower index (higher priority) than block rule
        var externalZoneId = "external-zone-123";
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            // Allow UniFi access - lower index, takes effect
            CreateFirewallRule("Allow UniFi Access", action: "allow", index: 100,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            // Allow AFC access - lower index, takes effect
            CreateFirewallRule("Allow AFC Access", action: "allow", index: 101,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "qcs.qualcomm.com" }),
            // Allow NTP access (UDP port 123) - lower index, takes effect
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Allow NTP Access",
                Action = "allow",
                Enabled = true,
                Index = 102,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "123",
                Protocol = "udp"
            },
            // Block Internet Access firewall rule - higher index, catch-all for everything else
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Block Management Internet",
                Action = "block",
                Enabled = true,
                Index = 200,  // Higher index = lower priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - No issues since all required allow rules are present
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_AllowRuleEclipsesBlockRule_InternetNotActuallyBlocked()
    {
        // Arrange - An allow rule with lower index eclipses the block rule,
        // so internet is NOT actually blocked (but current code might think it is)
        var externalZoneId = "external-zone-123";
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            // Allow All Internet - lower index, takes precedence
            new FirewallRule
            {
                Id = "allow-all-internet",
                Name = "Allow All Internet",
                Action = "allow",
                Enabled = true,
                Index = 100,  // Lower index = higher priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // Block Internet Access - higher index, eclipsed by allow rule
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block Management Internet",
                Action = "block",
                Enabled = true,
                Index = 200,  // Higher index = lower priority (eclipsed)
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - Internet is NOT blocked (allow rule takes effect), so NO issues should be raised
        // Network effectively has internet access, so we don't check for missing firewall rules
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_BlockRuleBeforeAllowRule_InternetBlocked()
    {
        // Arrange - Block rule with lower index blocks internet despite allow rule existing
        var externalZoneId = "external-zone-123";
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            // Block Internet Access - lower index, takes precedence
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block Management Internet",
                Action = "block",
                Enabled = true,
                Index = 100,  // Lower index = higher priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // Allow All Internet - higher index, eclipsed by block rule
            new FirewallRule
            {
                Id = "allow-all-internet",
                Name = "Allow All Internet",
                Action = "allow",
                Enabled = true,
                Index = 200,  // Higher index = lower priority (eclipsed)
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - Internet IS blocked (block rule takes effect), so issues SHOULD be raised
        issues.Should().HaveCount(3);
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_NTP_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_NtpViaPortGroup_SatisfiesRequirement()
    {
        // Arrange - NTP access via port group should satisfy the NTP requirement
        var externalZoneId = "external-zone-123";
        var mgmtNetworkId = "mgmt-network-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };

        // Set up port group with NTP port 123 (mixed with other ports)
        var portGroup = new NetworkOptimizer.UniFi.Models.UniFiFirewallGroup
        {
            Id = "common-ports-group",
            Name = "Common Ports",
            GroupType = "port-group",
            GroupMembers = new List<string> { "53", "123", "443" } // DNS, NTP, HTTPS
        };
        _analyzer.SetFirewallGroups(new[] { portGroup });

        // Parse a rule that references the port group for NTP
        var ntpRuleJson = System.Text.Json.JsonDocument.Parse(@"{
            ""_id"": ""allow-ntp-portgroup"",
            ""name"": ""Allow NTP via Port Group"",
            ""action"": ""ALLOW"",
            ""enabled"": true,
            ""protocol"": ""udp"",
            ""source"": {
                ""matching_target"": ""NETWORK"",
                ""network_ids"": [""mgmt-network-123""]
            },
            ""destination"": {
                ""matching_target"": ""ANY"",
                ""port_matching_type"": ""OBJECT"",
                ""port_group_id"": ""common-ports-group"",
                ""zone_id"": ""external-zone-123""
            }
        }").RootElement;

        var parsedRule = _analyzer.ParseFirewallPolicy(ntpRuleJson);
        parsedRule.Should().NotBeNull();
        parsedRule!.DestinationPort.Should().Be("53,123,443"); // Verify port group was resolved

        var rules = new List<FirewallRule>
        {
            // UniFi cloud access
            CreateFirewallRule("Allow UniFi Access", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            // AFC access
            CreateFirewallRule("Allow AFC Access", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "qcs.qualcomm.com" }),
            // NTP via port group
            parsedRule
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - All requirements satisfied (NTP via port group should be detected)
        issues.Should().BeEmpty();
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_UniFiAccessRuleEclipsedByBlockRule_ReportsMissing()
    {
        // Arrange - UniFi access allow rule is eclipsed by a block rule with lower index
        // The allow rule exists but doesn't actually take effect due to rule ordering
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule with lower index (higher priority) - eclipses the allow rule
            new FirewallRule
            {
                Id = "block-all-external",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 100,  // Lower index = higher priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // Allow ui.com - higher index, eclipsed by block rule above
            new FirewallRule
            {
                Id = "allow-unifi",
                Name = "Allow UniFi Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 200,  // Higher index = lower priority (eclipsed)
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "ui.com" },
                Protocol = "tcp"
            },
            // AFC and NTP rules (also eclipsed, but we're testing UniFi specifically)
            CreateFirewallRule("Allow AFC Access", action: "allow", index: 201,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "qcs.qualcomm.com" }),
            CreateFirewallRule("Allow NTP", action: "allow", index: 202,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - UniFi access rule exists but is eclipsed, so it should be reported as missing
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_UniFiAccessRuleNotEclipsed_NoIssue()
    {
        // Arrange - UniFi access allow rule has lower index than block rule
        // The allow rule takes effect, so access is granted
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Allow ui.com - lower index, takes effect
            new FirewallRule
            {
                Id = "allow-unifi",
                Name = "Allow UniFi Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 100,  // Lower index = higher priority (takes effect)
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "ui.com" },
                Protocol = "tcp"
            },
            // Block rule with higher index - eclipsed by allow rule above
            new FirewallRule
            {
                Id = "block-all-external",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 200,  // Higher index = lower priority (eclipsed)
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // AFC and NTP rules
            CreateFirewallRule("Allow AFC Access", action: "allow", index: 101,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "qcs.qualcomm.com" }),
            CreateFirewallRule("Allow NTP", action: "allow", index: 102,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - UniFi access rule takes effect, so no issue should be reported
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_AfcAccessRuleEclipsedByBlockRule_ReportsMissing()
    {
        // Arrange - AFC access allow rule is eclipsed by a block rule with lower index
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule with lower index (higher priority) - eclipses the allow rule
            new FirewallRule
            {
                Id = "block-all-external",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // UniFi access (also eclipsed)
            CreateFirewallRule("Allow UniFi Access", action: "allow", index: 200,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            // Allow qcs.qualcomm.com - higher index, eclipsed by block rule
            new FirewallRule
            {
                Id = "allow-afc",
                Name = "Allow AFC Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 201,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "qcs.qualcomm.com" },
                Protocol = "tcp"
            },
            CreateFirewallRule("Allow NTP", action: "allow", index: 202,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - AFC access rule exists but is eclipsed, so it should be reported as missing
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_AfcAccessRuleNotEclipsed_NoIssue()
    {
        // Arrange - AFC access allow rule has lower index than block rule
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Allow qcs.qualcomm.com - lower index, takes effect
            new FirewallRule
            {
                Id = "allow-afc",
                Name = "Allow AFC Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "qcs.qualcomm.com" },
                Protocol = "tcp"
            },
            // Block rule with higher index - eclipsed
            new FirewallRule
            {
                Id = "block-all-external",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 200,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            // UniFi and NTP rules
            CreateFirewallRule("Allow UniFi Access", action: "allow", index: 101,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("Allow NTP", action: "allow", index: 102,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp")
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - AFC access rule takes effect, so no issue should be reported
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_BlockRuleTargetsInternalZone_DoesNotEclipseExternalAccess()
    {
        // Arrange - Block rule has lower index but targets an internal zone, not external
        // It should NOT eclipse the UniFi/AFC access rules which target external destinations
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var internalZoneId = "internal-zone-456";  // Different from external
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule with lower index but targets INTERNAL zone (not external)
            // This should NOT eclipse the external-bound UniFi access rule
            new FirewallRule
            {
                Id = "block-internal",
                Name = "Block Access to Internal VLANs",
                Action = "DROP",
                Enabled = true,
                Index = 100,  // Lower index
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = internalZoneId,  // NOT the external zone
                Protocol = "all"
            },
            // UniFi access rule with higher index - should still be effective
            // because the block rule above doesn't target external traffic
            new FirewallRule
            {
                Id = "allow-unifi",
                Name = "Allow UniFi Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 200,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "ui.com" },
                Protocol = "tcp"
            },
            // AFC access rule
            new FirewallRule
            {
                Id = "allow-afc",
                Name = "Allow AFC Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 201,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "qcs.qualcomm.com" },
                Protocol = "tcp"
            },
            new FirewallRule
            {
                Id = "allow-ntp",
                Name = "Allow NTP",
                Action = "ALLOW",
                Enabled = true,
                Index = 202,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationZoneId = externalZoneId,
                DestinationPort = "123",
                Protocol = "udp"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - Block rule targets internal zone, so UniFi/AFC access should NOT be reported as missing
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_DnsBlockRule_DoesNotEclipseHttpsAccess()
    {
        // Arrange - DNS block rule (port 53) has lower index but shouldn't eclipse HTTPS-based rules
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // DNS block rule - blocks port 53 only
            new FirewallRule
            {
                Id = "block-dns",
                Name = "Block External DNS",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "53",
                Protocol = "udp_tcp"
            },
            // UniFi access rule - uses HTTPS (port 443), not blocked by DNS rule
            new FirewallRule
            {
                Id = "allow-unifi",
                Name = "Allow UniFi Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 200,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "ui.com" },
                Protocol = "tcp"
            },
            // AFC access rule
            new FirewallRule
            {
                Id = "allow-afc",
                Name = "Allow AFC Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 201,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                WebDomains = new List<string> { "qcs.qualcomm.com" },
                Protocol = "tcp"
            },
            new FirewallRule
            {
                Id = "allow-ntp",
                Name = "Allow NTP",
                Action = "ALLOW",
                Enabled = true,
                Index = 202,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetworkId },
                DestinationZoneId = externalZoneId,
                DestinationPort = "123",
                Protocol = "udp"
            }
        };

        // Act
        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, externalZoneId: externalZoneId);

        // Assert - DNS block (port 53) doesn't affect HTTPS (port 443), so no UniFi/AFC issues
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_UNIFI_ACCESS");
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_AFC_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_CidrBlockRuleEclipsesIpAllow_Reports5GIssue()
    {
        // Arrange - 5G allow rule targets a specific IP, block rule uses CIDR covering that IP
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule with CIDR source covering 192.168.99.0/24
            new FirewallRule
            {
                Id = "block-subnet",
                Name = "Block Subnet",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.0/24" },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            CreateFirewallRule("UniFi Cloud", action: "allow", index: 200,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("AFC Traffic", action: "allow", index: 201,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("NTP", action: "allow", index: 202,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp"),
            // 5G allow rule with specific IP within the blocked CIDR
            new FirewallRule
            {
                Id = "allow-5g",
                Name = "5G Modem Registration",
                Action = "ALLOW",
                Enabled = true,
                Index = 300,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.5" },
                WebDomains = new List<string> { "t-mobile.com" },
                Protocol = "tcp"
            }
        };

        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true, externalZoneId: externalZoneId);

        // 5G allow at 192.168.99.5 is eclipsed by block at 192.168.99.0/24
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_CidrBlockRuleDoesNotCoverAllow_No5GIssue()
    {
        // Arrange - Block rule CIDR doesn't cover the 5G allow rule's IP
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule targets 10.0.0.0/8 - doesn't cover 192.168.99.5
            new FirewallRule
            {
                Id = "block-other-subnet",
                Name = "Block Other Subnet",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "10.0.0.0/8" },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            CreateFirewallRule("UniFi Cloud", action: "allow", index: 200,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("AFC Traffic", action: "allow", index: 201,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("NTP", action: "allow", index: 202,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp"),
            // 5G allow rule at 192.168.99.5 - NOT in 10.0.0.0/8
            new FirewallRule
            {
                Id = "allow-5g",
                Name = "5G Modem Registration",
                Action = "ALLOW",
                Enabled = true,
                Index = 300,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.5" },
                WebDomains = new List<string> { "t-mobile.com" },
                Protocol = "tcp"
            }
        };

        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true, externalZoneId: externalZoneId);

        // 5G allow at 192.168.99.5 is NOT covered by block at 10.0.0.0/8
        issues.Should().NotContain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_OppositeIpsBlockRuleEclipsesAllow_Reports5GIssue()
    {
        // Arrange - Block rule uses SourceMatchOppositeIps: blocks all EXCEPT 192.168.50.0/24
        // 5G modem at 192.168.99.5 is NOT in the exception list, so it IS blocked
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            // Block everything EXCEPT 192.168.50.0/24
            new FirewallRule
            {
                Id = "block-except-lan",
                Name = "Block Except LAN",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.50.0/24" },
                SourceMatchOppositeIps = true,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            CreateFirewallRule("UniFi Cloud", action: "allow", index: 200,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "ui.com" }),
            CreateFirewallRule("AFC Traffic", action: "allow", index: 201,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                webDomains: new List<string> { "afcapi.qcs.qualcomm.com" }),
            CreateFirewallRule("NTP", action: "allow", index: 202,
                sourceNetworkIds: new List<string> { mgmtNetworkId },
                destinationPort: "123", protocol: "udp"),
            // 5G modem at 192.168.99.5 - NOT in the exception CIDR 192.168.50.0/24
            new FirewallRule
            {
                Id = "allow-5g",
                Name = "5G Modem Registration",
                Action = "ALLOW",
                Enabled = true,
                Index = 300,
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.5" },
                WebDomains = new List<string> { "t-mobile.com" },
                Protocol = "tcp"
            }
        };

        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true, externalZoneId: externalZoneId);

        // 192.168.99.5 is not in the exception list (192.168.50.0/24), so it's blocked
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
    }

    [Fact]
    public void AnalyzeManagementNetworkFirewallAccess_BareIpBlockRuleCoversMatchingBareIpAllow_Reports5GIssue()
    {
        // Arrange - Both block and allow rules use bare IPs (no CIDR /32 notation)
        // Block at 192.168.99.5 should eclipse allow at 192.168.99.5
        var mgmtNetworkId = "mgmt-network-123";
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: mgmtNetworkId, networkIsolationEnabled: true, internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-specific-ip",
                Name = "Block Specific IP",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "IP",
                SourceIps = ["192.168.99.5"],
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                Protocol = "all"
            },
            CreateFirewallRule("UniFi Cloud", action: "allow", index: 200,
                sourceNetworkIds: [mgmtNetworkId],
                webDomains: ["ui.com"]),
            CreateFirewallRule("AFC Traffic", action: "allow", index: 201,
                sourceNetworkIds: [mgmtNetworkId],
                webDomains: ["afcapi.qcs.qualcomm.com"]),
            CreateFirewallRule("NTP", action: "allow", index: 202,
                sourceNetworkIds: [mgmtNetworkId],
                destinationPort: "123", protocol: "udp"),
            new FirewallRule
            {
                Id = "allow-5g",
                Name = "5G Modem Registration",
                Action = "ALLOW",
                Enabled = true,
                Index = 300,
                SourceMatchingTarget = "IP",
                SourceIps = ["192.168.99.5"],
                WebDomains = ["t-mobile.com"],
                Protocol = "tcp"
            }
        };

        var issues = _analyzer.AnalyzeManagementNetworkFirewallAccess(rules, networks, has5GDevice: true, externalZoneId: externalZoneId);

        // Block at 192.168.99.5 covers allow at 192.168.99.5 (exact bare IP match)
        issues.Should().Contain(i => i.Type == "MGMT_MISSING_5G_ACCESS");
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
        issue.Severity.Should().Be(AuditSeverity.Recommended);
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
        issue!.Description.Should().Be("External Access");
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToGatewayBlock_SetsEmptyDescription()
    {
        // Allow rule before deny rule that blocks Gateway zone access (NOT external)
        // Gateway zone blocks should NOT be categorized as "External Access"
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
        // Gateway zone blocks should have empty description (not "External Access")
        issue!.Description.Should().BeEmpty();
    }

    [Fact]
    public void DetectShadowedRules_ExceptionToNetworkBlock_SetsEmptyDescription()
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
        // Without networks info, description is empty (no purpose can be determined)
        issue!.Description.Should().BeEmpty();
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
                DestinationIps = new List<string> { "192.168.20.100" }, // IP in IoT subnet (vlan 20)
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
            CreateNetwork("IoT", NetworkPurpose.IoT, id: iotNetworkId, vlanId: 20), // subnet 192.168.20.0/24
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-network-1", vlanId: 1)
        };

        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: null, networks: networks);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Should include Source -> Destination format
        issue!.Description.Should().Be("Home -> IoT");
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
                DestinationIps = new List<string> { "192.168.30.100" }, // IP in Security subnet (vlan 30)
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
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: securityNetworkId, vlanId: 30) // subnet 192.168.30.0/24
        };

        var issues = _analyzer.DetectShadowedRules(rules, networkConfigs: null, externalZoneId: null, networks: networks);

        var issue = issues.FirstOrDefault(i => i.Type == "ALLOW_EXCEPTION_PATTERN");
        issue.Should().NotBeNull();
        // Source is CLIENT (no network), destination is Security - should use "Device(s)" for unknown source
        issue!.Description.Should().Be("Device(s) -> Security");
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
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-net", vlanId: 1), // subnet 192.168.1.0/24
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
        // Should determine both source (Home from IP) and destination (Security from IP) using purpose names
        issue!.Description.Should().Be("Home -> Security");
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
        issue!.Description.Should().BeEmpty();
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

    [Fact]
    public void DetectShadowedRules_NarrowDenyWithDomains_DoesNotShadowBroadAllow()
    {
        // Scenario: "Block Scam Domains" (narrow) should NOT shadow "Allow NTP Access" (broad)
        // because the deny blocks only specific domains while the allow is for any destination
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-scam",
                Name = "Block Scam Domains",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "WEB",
                WebDomains = new List<string> { "scam-site.com", "phishing.net" }
            },
            new FirewallRule
            {
                Id = "allow-ntp",
                Name = "Allow NTP Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "123"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT report that Allow NTP is ineffective due to Block Scam Domains
        issues.Should().NotContain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow NTP Access") &&
            i.Message.Contains("Block Scam Domains"));
    }

    [Fact]
    public void DetectShadowedRules_NarrowDenyWithNetworks_DoesNotShadowBroadAllow()
    {
        // Scenario: "Block Access to VPN Network" should NOT shadow "Allow External Access"
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-vpn",
                Name = "Block Access to VPN",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "vpn-net-id" }
            },
            new FirewallRule
            {
                Id = "allow-external",
                Name = "Allow External Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT report that Allow External is ineffective due to Block VPN
        issues.Should().NotContain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow External Access") &&
            i.Message.Contains("Block Access to VPN"));
    }

    [Fact]
    public void DetectShadowedRules_NarrowDenyWithIps_DoesNotShadowBroadAllow()
    {
        // Scenario: "Block Specific IPs" should NOT shadow "Allow Internet Access"
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-ips",
                Name = "Block Specific IPs",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "10.0.0.1", "10.0.0.2" }
            },
            new FirewallRule
            {
                Id = "allow-internet",
                Name = "Allow Internet Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT report that Allow Internet is ineffective
        issues.Should().NotContain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow Internet Access") &&
            i.Message.Contains("Block Specific IPs"));
    }

    [Fact]
    public void DetectShadowedRules_NarrowDenyWithAppIds_DoesNotShadowBroadAllow()
    {
        // Scenario: "Block TikTok" (specific app ID) should NOT shadow "Allow Internet"
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-tiktok",
                Name = "Block TikTok",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY",
                AppIds = new List<int> { 1234567 } // Some app ID for TikTok
            },
            new FirewallRule
            {
                Id = "allow-internet",
                Name = "Allow Internet Access",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // Should NOT report that Allow Internet is ineffective due to Block TikTok
        issues.Should().NotContain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow Internet Access") &&
            i.Message.Contains("Block TikTok"));
    }

    [Fact]
    public void DetectShadowedRules_BroadDeny_DoesShadowNarrowAllow()
    {
        // Scenario: Broad "Block All External" SHOULD shadow narrow "Allow HTTP"
        // because the deny is broader than the allow
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-all",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                DestinationMatchingTarget = "ANY"
            },
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // SHOULD report that Allow HTTP is ineffective because the deny blocks all traffic first
        issues.Should().Contain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow HTTP") &&
            i.Message.Contains("Block All External"));
    }

    [Fact]
    public void DetectShadowedRules_BroadDeny_DoesShadowAppBasedAllow()
    {
        // Scenario: Broad "Block All External" SHOULD shadow app-based "Allow HTTP Apps"
        // because the deny blocks all traffic including HTTP app traffic
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-all",
                Name = "Block All External",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                DestinationMatchingTarget = "ANY"
            },
            new FirewallRule
            {
                Id = "allow-http-apps",
                Name = "Allow HTTP Apps",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "tcp_udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                DestinationMatchingTarget = "APP",
                AppIds = new List<int> { 852190, 1245278 } // HTTP (852190), HTTPS (1245278)
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // SHOULD report that Allow HTTP Apps is ineffective because the deny blocks all traffic first
        issues.Should().Contain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow HTTP Apps") &&
            i.Message.Contains("Block All External"));
    }

    [Fact]
    public void DetectShadowedRules_BroadDeny_DoesShadowAppCategoryAllow()
    {
        // Scenario: Broad deny SHOULD shadow app category-based allow (Web Services category)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "deny-all",
                Name = "Block Internet",
                Action = "DROP",
                Enabled = true,
                Index = 1,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" },
                DestinationMatchingTarget = "ANY"
            },
            new FirewallRule
            {
                Id = "allow-web-category",
                Name = "Allow Web Services",
                Action = "ALLOW",
                Enabled = true,
                Index = 2,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" },
                DestinationMatchingTarget = "APP_CATEGORY",
                AppCategoryIds = new List<int> { 13 } // Web Services category
            }
        };

        var issues = _analyzer.DetectShadowedRules(rules);

        // SHOULD report that Allow Web Services is ineffective
        issues.Should().Contain(i =>
            i.Type == "DENY_SHADOWS_ALLOW" &&
            i.Message.Contains("Allow Web Services") &&
            i.Message.Contains("Block Internet"));
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
    public void DetectPermissiveRules_AnyAnyAllWithDestPorts_NotFlaggedAsPermissive()
    {
        // A rule with source=ANY, dest=ANY, protocol=all but with specific destination ports
        // (e.g., from a port group) should NOT be flagged as PERMISSIVE_RULE.
        // It should fall through to the BROAD_RULE check, which also skips it due to specific ports.
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-port-group",
                Name = "Allow IoT to External Ports",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80,443"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnyAnyAllWithSourcePort_NotFlaggedAsPermissive()
    {
        // A rule with source=ANY, dest=ANY, protocol=all but with specific source port
        // should NOT be flagged as PERMISSIVE_RULE (source port narrows the rule)
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "rule-source-port",
                Name = "Allow from Ephemeral Ports",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY",
                SourcePort = "1024-65535"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceInCustomZone_NotFlaggedAsBroad()
    {
        // A rule with ANY source scoped to a custom zone (default_zone=false) should NOT be
        // flagged as broad, since custom zones are user-created and intentionally scoped
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "custom-zone-rule", Name = "Custom Zone Allow", Action = "ACCEPT",
                Enabled = true, Index = 1,
                SourceMatchingTarget = "ANY", SourceZoneId = "custom-zone-1",
                DestinationMatchingTarget = "NETWORK"
            }
        };
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Net1", NetworkPurpose.Corporate, id: "net1", firewallZoneId: "custom-zone-1"),
            CreateNetwork("Net2", NetworkPurpose.Corporate, id: "net2", firewallZoneId: "custom-zone-1"),
            CreateNetwork("Net3", NetworkPurpose.Corporate, id: "net3", firewallZoneId: "custom-zone-1"),
            CreateNetwork("Net4", NetworkPurpose.Corporate, id: "net4", firewallZoneId: "custom-zone-1"),
            CreateNetwork("Net5", NetworkPurpose.Corporate, id: "net5", firewallZoneId: "custom-zone-1"),
            CreateNetwork("Net6", NetworkPurpose.Corporate, id: "net6", firewallZoneId: "custom-zone-1")
        };
        var zoneLookup = new FirewallZoneLookup(new[]
        {
            new UniFiFirewallZone { Id = "custom-zone-1", Name = "Test", ZoneKey = "", IsDefaultZone = false }
        });

        var issues = _analyzer.DetectPermissiveRules(rules, networks, zoneLookup);

        // Even with 6 networks, custom zone suppresses BROAD_RULE
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceInDefaultZoneWithFewNetworks_NotFlaggedAsBroad()
    {
        // A rule with ANY source scoped to a default zone with < 5 networks should NOT be
        // flagged as broad, since the zone already restricts scope sufficiently
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "small-zone-rule", Name = "Small Zone Allow", Action = "ACCEPT",
                Enabled = true, Index = 1,
                SourceMatchingTarget = "ANY", SourceZoneId = "internal-zone-1",
                DestinationMatchingTarget = "NETWORK"
            }
        };
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Net1", NetworkPurpose.Corporate, id: "net1", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net2", NetworkPurpose.Corporate, id: "net2", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net3", NetworkPurpose.Corporate, id: "net3", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net4", NetworkPurpose.Corporate, id: "net4", firewallZoneId: "internal-zone-1")
        };
        var zoneLookup = new FirewallZoneLookup(new[]
        {
            new UniFiFirewallZone { Id = "internal-zone-1", Name = "Internal", ZoneKey = "internal", IsDefaultZone = true }
        });

        var issues = _analyzer.DetectPermissiveRules(rules, networks, zoneLookup);

        // 4 networks < 5 threshold, so suppressed
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceInDefaultZoneWithManyNetworks_FlaggedAsBroad()
    {
        // A rule with ANY source scoped to a default zone with >= 5 networks SHOULD be flagged
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "large-zone-rule", Name = "Large Zone Allow", Action = "ACCEPT",
                Enabled = true, Index = 1,
                SourceMatchingTarget = "ANY", SourceZoneId = "internal-zone-1",
                DestinationMatchingTarget = "NETWORK"
            }
        };
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Net1", NetworkPurpose.Corporate, id: "net1", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net2", NetworkPurpose.Corporate, id: "net2", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net3", NetworkPurpose.Corporate, id: "net3", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net4", NetworkPurpose.Corporate, id: "net4", firewallZoneId: "internal-zone-1"),
            CreateNetwork("Net5", NetworkPurpose.Corporate, id: "net5", firewallZoneId: "internal-zone-1")
        };
        var zoneLookup = new FirewallZoneLookup(new[]
        {
            new UniFiFirewallZone { Id = "internal-zone-1", Name = "Internal", ZoneKey = "internal", IsDefaultZone = true }
        });

        var issues = _analyzer.DetectPermissiveRules(rules, networks, zoneLookup);

        // 5 networks >= 5 threshold, so flagged
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.BroadRule);
    }

    [Fact]
    public void DetectPermissiveRules_AnySourceNoZone_StillFlaggedAsBroad()
    {
        // A rule with ANY source and no zone scoping should still be flagged
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "no-zone-rule", Name = "No Zone Allow", Action = "ACCEPT",
                Enabled = true, Index = 1,
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "NETWORK"
            }
        };

        var issues = _analyzer.DetectPermissiveRules(rules);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.BroadRule);
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
    public void CheckInterVlanIsolation_V2ApiFormat_ReverseDirection_StillFlagsForwardDirection()
    {
        // A block rule in reverse direction (Corp → IoT) does NOT protect Corporate from IoT.
        // We must have a rule specifically blocking IoT → Corporate.
        // This is the correct behavior because UniFi isolation is directional.
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

        // Should flag IoT → Corporate as missing (Corp → IoT rule does NOT protect Corporate from IoT)
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_V2ApiFormat_BothDirections_NoIssue()
    {
        // When both directions are blocked, no issues should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-iot",
                Name = "Block Corp to IoT",
                Action = "BLOCK",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "iot-net-id" }
            },
            new FirewallRule
            {
                Id = "block-iot-to-corp",
                Name = "Block IoT to Corp",
                Action = "BLOCK",
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
    public void CheckInterVlanIsolation_AllowRuleToExternalZone_NotFlaggedAsIsolationBypass()
    {
        // Test that ALLOW rules targeting the External zone (internet access) are NOT flagged
        // as isolation bypass - they're for outbound internet, not inter-VLAN traffic
        var externalZoneId = "external-zone-123";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id"),
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            // NTP rule: Management -> External zone (should NOT be flagged)
            new FirewallRule
            {
                Id = "allow-mgmt-ntp",
                Name = "[Network] NTP Access",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "udp",
                DestinationPort = "123",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net-id" },
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule between IoT and Corporate (should be flagged)
            new FirewallRule
            {
                Id = "allow-iot-to-corp",
                Name = "Bad IoT to Corp Rule",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks, externalZoneId);

        // The NTP rule targeting External zone should NOT be flagged as isolation bypass
        issues.Should().NotContain(i => i.Type == "ISOLATION_BYPASSED" && i.Message.Contains("NTP Access"));

        // But the IoT to Corp rule SHOULD be flagged
        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.Message.Contains("Bad IoT to Corp Rule"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleWithAnyDestination_NoExternalZoneId_DirectionalCheck()
    {
        // Isolation checks are directional:
        // - IoT/Guest are "isolated" = outbound from them is blocked
        // - Management/Security are "protected" = inbound to them is blocked
        // Management → IoT (via ANY destination) should NOT be flagged because
        // management devices often need to manage IoT devices
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id"),
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-mgmt-any",
                Name = "Management to Any",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net-id" },
                DestinationMatchingTarget = "ANY"
                // No DestinationZoneId set
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks, externalZoneId: null);

        // Management → IoT should NOT be flagged (management can manage IoT)
        // IoT → Management would be flagged, but there's no such rule
        issues.Should().NotContain(i => i.Type == "ISOLATION_BYPASSED");
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleEclipsedByBlockRule_NotFlaggedAsIsolationBypass()
    {
        // An allow rule that is eclipsed by a block rule with lower index should NOT be flagged
        // as ISOLATION_BYPASSED because the allow rule never actually takes effect.
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "security-net-id")
        };
        var rules = new List<FirewallRule>
        {
            // Block rule with lower index (higher priority) - takes effect first
            new FirewallRule
            {
                Id = "block-iot-to-security",
                Name = "Block IoT to Security",
                Action = "DROP",
                Enabled = true,
                Index = 100,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "security-net-id" }
            },
            // Allow rule with higher index (lower priority) - eclipsed by block rule
            new FirewallRule
            {
                Id = "allow-iot-to-security",
                Name = "Allow IoT to Security (eclipsed)",
                Action = "ALLOW",
                Enabled = true,
                Index = 200,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "security-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag the allow rule - it's eclipsed by a block rule and never takes effect
        issues.Should().NotContain(i => i.Type == "ISOLATION_BYPASSED");
        // Should also not flag missing isolation - the block rule provides it
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("IoT") && i.Message.Contains("Security"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBeforeBlockRule_FlaggedAsIsolationBypass()
    {
        // An allow rule with lower index that eclipses a block rule SHOULD be flagged
        // because the allow rule takes effect and bypasses the intended block.
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "security-net-id")
        };
        var rules = new List<FirewallRule>
        {
            // Allow rule with lower index (higher priority) - takes effect first
            new FirewallRule
            {
                Id = "allow-iot-to-security",
                Name = "Allow IoT to Security (takes effect)",
                Action = "ALLOW",
                Enabled = true,
                Index = 100,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "security-net-id" }
            },
            // Block rule with higher index (lower priority) - eclipsed by allow rule
            new FirewallRule
            {
                Id = "block-iot-to-security",
                Name = "Block IoT to Security (eclipsed)",
                Action = "DROP",
                Enabled = true,
                Index = 200,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "security-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag the allow rule - it takes effect and bypasses isolation
        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.Message.Contains("Allow IoT to Security"));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckInterVlanIsolation_HomeWithIsolation_ToManagement_NoIssue(bool mgmtIsolated)
    {
        // Home with isolation enabled cannot reach other VLANs, so no issue should be flagged
        // regardless of whether Management also has isolation enabled
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: true),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: mgmtIsolated)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Home"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckInterVlanIsolation_CorporateWithIsolation_ToManagement_NoIssue(bool mgmtIsolated)
    {
        // Corporate with isolation enabled cannot reach other VLANs, so no issue should be flagged
        // regardless of whether Management also has isolation enabled
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: true),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: mgmtIsolated)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate"));
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
    public void CheckInterVlanIsolation_ManagementWithSystemIsolation_StillChecksInbound()
    {
        // Management network WITH system isolation enabled SHOULD still be checked for INBOUND access.
        // UniFi's "Network Isolation" feature only blocks OUTBOUND traffic FROM the isolated network.
        // It does NOT block INBOUND traffic TO the isolated network from other VLANs.
        // Therefore, we must verify that other networks are blocked from reaching Management.
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: true) // System isolation ON
        };
        var rules = new List<FirewallRule>(); // No manual rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag missing isolation - isolation only blocks outbound from Management,
        // not inbound from Corporate to Management
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_SecurityWithSystemIsolation_StillChecksInbound()
    {
        // Security network WITH system isolation enabled SHOULD still be checked for INBOUND access.
        // UniFi's "Network Isolation" feature only blocks OUTBOUND traffic FROM the isolated network.
        // It does NOT block INBOUND traffic TO the isolated network from other VLANs.
        // Therefore, we must verify that IoT/Guest networks are blocked from reaching Security (cameras).
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", networkIsolationEnabled: true) // System isolation ON
        };
        var rules = new List<FirewallRule>(); // No manual rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag missing isolation - isolation only blocks outbound from Security,
        // not inbound from IoT to Security (cameras)
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToSecurityWithIsolation_StillChecksInbound()
    {
        // Guest network trying to access Security cameras - should be flagged even if Security has isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>(); // No manual rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag missing isolation - guests shouldn't be able to access cameras
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_ManagementWithIsolation_HasBlockRule_NoIssue()
    {
        // Management network with isolation enabled, but there IS a block rule for inbound access - no issue
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - there's a block rule protecting Management from Corporate
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    #region VLAN Isolation Gaps - New Tests for Missing Checks

    [Fact]
    public void CheckInterVlanIsolation_ManagementToSecurity_NoBlockRule_FlagsMissing()
    {
        // Management → Security should be blocked (NVR on Management shouldn't have open access to cameras)
        // This is currently a gap in the audit
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - Management can reach Security cameras without explicit allow
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Management") && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_ManagementToSecurity_WithSourceIsolation_NoIssue()
    {
        // If Management has network isolation enabled, it can't initiate outbound connections
        // So Management → Security should NOT be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: true), // Isolation ON
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", networkIsolationEnabled: true) // Also isolated to avoid reverse direction flag
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag any issues - both networks have isolation enabled so neither can initiate connections
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_PrinterToSecurity_NoBlockRule_FlagsMissing()
    {
        // Printers have no legitimate need to access cameras - should be blocked
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Printers", NetworkPurpose.Printer, id: "printer-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - Printers shouldn't access Security
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Printers") && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_PrinterToManagement_NoBlockRule_FlagsMissing()
    {
        // Printers have no legitimate need to access management network - should be blocked
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Printers", NetworkPurpose.Printer, id: "printer-net-id", networkIsolationEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - Printers shouldn't access Management
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Printers") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_DmzToSecurity_NoBlockRule_FlagsMissing()
    {
        // DMZ (internet-facing services) should never access security cameras
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("DMZ", NetworkPurpose.Dmz, id: "dmz-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - DMZ shouldn't access Security
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("DMZ") && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_DmzToManagement_NoBlockRule_FlagsMissing()
    {
        // DMZ (internet-facing services) should never access management network
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("DMZ", NetworkPurpose.Dmz, id: "dmz-net-id", networkIsolationEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - DMZ shouldn't access Management
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("DMZ") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_UnknownToSecurity_NoBlockRule_FlagsMissing()
    {
        // Unknown/unclassified networks should be treated as untrusted
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Unclassified", NetworkPurpose.Unknown, id: "unknown-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - Unknown shouldn't access Security
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Unclassified") && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_UnknownToManagement_NoBlockRule_FlagsMissing()
    {
        // Unknown/unclassified networks should be treated as untrusted
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Unclassified", NetworkPurpose.Unknown, id: "unknown-net-id", networkIsolationEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should flag missing isolation - Unknown shouldn't access Management
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Unclassified") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_DestinationIsolationDoesNotSatisfy_StillFlagsIssue()
    {
        // CRITICAL: Destination having isolation enabled does NOT protect it from inbound traffic
        // UniFi isolation only blocks OUTBOUND from isolated networks, not INBOUND to them
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: false),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", networkIsolationEnabled: true) // Destination has isolation
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // MUST flag - destination isolation does NOT block inbound from Corporate
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Corporate") && i.Message.Contains("Cameras"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToCorporate_FlagsEvenWhenCorporateHasIsolation()
    {
        // Corporate has isolation enabled, but that only blocks Corporate → other
        // IoT can still reach Corporate - should flag missing block rule
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // MUST flag - IoT can still reach Corporate even though Corporate has isolation
        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-IOT" &&
            i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToHome_FlagsEvenWhenHomeHasIsolation()
    {
        // Home has isolation enabled, but that only blocks Home → other
        // IoT can still reach Home - should flag missing block rule
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: false),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // MUST flag - IoT can still reach Home even though Home has isolation
        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-IOT" &&
            i.Message.Contains("IoT") && i.Message.Contains("Home"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToCorporate_FlagsEvenWhenCorporateHasIsolation()
    {
        // Corporate has isolation enabled, but Guest can still reach Corporate
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id", networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // MUST flag - Guest can still reach Corporate even though Corporate has isolation
        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-GUEST" &&
            i.Message.Contains("Guest") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToHome_FlagsEvenWhenHomeHasIsolation()
    {
        // Home has isolation enabled, but Guest can still reach Home
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id", networkIsolationEnabled: false),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: true)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // MUST flag - Guest can still reach Home even though Home has isolation
        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-GUEST" &&
            i.Message.Contains("Guest") && i.Message.Contains("Home"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToCorporate_NoFlagWhenIoTHasIsolation()
    {
        // IoT has isolation enabled - can't initiate outbound, so no issue
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id", networkIsolationEnabled: true),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - IoT with isolation enabled can't initiate connections
        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-IOT" &&
            i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToHome_NoFlagWhenGuestHasIsolation()
    {
        // Guest has isolation enabled - can't initiate outbound, so no issue
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id", networkIsolationEnabled: true),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>(); // No rules

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - Guest with isolation enabled can't initiate connections
        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-GUEST" &&
            i.Message.Contains("Guest") && i.Message.Contains("Home"));
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateToHome_NoBlockRule_FlaggedAsRecommended()
    {
        // Corporate to Home without block rule should be Recommended (not Critical)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.RuleId == "FW-ISOLATION-CORP-HOME" &&
            i.Message.Contains("Work Network") && i.Message.Contains("Home Network"));
        issues.First(i => i.RuleId == "FW-ISOLATION-CORP-HOME").Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void CheckInterVlanIsolation_HomeToCorporate_NoBlockRule_FlaggedAsRecommended()
    {
        // Home to Corporate without block rule should be Recommended (not Critical)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id"),
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.RuleId == "FW-ISOLATION-HOME-CORP" &&
            i.Message.Contains("Home Network") && i.Message.Contains("Work Network"));
        issues.First(i => i.RuleId == "FW-ISOLATION-HOME-CORP").Severity.Should().Be(AuditSeverity.Recommended);
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateToHome_WithBlockRule_NoIssue()
    {
        // Block rule between Corporate and Home should satisfy isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-home",
                Name = "Block Corp to Home",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "home-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-CORP-HOME");
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateToHome_Bidirectional_BlockOneDirection_StillFlagsOther()
    {
        // Block rule Corp→Home does NOT protect Home→Corp direction
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-home",
                Name = "Block Corp to Home",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "home-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Corp→Home should be satisfied
        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-CORP-HOME");
        // Home→Corp should still be flagged
        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-HOME-CORP" &&
            i.Message.Contains("Home Network") && i.Message.Contains("Work Network"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckInterVlanIsolation_CorporateWithIsolation_ToHome_NoIssue(bool homeIsolated)
    {
        // Corporate with isolation enabled can't initiate outbound, so no issue
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: true),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: homeIsolated)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-CORP-HOME" &&
            i.Message.Contains("Work Network"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CheckInterVlanIsolation_HomeWithIsolation_ToCorporate_NoIssue(bool corpIsolated)
    {
        // Home with isolation enabled can't initiate outbound, so no issue
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", networkIsolationEnabled: true),
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id", networkIsolationEnabled: corpIsolated)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-HOME-CORP" &&
            i.Message.Contains("Home Network"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleCorporateToHome_FlaggedAsIsolationBypass()
    {
        // An allow rule between Corporate and Home should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-corp-to-home",
                Name = "Allow Corp to Home",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "home-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" &&
            i.RuleId == "FW-ISOLATION-BYPASS" &&
            i.Message.Contains("Allow Corp to Home"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleHomeToCorporate_FlaggedAsIsolationBypass()
    {
        // An allow rule from Home to Corporate should also be flagged (bidirectional)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id"),
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-home-to-corp",
                Name = "Allow Home to Corp",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "home-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" &&
            i.RuleId == "FW-ISOLATION-BYPASS" &&
            i.Message.Contains("Allow Home to Corp"));
    }

    #endregion

    #region Server Network Isolation Tests

    [Fact]
    public void CheckInterVlanIsolation_IoTToServer_NoBlockRule_FlaggedAsMissing()
    {
        // IoT to Server without a block rule should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("IoT") && i.Message.Contains("Server"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestToServer_NoBlockRule_FlaggedAsCritical()
    {
        // Guest to Server without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical);
        issues.First(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Server"))
            .Message.Should().Contain("Guest");
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateToServer_NoBlockRule_NotFlagged()
    {
        // Corporate to Server should NOT be flagged - both are trusted
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Corporate") && i.Message.Contains("Server"));
    }

    [Fact]
    public void CheckInterVlanIsolation_HomeToServer_NoBlockRule_NotFlagged()
    {
        // Home to Server should NOT be flagged - both are trusted
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Home") && i.Message.Contains("Server"));
    }

    [Fact]
    public void CheckInterVlanIsolation_ServerToManagement_NoBlockRule_FlaggedAsCritical()
    {
        // Server to Management without block rule should be Critical
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id", networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Severity == AuditSeverity.Critical &&
            i.Message.Contains("Server") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_ServerToSecurity_NoBlockRule_FlaggedAsMissing()
    {
        // Server to Security without block rule should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id"),
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net-id")
        };
        var rules = new List<FirewallRule>();

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Server") && i.Message.Contains("Security"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToServer_WithBlockRule_NoIssue()
    {
        // IoT to Server with a block rule should not be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-iot-to-server",
                Name = "Block IoT to Server",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "server-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("IoT") && i.Message.Contains("Server"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTToServer_AllowRule_FlaggedAsBypassed()
    {
        // An allow rule from IoT to Server should be flagged as isolation bypassed
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id"),
            CreateNetwork("Server VLAN", NetworkPurpose.Server, id: "server-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-iot-to-server",
                Name = "Allow IoT to Server",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "server-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" &&
            i.Message.Contains("IoT") && i.Message.Contains("Server"));
    }

    #endregion

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleWithConnectionStateAll_NoIssue()
    {
        // Block rule with ConnectionStateType = "ALL" blocks all traffic including NEW connections - valid isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" },
                ConnectionStateType = "ALL"  // Blocks all connection states including NEW
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - rule with ConnectionStateType=ALL blocks NEW connections
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleWithOnlyInvalidState_StillFlagsIssue()
    {
        // Block rule that only blocks INVALID connections does NOT provide inter-VLAN isolation
        // INVALID = malformed packets, not legitimate connection attempts
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-invalid-traffic",
                Name = "Block Invalid Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,  // This is a predefined UniFi rule
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY",
                ConnectionStateType = "CUSTOM",
                ConnectionStates = new List<string> { "INVALID" }  // Only blocks INVALID, not NEW
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag - rule only blocks INVALID connections, not NEW connections
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleWithNewState_NoIssue()
    {
        // Block rule with CUSTOM connection states including NEW does block inter-VLAN traffic
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" },
                ConnectionStateType = "CUSTOM",
                ConnectionStates = new List<string> { "NEW", "ESTABLISHED", "RELATED", "INVALID" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - rule blocks NEW connections
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleWithNoConnectionStateType_NoIssue()
    {
        // Block rule without ConnectionStateType specified - defaults to blocking all traffic (legacy behavior)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
                // No ConnectionStateType - defaults to blocking all (including NEW)
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - no connection state type means block all
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleWithNewOnlyState_NoIssue()
    {
        // Block rule that only specifies NEW - valid for blocking new connection attempts
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-new-connections",
                Name = "Block New Connections to Mgmt",
                Action = "DROP",
                Enabled = true,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" },
                ConnectionStateType = "CUSTOM",
                ConnectionStates = new List<string> { "NEW" }  // Only NEW, but that's what we care about
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - rule blocks NEW connections
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleBeforeBlockRule_FlagsIssue()
    {
        // Allow rule with lower index eclipses block rule with higher index
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-all-traffic",
                Name = "Allow All Traffic",
                Action = "ACCEPT",
                Enabled = true,
                Index = 100,  // Lower index = higher priority
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            },
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Index = 200,  // Higher index = lower priority (eclipsed by allow rule)
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // SHOULD flag as ISOLATION_BYPASSED - the allow rule is the problem, more specific than "MISSING_ISOLATION"
        issues.Should().Contain(i => i.Type == "ISOLATION_BYPASSED" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_AllowRuleWithNoBlockRule_BypassedNotMissing()
    {
        // When an allow rule exists for a network pair that should be isolated,
        // we should report "ISOLATION_BYPASSED" (naming the specific rule) but NOT "MISSING_ISOLATION"
        // (which would be redundant and less actionable)
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home", NetworkPurpose.Home, id: "home-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            // Allow rule without any block rule
            new FirewallRule
            {
                Id = "allow-home-to-mgmt",
                Name = "Test Allow Home to Mgmt",
                Action = "ALLOW",
                Enabled = true,
                Index = 100,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "home-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should report ISOLATION_BYPASSED for the allow rule
        issues.Should().Contain(i =>
            i.Type == "ISOLATION_BYPASSED" &&
            i.Message.Contains("Test Allow Home to Mgmt") &&
            i.Message.Contains("Home") &&
            i.Message.Contains("Management"));

        // Should NOT also report MISSING_ISOLATION for the same network pair (redundant)
        issues.Should().NotContain(i =>
            i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Home") &&
            i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleBeforeAllowRule_NoIssue()
    {
        // Block rule with lower index takes precedence over allow rule with higher index
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                Index = 100,  // Lower index = higher priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
            },
            new FirewallRule
            {
                Id = "allow-all-traffic",
                Name = "Allow All Traffic",
                Action = "ACCEPT",
                Enabled = true,
                Index = 200,  // Higher index = lower priority
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - block rule comes before allow rule
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_SpecificBlockRuleBeforeGenericAllowRule_NoIssue()
    {
        // Specific block rule (network-targeted) with lower index beats generic allow rule
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "block-corp-to-mgmt",
                Name = "Block Corp to Mgmt",
                Action = "DROP",
                Enabled = true,
                Index = 50,  // Lower index = higher priority
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net-id" },
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "mgmt-net-id" }
            },
            new FirewallRule
            {
                Id = "allow-all-traffic",
                Name = "Allow All Traffic",
                Action = "ACCEPT",
                Enabled = true,
                Index = 30000,  // High index (UniFi default rules have high indices)
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        // Should NOT flag - specific block rule has lower index
        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" && i.Message.Contains("Corporate") && i.Message.Contains("Management"));
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

    #region CheckInternetDisabledBroadAllow Tests

    [Fact]
    public void CheckInternetDisabledBroadAllow_InternetEnabled_NoIssue()
    {
        // Network with internet enabled should not trigger the check
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: true)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-external",
                Name = "Allow External Access",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_InternetDisabled_BroadAllowRule_ReturnsIssue()
    {
        // Network with internet disabled AND a broad allow rule should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-all-external",
                Name = "Allow All External",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.Message.Should().Contain("Security Cameras");
        issue.Message.Should().Contain("Allow All External");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_HttpPort_ReturnsIssue()
    {
        // Allow rule for HTTP (port 80) on internet-disabled network should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Message.Should().Contain("HTTP access");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_HttpsPort_ReturnsIssue()
    {
        // Allow rule for HTTPS (port 443) on internet-disabled network should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-https",
                Name = "Allow HTTPS",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Message.Should().Contain("HTTPS access");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Port80_Udp_NoIssue()
    {
        // Port 80 with UDP only is NOT HTTP - HTTP requires TCP
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-80-udp",
                Name = "Allow Port 80 UDP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "udp", // UDP only - not HTTP
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        // UDP port 80 is NOT HTTP - should not be flagged
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Port80_Tcp_ReturnsIssue()
    {
        // Port 80 with TCP is HTTP - should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-80-tcp",
                Name = "Allow Port 80 TCP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        issues.First().Message.Should().Contain("HTTP");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Port80_TcpUdp_ReturnsIssue()
    {
        // Port 80 with TCP/UDP includes TCP, so it's HTTP
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-80-tcpudp",
                Name = "Allow Port 80 TCP/UDP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Port443_Udp_ReturnsIssue()
    {
        // Port 443 with UDP is QUIC (HTTP/3) - should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-443-udp",
                Name = "Allow Port 443 UDP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "udp", // UDP port 443 = QUIC
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        issues.First().Message.Should().Contain("HTTPS"); // QUIC is still web access
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_Port443_Tcp_ReturnsIssue()
    {
        // Port 443 with TCP is HTTPS - should be flagged
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-443-tcp",
                Name = "Allow Port 443 TCP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        issues.First().Message.Should().Contain("HTTPS");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_ExternalZone_AllProtocols_ReturnsIssue()
    {
        // Allow rule targeting external zone with ALL protocols on internet-disabled network should trigger
        var externalZoneId = "external-zone-1";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-external",
                Name = "Allow All External",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all", // All protocols = broad access
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationZoneId = externalZoneId,
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Message.Should().Contain("external/internet access");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_ExternalZone_SpecificProtocol_NoIssue()
    {
        // Allow rule targeting external zone with specific protocol (not HTTP ports) should NOT trigger
        var externalZoneId = "external-zone-1";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-external-tcp",
                Name = "Allow TCP External",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp", // Specific protocol without HTTP ports = narrow
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationZoneId = externalZoneId,
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // Not flagged because it's a specific protocol without HTTP/HTTPS ports
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_DisabledRule_NoIssue()
    {
        // Disabled allow rules should not trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-external-disabled",
                Name = "Allow External (Disabled)",
                Action = "ALLOW",
                Enabled = false, // Disabled
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_NarrowRule_NoIssue()
    {
        // Narrow allow rules (specific IPs, not HTTP/HTTPS) should not trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-ntp",
                Name = "Allow NTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "IP",
                DestinationIps = new List<string> { "192.0.2.1" },
                DestinationPort = "123"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_PortRange_ReturnsIssue()
    {
        // Port range including HTTP should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-web-range",
                Name = "Allow Web Ports",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80-443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Message.Should().Contain("HTTP/HTTPS access");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_HttpAppId_ReturnsIssue()
    {
        // Allow rule with HTTP App ID (852190) should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-http-app",
                Name = "Allow HTTP App",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "ANY",
                AppIds = new List<int> { 852190 } // HTTP app ID
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_WebServicesCategory_ReturnsIssue()
    {
        // Allow rule with Web Services category (13) should trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-web-category",
                Name = "Allow Web Services",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" },
                DestinationMatchingTarget = "APP_CATEGORY",
                AppCategoryIds = new List<int> { 13 } // Web Services category
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_PredefinedRule_NoIssue()
    {
        // Predefined/system rules (like "Allow Return Traffic") should be excluded
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-return",
                Name = "Allow Return Traffic",
                Action = "ALLOW",
                Enabled = true,
                Predefined = true, // System-created rule
                Protocol = "all",
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_SpecificDomains_NoIssue()
    {
        // Rules with specific WebDomains (like UniFi cloud access) should NOT trigger
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-unifi",
                Name = "Allow UniFi Access",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net" },
                DestinationMatchingTarget = "WEB",
                WebDomains = new List<string> { "ui.com", "unifi.ui.com" }
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_NtpPort_NoIssue()
    {
        // Rules with NTP port (123) should NOT trigger - it's narrow access
        var externalZoneId = "external-zone-1";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-ntp",
                Name = "NTP Access",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "udp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "mgmt-net" },
                DestinationZoneId = externalZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationPort = "123"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_MatchOppositeNetworks_ExcludesNetwork()
    {
        // Rule with SourceMatchOppositeNetworks=true excludes the listed network
        // If network IS in the list with match_opposite=true, rule does NOT apply to it
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-match-opposite",
                Name = "Allow HTTP Match Opposite",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "sec-net" }, // Security is in the list
                SourceMatchOppositeNetworks = true, // But match opposite means "everyone EXCEPT Security"
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        // Security should NOT be flagged because the rule excludes it (match opposite)
        // Management SHOULD be flagged because the rule applies to it (not in the exclusion list)
        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Metadata!["network_name"].Should().Be("Management");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_MatchOppositeNetworks_IncludesOtherNetworks()
    {
        // Rule with SourceMatchOppositeNetworks=true applies to networks NOT in the list
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net", internetAccessEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-except-corp",
                Name = "Allow All Except Corp",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "corp-net" }, // Corp is excluded
                SourceMatchOppositeNetworks = true, // Match opposite = everyone except corp
                DestinationMatchingTarget = "ANY"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        // Management should be flagged because it's NOT in the exclusion list
        issues.Should().ContainSingle();
        issues.First().Message.Should().Contain("Management");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_PortGroupWithHttp_ReturnsIssue()
    {
        // Test that port groups containing HTTP ports are detected
        // This verifies the full flow: port group -> parsing -> detection
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };

        // Set up port group with HTTP port 80
        var portGroup = new NetworkOptimizer.UniFi.Models.UniFiFirewallGroup
        {
            Id = "http-ports-group",
            Name = "HTTP Ports",
            GroupType = "port-group",
            GroupMembers = new List<string> { "80", "443" }
        };
        _analyzer.SetFirewallGroups(new[] { portGroup });

        // Parse a rule that references the port group
        var ruleJson = System.Text.Json.JsonDocument.Parse(@"{
            ""_id"": ""allow-http-portgroup"",
            ""name"": ""[TEST] Allow HTTP via Port Group"",
            ""action"": ""ALLOW"",
            ""enabled"": true,
            ""protocol"": ""tcp"",
            ""source"": {
                ""matching_target"": ""NETWORK"",
                ""network_ids"": [""sec-net""]
            },
            ""destination"": {
                ""matching_target"": ""ANY"",
                ""port_matching_type"": ""OBJECT"",
                ""port_group_id"": ""http-ports-group"",
                ""zone_id"": ""external-zone""
            }
        }").RootElement;

        var parsedRule = _analyzer.ParseFirewallPolicy(ruleJson);
        parsedRule.Should().NotBeNull();
        parsedRule!.DestinationPort.Should().Be("80,443"); // Verify port group was resolved

        var rules = new List<FirewallRule> { parsedRule };
        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, "external-zone");

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Message.Should().Contain("HTTP");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_PortGroupNotResolved_StillDetectsExternalZone()
    {
        // Test behavior when port group is NOT resolved (group not loaded)
        // Should still detect broad access via external zone with all protocols
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security", NetworkPurpose.Security, id: "sec-net", internetAccessEnabled: false)
        };

        // Don't set firewall groups - port group won't be resolved
        _analyzer.SetFirewallGroups(null);

        // Parse a rule that references a non-existent port group
        var ruleJson = System.Text.Json.JsonDocument.Parse(@"{
            ""_id"": ""allow-portgroup-unresolved"",
            ""name"": ""[TEST] Allow via Unresolved Port Group"",
            ""action"": ""ALLOW"",
            ""enabled"": true,
            ""protocol"": ""all"",
            ""source"": {
                ""matching_target"": ""NETWORK"",
                ""network_ids"": [""sec-net""]
            },
            ""destination"": {
                ""matching_target"": ""ANY"",
                ""port_matching_type"": ""OBJECT"",
                ""port_group_id"": ""nonexistent-group"",
                ""zone_id"": ""external-zone""
            }
        }").RootElement;

        var parsedRule = _analyzer.ParseFirewallPolicy(ruleJson);
        parsedRule.Should().NotBeNull();
        parsedRule!.DestinationPort.Should().BeNull(); // Port group not resolved

        var rules = new List<FirewallRule> { parsedRule };

        // With protocol=all and external zone, should still be detected as broad access
        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, "external-zone");

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_SourceCidrCoversNetwork_ReturnsIssue()
    {
        // Rule with IP-based source CIDR that covers the network's subnet should trigger
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "sec-net",
                Name = "Security Cameras",
                Purpose = NetworkPurpose.Security,
                VlanId = 99,
                Subnet = "192.168.99.0/24",
                InternetAccessEnabled = false
            }
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-http-cidr",
                Name = "Allow HTTP from CIDR",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.99.0/24" }, // Covers the Security subnet
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80,443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        issues.Should().ContainSingle();
        var issue = issues.First();
        issue.Type.Should().Be("INTERNET_BLOCK_BYPASSED");
        issue.Message.Should().Contain("Security Cameras");
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_SourceCidrDoesNotCoverNetwork_NoIssue()
    {
        // Rule with IP-based source CIDR that does NOT cover the network's subnet should NOT trigger
        var networks = new List<NetworkInfo>
        {
            new NetworkInfo
            {
                Id = "sec-net",
                Name = "Security Cameras",
                Purpose = NetworkPurpose.Security,
                VlanId = 99,
                Subnet = "192.168.99.0/24",
                InternetAccessEnabled = false
            }
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "allow-http-cidr",
                Name = "Allow HTTP from Different CIDR",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                SourceMatchingTarget = "IP",
                SourceIps = new List<string> { "192.168.50.0/24" }, // Different subnet
                DestinationMatchingTarget = "ANY",
                DestinationPort = "80,443"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, null);

        // Should NOT be flagged because the CIDR doesn't cover the Security network
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_InternetBlockedViaFirewall_BroadAllowRule_ReturnsIssue()
    {
        // Network has internetAccessEnabled=true but internet is blocked via firewall rule.
        // A narrow allow rule (port 80) bypasses the firewall-based internet block.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Block rule: blocks all internet access for this network's zone
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: allows HTTP (port 80) through, bypassing the block
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow IoT HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 999,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
        issues.First().Metadata!["network_name"].Should().Be("Security Cameras");
    }

    // --- Eclipse logic tests ---
    // Setup pattern: a network with internetAccessEnabled=true and firewallZoneId="internal-zone",
    // plus a "block all internet" rule (index=2000) that makes HasEffectiveInternetAccess return false.
    // An allow rule (index=1000) has lower index than the internet block so it isn't eclipsed by it.
    // A "test" block rule (index < allow) tests whether specific block types eclipse the allow.
    // Index ordering: test block (998) < allow (1000) < internet block (2000).

    [Fact]
    public void CheckInternetDisabledBroadAllow_WebBlockRule_DoesNotEclipsePortAllow_ReturnsIssue()
    {
        // A WEB-target block rule (like "Block Scam Domains") should NOT eclipse
        // a port-based allow rule, because WEB blocks target specific domain categories,
        // not arbitrary port/protocol traffic.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block (makes HasEffectiveInternetAccess return false)
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // WEB domain block (like "Block Scam Domains") - should NOT eclipse port-based allow
            new FirewallRule
            {
                Id = "block-scam-domains",
                Name = "Block Scam Domains",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "WEB",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // WEB block doesn't eclipse port-based allow, so the allow rule should be flagged
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_PortSpecificBlock_DoesNotEclipse_ReturnsIssue()
    {
        // A block rule for port 53 (DNS) should NOT eclipse an allow rule for port 80 (HTTP).
        // Port-specific blocks only cover their own ports.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // DNS block (port 53) - should NOT eclipse HTTP allow (port 80)
            new FirewallRule
            {
                Id = "block-dns",
                Name = "Block DNS",
                Action = "DROP",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "53"
            },
            // Allow rule: port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // Port 53 block doesn't eclipse port 80 allow
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_IpSpecificBlock_DoesNotEclipse_ReturnsIssue()
    {
        // A block rule targeting specific IPs (destTarget=IP) should NOT eclipse
        // a broad allow rule (destTarget=ANY), because it only blocks specific destinations.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // IP-specific block - should NOT eclipse broad allow
            new FirewallRule
            {
                Id = "block-specific-ip",
                Name = "Block Specific IPs",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "IP",
                DestinationZoneId = externalZoneId,
                DestinationIps = new List<string> { "10.0.0.0/8" }
            },
            // Allow rule: port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // IP-specific block doesn't eclipse broad allow
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_FullInternetBlock_Eclipses_NoIssue()
    {
        // A full internet block (protocol=all, no port, destTarget=ANY, destZone=external)
        // with lower index than the allow rule DOES eclipse it.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block (makes HasEffectiveInternetAccess return false)
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Another full internet block with lower index than the allow - DOES eclipse
            new FirewallRule
            {
                Id = "block-internet-2",
                Name = "Block All Internet 2",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // The full block at index 998 eclipses the allow at index 1000, so no issue
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_TcpBlockDoesNotEclipseTcpUdpAllow_ReturnsIssue()
    {
        // A TCP-only block should NOT eclipse a tcp_udp allow, because the block
        // doesn't cover the UDP portion of the allow rule.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // TCP-only block - doesn't cover UDP portion
            new FirewallRule
            {
                Id = "block-tcp",
                Name = "Block TCP",
                Action = "DROP",
                Enabled = true,
                Protocol = "tcp",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: tcp_udp port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // TCP block doesn't cover UDP portion of tcp_udp allow
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_TcpUdpBlockEclipsesTcpAllow_NoIssue()
    {
        // A tcp_udp block covers TCP, so it eclipses a TCP-only allow rule.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // tcp_udp block - covers both TCP and UDP
            new FirewallRule
            {
                Id = "block-tcpudp",
                Name = "Block TCP/UDP",
                Action = "DROP",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: TCP-only port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http-tcp",
                Name = "Allow HTTP TCP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // tcp_udp block covers TCP, so the TCP allow is eclipsed
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_AllProtocolBlockEclipsesTcpAllow_NoIssue()
    {
        // A protocol=all block covers everything, so it eclipses a TCP-only allow rule.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // all-protocol block
            new FirewallRule
            {
                Id = "block-all-proto",
                Name = "Block All Protocols",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: TCP port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http-tcp",
                Name = "Allow HTTP TCP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // protocol=all covers tcp, so the TCP allow is eclipsed
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_BlockInternalZone_DoesNotEclipseExternalAllow_ReturnsIssue()
    {
        // A block rule targeting the internal zone should NOT eclipse an allow rule
        // targeting the external zone, since they affect different zones.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Block targeting internal zone (NOT external) - should NOT eclipse external allow
            new FirewallRule
            {
                Id = "block-internal",
                Name = "Block Internal Zone",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = networkZoneId // Internal zone, NOT external
            },
            // Allow rule: port 80 to EXTERNAL zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // Internal-zone block doesn't eclipse external-zone allow
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_BlockNoZone_Eclipses_NoIssue()
    {
        // A block rule with no zone (null DestinationZoneId) applies everywhere,
        // so it DOES eclipse the allow rule.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Block with no zone - applies everywhere
            new FirewallRule
            {
                Id = "block-no-zone",
                Name = "Block All No Zone",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 998,
                SourceMatchingTarget = "ANY",
                DestinationMatchingTarget = "ANY"
                // No DestinationZoneId - applies everywhere
            },
            // Allow rule: port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http",
                Name = "Allow HTTP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp_udp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // No-zone block applies everywhere, so it eclipses the allow rule
        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInternetDisabledBroadAllow_MatchOppositeProtocol_TcpExcluded_DoesNotEclipseTcpAllow_ReturnsIssue()
    {
        // A block rule with protocol=tcp and MatchOppositeProtocol=true blocks everything
        // EXCEPT TCP. So a TCP allow rule is NOT eclipsed by it.
        var externalZoneId = "external-zone";
        var networkZoneId = "internal-zone";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Security Cameras", NetworkPurpose.Security, id: "sec-net",
                internetAccessEnabled: true, firewallZoneId: networkZoneId)
        };
        var rules = new List<FirewallRule>
        {
            // Internet block
            new FirewallRule
            {
                Id = "block-internet",
                Name = "Block IoT Internet",
                Action = "DROP",
                Enabled = true,
                Protocol = "all",
                Index = 2000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Block with MatchOppositeProtocol: protocol=tcp + match_opposite = blocks everything EXCEPT TCP
            new FirewallRule
            {
                Id = "block-except-tcp",
                Name = "Block Except TCP",
                Action = "DROP",
                Enabled = true,
                Protocol = "tcp",
                MatchOppositeProtocol = true, // Blocks everything EXCEPT TCP
                Index = 998,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId
            },
            // Allow rule: TCP port 80 to external zone
            new FirewallRule
            {
                Id = "allow-http-tcp",
                Name = "Allow HTTP TCP",
                Action = "ALLOW",
                Enabled = true,
                Protocol = "tcp",
                Index = 1000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = networkZoneId,
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = externalZoneId,
                DestinationPort = "80"
            }
        };

        var issues = _analyzer.CheckInternetDisabledBroadAllow(rules, networks, externalZoneId);

        // The block excludes TCP, so the TCP allow is NOT eclipsed
        issues.Should().ContainSingle();
        issues.First().Type.Should().Be(IssueTypes.InternetBlockBypassed);
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
        // This blocks: Corp→Corp, Corp→IoT, Guest→Corp, Guest→IoT
        // This does NOT block: IoT→Corp, IoT→Guest (IoT excluded from source)
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

        // IoT → Corporate should be flagged (IoT is excluded from the rule's source)
        // Note: Each direction must be explicitly blocked; a reverse rule is not sufficient
        issues.Should().Contain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
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

    #region DetectNetworkIsolationExceptions Tests

    [Fact]
    public void DetectNetworkIsolationExceptions_NoIsolatedNetworks_ReturnsNoIssues()
    {
        // Arrange - No networks have isolation enabled
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, networkIsolationEnabled: false),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, networkIsolationEnabled: false)
        };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow IoT to Corp", action: "allow",
                sourceNetworkIds: new List<string> { networks[0].Id })
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_NoPredefinedIsolatedNetworksRule_ReturnsNoIssues()
    {
        // Arrange - Network has isolation enabled but no predefined rule exists
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow IoT Access", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id })
            // No predefined "Isolated Networks" rule
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_UserAllowRuleFromIsolatedNetwork_ReturnsIssue()
    {
        // Arrange - IoT network has isolation enabled, user created an allow rule FROM it
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            // User-created allow rule from isolated network
            CreateFirewallRule("Allow IoT to Printer", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id }),
            // Predefined "Isolated Networks" rule
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.NetworkIsolationException);
        issues[0].Severity.Should().Be(AuditSeverity.Informational);
        issues[0].Description.Should().Be("IoT ->");
        issues[0].Message.Should().Contain("Allow IoT to Printer");
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_UserAllowRuleToIsolatedNetwork_ReturnsNoIssue()
    {
        // Arrange - Security network has isolation enabled, user created an allow rule TO it
        // Traffic TO isolated networks is implicitly allowed (predefined rules only block FROM isolated networks)
        var securityNetwork = CreateNetwork("Security", NetworkPurpose.Security, id: "sec-123", networkIsolationEnabled: true);
        var corpNetwork = CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-123", networkIsolationEnabled: false);
        var networks = new List<NetworkInfo> { securityNetwork, corpNetwork };
        var rules = new List<FirewallRule>
        {
            // User-created allow rule to isolated network (source is NOT isolated)
            CreateFirewallRuleWithDestination("Allow to Cameras", action: "allow",
                sourceNetworkIds: new List<string> { corpNetwork.Id },
                destNetworkIds: new List<string> { securityNetwork.Id }),
            // Predefined "Isolated Networks" rule
            CreatePredefinedIsolatedNetworksRule(securityNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert - No issue because source (Corporate) is not isolated
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_UserAllowRuleBetweenIsolatedNetworks_ReturnsIssue()
    {
        // Arrange - Both IoT and Security have isolation, allow rule between them
        // Only the SOURCE network (IoT) matters for isolation exceptions
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var securityNetwork = CreateNetwork("Security", NetworkPurpose.Security, id: "sec-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork, securityNetwork };
        var rules = new List<FirewallRule>
        {
            // User-created allow rule between isolated networks
            CreateFirewallRuleWithDestination("Allow IoT to Cameras", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id },
                destNetworkIds: new List<string> { securityNetwork.Id }),
            // Predefined rules for both networks
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id),
            CreatePredefinedIsolatedNetworksRule(securityNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert - Only source (IoT) is flagged, not destination
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.NetworkIsolationException);
        issues[0].Description.Should().Be("IoT -> Security");
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_PredefinedAllowRule_IsIgnored()
    {
        // Arrange - Predefined allow rule should not be flagged
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            // Predefined allow rule (system-generated) - should be ignored
            CreateFirewallRule("Allow Return Traffic", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id },
                predefined: true),
            // Predefined "Isolated Networks" rule
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_DisabledUserAllowRule_IsIgnored()
    {
        // Arrange - Disabled allow rule should not be flagged
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            // Disabled allow rule
            CreateFirewallRule("Allow IoT Access", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id },
                enabled: false),
            // Predefined "Isolated Networks" rule
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_ManagementNetwork_HasCorrectPurposeSuffix()
    {
        // Arrange - Management network exception should have (Management) suffix
        var mgmtNetwork = CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { mgmtNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow SSH to MGMT", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetwork.Id }),
            CreatePredefinedIsolatedNetworksRule(mgmtNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Description.Should().Be("Management ->");
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_ManagementNtpRule_IsExcluded()
    {
        // Arrange - NTP access rule from management network should NOT be flagged
        var mgmtNetwork = CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { mgmtNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRuleWithPort("Allow NTP", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetwork.Id },
                destPort: "123", protocol: "udp"),
            CreatePredefinedIsolatedNetworksRule(mgmtNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert - NTP rule should be excluded
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_ManagementUniFiRule_IsExcluded()
    {
        // Arrange - UniFi access rule from management network should NOT be flagged
        var mgmtNetwork = CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { mgmtNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow UniFi", action: "allow",
                sourceNetworkIds: new List<string> { mgmtNetwork.Id },
                webDomains: new List<string> { "ui.com" }),
            CreatePredefinedIsolatedNetworksRule(mgmtNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert - UniFi rule should be excluded
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_MultipleAllowRules_ReturnsMultipleIssues()
    {
        // Arrange - Multiple allow rules creating exceptions
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Allow IoT to Printer", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id }, index: 1),
            CreateFirewallRule("Allow IoT HTTP", action: "allow",
                sourceNetworkIds: new List<string> { iotNetwork.Id }, index: 2),
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().HaveCount(2);
        issues.Should().AllSatisfy(i => i.Type.Should().Be(IssueTypes.NetworkIsolationException));
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_BlockRule_IsIgnored()
    {
        // Arrange - Block rules should not be flagged as exceptions
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", networkIsolationEnabled: true);
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            CreateFirewallRule("Block IoT External", action: "block",
                sourceNetworkIds: new List<string> { iotNetwork.Id }),
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_SourceCidrCoversIsolatedNetwork_ReturnsIssue()
    {
        // Arrange - Rule uses CIDR source that covers an isolated network's subnet
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", vlanId: 99, networkIsolationEnabled: true);
        // Network has subnet 192.168.99.0/24 (from CreateNetwork helper)
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            // Rule with IP-based source that matches the IoT subnet
            CreateFirewallRuleWithSourceCidr("Allow IoT Subnet", action: "allow",
                sourceCidrs: new List<string> { "192.168.99.0/24" }),
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.NetworkIsolationException);
        issues[0].Description.Should().Be("IoT ->");
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_SourceCidrDoesNotCoverIsolatedNetwork_ReturnsNoIssue()
    {
        // Arrange - Rule uses CIDR source that does NOT cover the isolated network's subnet
        var iotNetwork = CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-123", vlanId: 99, networkIsolationEnabled: true);
        // Network has subnet 192.168.99.0/24, rule covers different subnet
        var networks = new List<NetworkInfo> { iotNetwork };
        var rules = new List<FirewallRule>
        {
            // Rule with IP-based source for different subnet
            CreateFirewallRuleWithSourceCidr("Allow Other Subnet", action: "allow",
                sourceCidrs: new List<string> { "192.168.50.0/24" }),
            CreatePredefinedIsolatedNetworksRule(iotNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks);

        // Assert
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_ExternalDestinationRule_ReturnsNoIssue()
    {
        // Arrange - rule allows traffic from isolated network to EXTERNAL zone (internet)
        // This is NOT an isolation exception because "Isolated Networks" rules block inter-VLAN traffic, not internet
        var mgmtNetwork = new NetworkInfo
        {
            Id = "mgmt-1",
            Name = "Management",
            VlanId = 99,
            Subnet = "192.168.99.0/24",
            NetworkIsolationEnabled = true,
            Purpose = NetworkPurpose.Management
        };
        var networks = new List<NetworkInfo> { mgmtNetwork };

        var externalZoneId = "external-zone-1";
        var rules = new List<FirewallRule>
        {
            // Rule allowing Management network to access internet (HTTP/HTTPS)
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Allow Management HTTP",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetwork.Id },
                DestinationZoneId = externalZoneId, // Targets external/internet zone
                DestinationMatchingTarget = "ANY"
            },
            CreatePredefinedIsolatedNetworksRule(mgmtNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks, externalZoneId);

        // Assert - no issue because it's external access, not inter-VLAN
        issues.Should().BeEmpty();
    }

    [Fact]
    public void DetectNetworkIsolationExceptions_InternalDestinationRule_ReturnsIssue()
    {
        // Arrange - rule allows traffic from isolated network to INTERNAL network (another VLAN)
        // This IS an isolation exception
        var mgmtNetwork = new NetworkInfo
        {
            Id = "mgmt-1",
            Name = "Management",
            VlanId = 99,
            Subnet = "192.168.99.0/24",
            NetworkIsolationEnabled = true,
            Purpose = NetworkPurpose.Management
        };
        var homeNetwork = new NetworkInfo
        {
            Id = "home-1",
            Name = "Home",
            VlanId = 1,
            Subnet = "192.168.1.0/24",
            NetworkIsolationEnabled = false,
            Purpose = NetworkPurpose.Home
        };
        var networks = new List<NetworkInfo> { mgmtNetwork, homeNetwork };

        var externalZoneId = "external-zone-1";
        var internalZoneId = "internal-zone-1";
        var rules = new List<FirewallRule>
        {
            // Rule allowing Management network to access Home network (inter-VLAN)
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Allow Management to Home",
                Action = "allow",
                Enabled = true,
                Index = 1,
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { mgmtNetwork.Id },
                DestinationZoneId = internalZoneId, // Internal zone, not external
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { homeNetwork.Id }
            },
            CreatePredefinedIsolatedNetworksRule(mgmtNetwork.Id)
        };

        // Act
        var issues = _analyzer.DetectNetworkIsolationExceptions(rules, networks, externalZoneId);

        // Assert - issue because it's inter-VLAN access
        issues.Should().HaveCount(1);
        issues[0].Type.Should().Be(IssueTypes.NetworkIsolationException);
    }

    private static FirewallRule CreateFirewallRuleWithSourceCidr(
        string name,
        string action = "allow",
        List<string>? sourceCidrs = null,
        bool enabled = true)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Enabled = enabled,
            Index = 1,
            SourceMatchingTarget = "IP",
            SourceIps = sourceCidrs
        };
    }

    private static FirewallRule CreateFirewallRuleWithPort(
        string name,
        string action = "allow",
        List<string>? sourceNetworkIds = null,
        string? destPort = null,
        string? protocol = null,
        bool enabled = true)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Enabled = enabled,
            Index = 1,
            SourceNetworkIds = sourceNetworkIds,
            SourceMatchingTarget = sourceNetworkIds?.Any() == true ? "NETWORK" : null,
            DestinationPort = destPort,
            Protocol = protocol
        };
    }

    private static FirewallRule CreatePredefinedIsolatedNetworksRule(string originNetworkId)
    {
        return new FirewallRule
        {
            Id = $"isolated-{originNetworkId}",
            Name = "Isolated Networks",
            Action = "block",
            Enabled = true,
            Predefined = true,
            Index = 30000 // High index like real UniFi rules
        };
    }

    private static FirewallRule CreateFirewallRuleWithDestination(
        string name,
        string action = "allow",
        List<string>? sourceNetworkIds = null,
        List<string>? destNetworkIds = null,
        bool enabled = true,
        bool predefined = false)
    {
        return new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            Action = action,
            Enabled = enabled,
            Predefined = predefined,
            Index = 1,
            SourceNetworkIds = sourceNetworkIds,
            SourceMatchingTarget = sourceNetworkIds?.Any() == true ? "NETWORK" : null,
            DestinationNetworkIds = destNetworkIds,
            DestinationMatchingTarget = destNetworkIds?.Any() == true ? "NETWORK" : null
        };
    }

    #endregion

    #region AppliesToSourceNetwork Zone Tests

    [Fact]
    public void AppliesToSourceNetwork_MatchingZones_NetworkSource_ReturnsTrue()
    {
        var networkId = "security-net-001";
        var zoneId = "custom-zone-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security, id: networkId,
            vlanId: 42, firewallZoneId: zoneId);
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block Security Internet",
            SourceMatchingTarget = "NETWORK",
            SourceNetworkIds = new List<string> { networkId },
            SourceZoneId = zoneId
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_MismatchedZones_NetworkSource_ReturnsFalse()
    {
        var networkId = "security-net-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security, id: networkId,
            vlanId: 42, firewallZoneId: "internal-zone");
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block Custom Zone Internet",
            SourceMatchingTarget = "NETWORK",
            SourceNetworkIds = new List<string> { networkId },
            SourceZoneId = "custom-zone-001"
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeFalse();
    }

    [Fact]
    public void AppliesToSourceNetwork_MatchingZones_AnySource_ReturnsTrue()
    {
        var zoneId = "custom-zone-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security,
            vlanId: 42, firewallZoneId: zoneId);
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block All in Zone",
            SourceMatchingTarget = "ANY",
            SourceZoneId = zoneId
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_MismatchedZones_AnySource_ReturnsFalse()
    {
        // Rule scoped to custom zone with Source=ANY should NOT match networks in other zones
        var network = CreateNetwork("Security", NetworkPurpose.Security,
            vlanId: 42, firewallZoneId: "internal-zone");
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block All in Custom Zone",
            SourceMatchingTarget = "ANY",
            SourceZoneId = "custom-zone-001"
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeFalse();
    }

    [Fact]
    public void AppliesToSourceNetwork_MatchingZones_IpCidrSource_ReturnsTrue()
    {
        var zoneId = "custom-zone-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security,
            vlanId: 42, firewallZoneId: zoneId);
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block CIDR in Zone",
            SourceMatchingTarget = "IP",
            SourceIps = new List<string> { "192.168.42.0/24" },
            SourceZoneId = zoneId
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_MismatchedZones_IpCidrSource_ReturnsFalse()
    {
        // Even though CIDR covers the subnet, zone mismatch means rule doesn't apply
        var network = CreateNetwork("Security", NetworkPurpose.Security,
            vlanId: 42, firewallZoneId: "internal-zone");
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block CIDR in Custom Zone",
            SourceMatchingTarget = "IP",
            SourceIps = new List<string> { "192.168.42.0/24" },
            SourceZoneId = "custom-zone-001"
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeFalse();
    }

    [Fact]
    public void AppliesToSourceNetwork_RuleHasNoZone_StillMatchesBySource()
    {
        // Rules without a zone (legacy or zone-less) should still match by source
        var networkId = "security-net-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security, id: networkId,
            vlanId: 42, firewallZoneId: "internal-zone");
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block Without Zone",
            SourceMatchingTarget = "NETWORK",
            SourceNetworkIds = new List<string> { networkId },
            SourceZoneId = null
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeTrue();
    }

    [Fact]
    public void AppliesToSourceNetwork_NetworkHasNoZone_StillMatchesBySource()
    {
        // Networks without a zone ID (missing data) should still match by source
        var networkId = "security-net-001";
        var network = CreateNetwork("Security", NetworkPurpose.Security, id: networkId,
            vlanId: 42, firewallZoneId: null);
        var rule = new FirewallRule
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Block in Custom Zone",
            SourceMatchingTarget = "NETWORK",
            SourceNetworkIds = new List<string> { networkId },
            SourceZoneId = "custom-zone-001"
        };

        var result = rule.AppliesToSourceNetwork(network);

        result.Should().BeTrue();
    }

    #endregion

    #region CheckInterVlanIsolation Zone Tests

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleZonesMatchNetworks_NoIssue()
    {
        // Block rule with matching source/dest zones should satisfy isolation
        var zoneId = "internal-zone-001";
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net",
                networkIsolationEnabled: false, firewallZoneId: zoneId),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net",
                firewallZoneId: zoneId)
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Block IoT to Corp",
                Enabled = true,
                Action = "DROP",
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                SourceZoneId = zoneId,
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net" },
                DestinationZoneId = zoneId
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().BeEmpty();
    }

    [Fact]
    public void CheckInterVlanIsolation_BlockRuleSourceZoneMismatch_ReturnsIssue()
    {
        // Block rule with wrong source zone should NOT satisfy isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT", NetworkPurpose.IoT, id: "iot-net",
                networkIsolationEnabled: false, firewallZoneId: "internal-zone"),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net",
                firewallZoneId: "internal-zone")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = Guid.NewGuid().ToString(),
                Name = "Block IoT to Corp (wrong zone)",
                Enabled = true,
                Action = "DROP",
                Protocol = "all",
                SourceMatchingTarget = "NETWORK",
                SourceNetworkIds = new List<string> { "iot-net" },
                SourceZoneId = "custom-zone-other",
                DestinationMatchingTarget = "NETWORK",
                DestinationNetworkIds = new List<string> { "corp-net" },
                DestinationZoneId = "internal-zone"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().ContainSingle();
        issues.First().Type.Should().Be("MISSING_ISOLATION");
    }

    [Fact]
    public void CheckInterVlanIsolation_CorporateAndHome_DifferentZones_PredefinedBlockAll_NoIssue()
    {
        // Corporate and Home in different zones - predefined "Block All Traffic" inter-zone rule satisfies isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Work Network", NetworkPurpose.Corporate, id: "corp-net-id", firewallZoneId: "zone-corporate"),
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", firewallZoneId: "zone-home")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "predefined-block-all",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-corporate",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-home"
            },
            new FirewallRule
            {
                Id = "predefined-block-all-reverse",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30001,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-home",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-corporate"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-CORP-HOME");
        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-HOME-CORP");
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTAndCorporate_DifferentZones_PredefinedBlockAll_NoIssue()
    {
        // IoT and Corporate in different zones - predefined inter-zone block rule satisfies isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id",
                networkIsolationEnabled: false, firewallZoneId: "zone-iot"),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", firewallZoneId: "zone-internal")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "predefined-block-iot-to-internal",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-iot",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-internal"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-IOT" &&
            i.Message.Contains("IoT") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_GuestAndCorporate_DifferentZones_PredefinedBlockAll_NoIssue()
    {
        // Guest and Corporate in different zones - predefined inter-zone block satisfies isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Guest WiFi", NetworkPurpose.Guest, id: "guest-net-id",
                networkIsolationEnabled: false, firewallZoneId: "zone-guest"),
            CreateNetwork("Corporate", NetworkPurpose.Corporate, id: "corp-net-id", firewallZoneId: "zone-internal")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "predefined-block-guest-to-internal",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-guest",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-internal"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.RuleId == "FW-ISOLATION-GUEST" &&
            i.Message.Contains("Guest") && i.Message.Contains("Corporate"));
    }

    [Fact]
    public void CheckInterVlanIsolation_HomeAndManagement_DifferentZones_PredefinedBlockAll_NoIssue()
    {
        // Home and Management in different zones - predefined inter-zone block satisfies isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("Home Network", NetworkPurpose.Home, id: "home-net-id", firewallZoneId: "zone-home"),
            CreateNetwork("Management", NetworkPurpose.Management, id: "mgmt-net-id",
                networkIsolationEnabled: false, firewallZoneId: "zone-mgmt")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "predefined-block-home-to-mgmt",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-home",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-mgmt"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("Home") && i.Message.Contains("Management"));
    }

    [Fact]
    public void CheckInterVlanIsolation_IoTAndSecurity_DifferentZones_PredefinedBlockAll_NoIssue()
    {
        // IoT and Security in different zones - predefined inter-zone block satisfies isolation
        var networks = new List<NetworkInfo>
        {
            CreateNetwork("IoT Devices", NetworkPurpose.IoT, id: "iot-net-id",
                networkIsolationEnabled: false, firewallZoneId: "zone-iot"),
            CreateNetwork("Cameras", NetworkPurpose.Security, id: "sec-net-id", firewallZoneId: "zone-security")
        };
        var rules = new List<FirewallRule>
        {
            new FirewallRule
            {
                Id = "predefined-block-iot-to-security",
                Name = "Block All Traffic",
                Action = "DROP",
                Enabled = true,
                Predefined = true,
                Protocol = "all",
                Index = 30000,
                SourceMatchingTarget = "ANY",
                SourceZoneId = "zone-iot",
                DestinationMatchingTarget = "ANY",
                DestinationZoneId = "zone-security"
            }
        };

        var issues = _analyzer.CheckInterVlanIsolation(rules, networks);

        issues.Should().NotContain(i => i.Type == "MISSING_ISOLATION" &&
            i.Message.Contains("IoT") && i.Message.Contains("Cameras"));
    }

    #endregion

    #region Helper Methods

    private static NetworkInfo CreateNetwork(
        string name,
        NetworkPurpose purpose,
        string? id = null,
        int vlanId = 99,
        bool networkIsolationEnabled = false,
        bool internetAccessEnabled = true,
        string? firewallZoneId = null)
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
            InternetAccessEnabled = internetAccessEnabled,
            FirewallZoneId = firewallZoneId
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
            SourceMatchingTarget = sourceNetworkIds?.Any() == true ? "NETWORK" : null,
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
