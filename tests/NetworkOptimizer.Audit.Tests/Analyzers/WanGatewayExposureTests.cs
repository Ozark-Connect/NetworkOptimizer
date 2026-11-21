using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>
/// Gateway management open to the internet: External -> Gateway allows, Direct Remote Connection, and what the
/// gateway's WAN input chain accepts.
/// </summary>
public class WanGatewayExposureTests
{
    private const string ExternalZone = "zone-external";
    private const string GatewayZone = "zone-gateway";
    private const string InternalZone = "zone-internal";

    #region Policies

    [Fact]
    public void StockExternalToGatewayPolicies_NoFinding()
    {
        Analyze(StockPolicies()).Should().BeEmpty();
    }

    [Fact]
    public void DirectRemoteConnectionPolicy_CriticalOn443()
    {
        var issue = Analyze([.. StockPolicies(), DirectRemoteConnection()], settingOn: true).Should().ContainSingle().Subject;

        issue.Type.Should().Be(IssueTypes.GatewayWanManagementExposed);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(25);
        issue.RuleId.Should().Be("FW-GATEWAY-WAN");
        issue.Message.Should().Be("Direct Remote Connection is on, so any host on the internet can reach the console sign-in page on TCP 443");
        issue.RecommendedAction.Should().StartWith("Turn off Direct Remote Connection");
        issue.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.DirectRemoteConnectionSource);
        issue.Metadata[WanGatewayExposureAnalyzer.PortsKey].Should().Be("443");
        issue.Metadata["from_setting_only"].Should().Be(false);
        issue.Metadata.Should().NotContainKey(IpFamilyText.MetadataKey);
    }

    [Fact]
    public void DirectRemoteConnectionPolicy_KnownByNameWhenOriginTypeIsMissing()
    {
        var policy = Rule("Allow Direct Remote Connection", "allow", 30003, protocol: "tcp", port: "443", predefined: true);

        Analyze([.. StockPolicies(), policy]).Should().ContainSingle()
            .Which.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.DirectRemoteConnectionSource);
    }

    [Fact]
    public void SettingOn_PolicyMissingFromRead_FindingFromSetting()
    {
        var issue = Analyze(StockPolicies(), settingOn: true).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Metadata!["from_setting_only"].Should().Be(true);
    }

    [Fact]
    public void SettingOn_NoZoneData_FindingFromSetting()
    {
        WanGatewayExposureAnalyzer.Analyze([], externalZoneId: null, gatewayZoneId: null, directRemoteConnectionEnabled: true)
            .Should().ContainSingle().Which.Metadata!["from_setting_only"].Should().Be(true);
    }

    [Fact]
    public void SettingOn_PolicyBlockedByUserRuleAbove_NoFinding()
    {
        var block = Rule("Block WAN to console", "block", 5, protocol: "tcp", port: "443");

        Analyze([block, .. StockPolicies(), DirectRemoteConnection()], settingOn: true).Should().BeEmpty();
    }

    [Fact]
    public void UserAllowFromAnySource_CriticalNamingTheRule()
    {
        var allow = Rule("Remote SSH", "allow", 20, protocol: "tcp", port: "22");

        var issue = Analyze([allow, .. StockPolicies()]).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(25);
        issue.Message.Should().Be("Rule 'Remote SSH' lets any host on the internet reach the gateway's management on TCP 22");
        issue.RecommendedAction.Should().StartWith("Disable rule 'Remote SSH'");
        issue.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.RuleSource);
    }

    [Fact]
    public void UserAllowAllPorts_ListsEveryManagementPort()
    {
        var allow = Rule("Open everything", "allow", 20, protocol: "all");

        Analyze([allow, .. StockPolicies()]).Should().ContainSingle()
            .Which.Metadata![WanGatewayExposureAnalyzer.PortsKey].Should().Be("22, 80, 443, 8443, 7443, 28082");
    }

    [Fact]
    public void UserAllowOnNonManagementPort_NoFinding()
    {
        var allow = Rule("Game server", "allow", 20, protocol: "tcp", port: "25565");

        Analyze([allow, .. StockPolicies()]).Should().BeEmpty();
    }

    [Fact]
    public void UserAllowFromLimitedSource_RecommendedWithTheSource()
    {
        var allow = Rule("Office SSH", "allow", 20, protocol: "tcp", port: "22", sourceTarget: "IP", sourceIps: ["203.0.113.10"]);

        var issue = Analyze([allow, .. StockPolicies()]).Should().ContainSingle().Subject;

        issue.Severity.Should().Be(AuditSeverity.Recommended);
        issue.ScoreImpact.Should().Be(7);
        issue.Message.Should().Be("Rule 'Office SSH' lets 203.0.113.10 reach the gateway's management from the internet on TCP 22");
        issue.RecommendedAction.Should().Be("If this access is intended, keep 203.0.113.10 to addresses you control. A VPN is safer for remote management.");
    }

    [Fact]
    public void UserAllowFromEveryAddressExceptSome_CountsAsAnySource()
    {
        var allow = Rule("Not these", "allow", 20, protocol: "tcp", port: "22", sourceTarget: "IP",
            sourceIps: ["198.51.100.0/24"], oppositeIps: true);

        Analyze([allow, .. StockPolicies()]).Should().ContainSingle().Which.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void LimitedSourceBlockAboveAnyAllow_DoesNotProtect()
    {
        // Blocking a few addresses leaves the rest of the internet allowed
        var block = Rule("Block a few", "block", 5, protocol: "tcp", port: "22", sourceTarget: "IP", sourceIps: ["198.51.100.7"]);
        var allow = Rule("Remote SSH", "allow", 20, protocol: "tcp", port: "22");

        Analyze([block, allow, .. StockPolicies()]).Should().ContainSingle().Which.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void AnySourceBlockAboveLimitedAllow_NoFinding()
    {
        var block = Rule("Block SSH", "block", 5, protocol: "tcp", port: "22");
        var allow = Rule("Office SSH", "allow", 20, protocol: "tcp", port: "22", sourceTarget: "IP", sourceIps: ["203.0.113.10"]);

        Analyze([block, allow, .. StockPolicies()]).Should().BeEmpty();
    }

    [Fact]
    public void IpVersionSpecificAllow_FlaggedIpv6Only()
    {
        var allow = Rule("v6 SSH", "allow", 20, protocol: "tcp", port: "22", ipVersion: "IPV6");

        var issue = Analyze([allow, .. StockPolicies()]).Should().ContainSingle().Subject;

        issue.Message.Should().EndWith("on TCP 22 over IPv6");
        issue.Metadata![IpFamilyText.MetadataKey].Should().Be(IpFamilyText.Ipv6);
    }

    [Fact]
    public void RespondOnlyAllow_NoFinding()
    {
        var allow = Rule("Return", "allow", 20, protocol: "tcp", port: "22", connectionState: "RESPOND_ONLY");

        Analyze([allow, .. StockPolicies()]).Should().BeEmpty();
    }

    [Fact]
    public void InternalToGatewayAllow_NotThisCheck()
    {
        var allow = Rule("LAN SSH", "allow", 20, protocol: "tcp", port: "22", sourceZone: InternalZone);

        Analyze([allow, .. StockPolicies()]).Should().BeEmpty();
    }

    [Fact]
    public void ParsedPolicy_CarriesOriginType()
    {
        using var doc = JsonDocument.Parse($$"""
            {
              "_id": "drc", "name": "Allow Direct Remote Connection", "action": "ALLOW", "enabled": true, "index": 30003,
              "predefined": true, "origin_type": "console_setting_direct_connection", "protocol": "tcp", "ip_version": "BOTH",
              "connection_state_type": "ALL",
              "source": { "zone_id": "{{ExternalZone}}", "matching_target": "ANY" },
              "destination": { "zone_id": "{{GatewayZone}}", "matching_target": "ANY", "port": "443", "port_matching_type": "SPECIFIC" }
            }
            """);

        var rule = new FirewallRuleParser(NullLogger<FirewallRuleParser>.Instance).ParseFirewallPolicy(doc.RootElement);

        rule!.OriginType.Should().Be("console_setting_direct_connection");
        Analyze([.. StockPolicies(), rule]).Should().ContainSingle()
            .Which.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.DirectRemoteConnectionSource);
    }

    #endregion

    #region Setting

    [Theory]
    [InlineData("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt","direct_connect_enabled":true}]}""", true)]
    [InlineData("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt","direct_connect_enabled":false}]}""", false)]
    [InlineData("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt","x_ssh_enabled":true}]}""", false)]
    [InlineData("""[{"key":"usg","direct_connect_enabled":true}]""", false)]
    public void ReadDirectRemoteConnection(string json, bool expected)
    {
        using var doc = JsonDocument.Parse(json);

        WanGatewayExposureAnalyzer.ReadDirectRemoteConnection(doc.RootElement.Clone()).Should().Be(expected);
    }

    [Fact]
    public void ReadDirectRemoteConnection_NoSettings_Off()
    {
        WanGatewayExposureAnalyzer.ReadDirectRemoteConnection(null).Should().BeFalse();
    }

    #endregion

    #region Gateway WAN input chain

    // UBIOS_WAN_LOCAL_USER on a UCG-Fiber with nothing opened, and the accept Direct Remote Connection adds.
    private const string StockWanLocal = """
        -A UBIOS_WAN_LOCAL_USER -m set --match-set UBIOS_fw_group_4 src -m conntrack --ctstate NEW -j DROP
        -A UBIOS_WAN_LOCAL_USER -p icmp -m set --match-set UBIOS_fw_group_2 src -m icmp --icmp-type 8 -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -m conntrack --ctstate INVALID -j DROP
        -A UBIOS_WAN_LOCAL_USER -p udp -m set --match-set UBIOS_wireguard_ports dst -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -j DROP
        """;

    private const string DirectRemoteConnectionAccept =
        "-A UBIOS_WAN_LOCAL_USER -p tcp -m set --match-set UBIOS_unifi_console_https_port dst -m comment --comment \"00000000004294997299\" -j ACCEPT";

    private const string Ipv6Stock = """
        -A UBIOS_WAN_LOCAL_USER -m conntrack --ctstate RELATED,ESTABLISHED -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -p ipv6-icmp -m icmp6 --icmpv6-type 135 -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -p udp -m udp --sport 547 --dport 546 -j ACCEPT
        -A UBIOS_WAN_LOCAL_USER -j DROP
        """;

    [Fact]
    public void GatewayStock_AcceptsNoManagementPort()
    {
        var (issues, accepted) = Gateway(StockWanLocal, Ipv6Stock, explained: []);

        issues.Should().BeEmpty();
        accepted.Should().BeEmpty();
    }

    [Fact]
    public void GatewayDirectRemoteConnection_ExplainedByPolicy_Folded()
    {
        var (issues, accepted) = Gateway($"{StockWanLocal}\n{DirectRemoteConnectionAccept}", Ipv6Stock, explained: ["443"]);

        issues.Should().BeEmpty();
        accepted.Should().BeEquivalentTo(["443"]);
    }

    [Fact]
    public void GatewayAcceptWithNoPolicy_OwnCritical()
    {
        var (issues, _) = Gateway($"{StockWanLocal}\n-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp --dport 22 -j ACCEPT", Ipv6Stock, explained: []);

        var issue = issues.Should().ContainSingle().Subject;
        issue.Type.Should().Be(IssueTypes.GatewayWanManagementExposed);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(25);
        issue.Message.Should().Be("The gateway accepts TCP 22 from the internet, and no firewall policy in UniFi Network accounts for it");
        issue.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.GatewaySource);
    }

    [Theory]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m multiport --dports 80,8000:9000 -j ACCEPT", "80, 8443")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp --dport 7000:7999 -j ACCEPT", "7443")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -j ACCEPT", "22, 80, 443, 8443, 7443, 28082")]
    public void GatewayAcceptPortForms(string rule, string ports)
    {
        var (issues, _) = Gateway($"{StockWanLocal}\n{rule}", "", explained: []);

        issues.Should().ContainSingle().Which.Metadata![WanGatewayExposureAnalyzer.PortsKey].Should().Be(ports);
    }

    [Theory]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p udp -m udp --dport 443 -j ACCEPT")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m conntrack --ctstate ESTABLISHED -m tcp --dport 22 -j ACCEPT")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m set --match-set UBIOS_policy_dst_port_94 dst -j ACCEPT")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp ! --dport 22 -j ACCEPT")]
    [InlineData("-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp --dport 22 -j DROP")]
    [InlineData("-A UBIOS_LAN_LOCAL_USER -p tcp -m tcp --dport 22 -j ACCEPT")]
    public void GatewayRulesThatDoNotOpenAManagementPort(string rule)
    {
        Gateway($"{StockWanLocal}\n{rule}", "", explained: []).Issues.Should().BeEmpty();
    }

    [Fact]
    public void GatewayAcceptOnIpv6Only_TaggedIpv6()
    {
        var (issues, _) = Gateway(StockWanLocal, $"{Ipv6Stock}\n-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp --dport 22 -j ACCEPT", explained: []);

        issues.Should().ContainSingle().Which.Message.Should().EndWith("accounts for it over IPv6");
    }

    #endregion

    #region Full audit

    [Fact]
    public async Task FullAudit_DirectRemoteConnection_RaisedOnceAndMarkedEnforced()
    {
        using var settings = JsonDocument.Parse("""{"meta":{"rc":"ok"},"data":[{"key":"mgmt","direct_connect_enabled":true}]}""");
        var engine = new ConfigAuditEngine(NullLogger<ConfigAuditEngine>.Instance, NullLoggerFactory.Instance);

        var result = await engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = """
                [{ "type": "udm", "model": "UCGF", "name": "Synthetic gateway", "mac": "02:00:00:00:00:10", "ip": "192.0.2.1",
                   "network_table": [{ "_id": "lan", "name": "Default", "vlan": 1, "ip_subnet": "192.0.2.1/24",
                                       "purpose": "corporate", "firewall_zone_id": "zone-internal" }] }]
                """,
            SettingsData = settings.RootElement.Clone(),
            FirewallRules = [.. StockPolicies(), DirectRemoteConnection()],
            FirewallZones =
            [
                new UniFiFirewallZone { Id = ExternalZone, Name = "External", ZoneKey = FirewallZoneKeys.External },
                new UniFiFirewallZone { Id = GatewayZone, Name = "Gateway", ZoneKey = FirewallZoneKeys.Gateway },
                new UniFiFirewallZone { Id = InternalZone, Name = "Internal", ZoneKey = FirewallZoneKeys.Internal, NetworkIds = ["lan"] }
            ],
            GatewayFirewall = new GatewayFirewallSnapshot
            {
                Iptables = $"*filter\n:UBIOS_WAN_LOCAL_USER - [0:0]\n{StockWanLocal}\n{DirectRemoteConnectionAccept}\nCOMMIT",
                Ip6tables = $"*filter\n{Ipv6Stock}\nCOMMIT"
            }
        });

        var issue = result.Issues.Where(i => i.Type == IssueTypes.GatewayWanManagementExposed).Should().ContainSingle().Subject;
        issue.Metadata![WanGatewayExposureAnalyzer.SourceKey].Should().Be(WanGatewayExposureAnalyzer.DirectRemoteConnectionSource);
        issue.Metadata[WanGatewayExposureAnalyzer.EnforcedKey].Should().Be(true);
    }

    [Fact]
    public async Task FullAudit_LegacyFirewall_LiveCheckSkipped()
    {
        // Legacy WAN_LOCAL rules are not read as policies, so a gateway accept cannot be told apart from one
        var engine = new ConfigAuditEngine(NullLogger<ConfigAuditEngine>.Instance, NullLoggerFactory.Instance);

        var result = await engine.RunAuditAsync(new AuditRequest
        {
            DeviceDataJson = """[{ "type": "udm", "model": "UCGF", "name": "Synthetic gateway", "mac": "02:00:00:00:00:10", "ip": "192.0.2.1" }]""",
            GatewayFirewall = new GatewayFirewallSnapshot
            {
                Iptables = $"*filter\n{StockWanLocal}\n-A UBIOS_WAN_LOCAL_USER -p tcp -m tcp --dport 22 -j ACCEPT\nCOMMIT"
            }
        });

        result.Issues.Should().NotContain(i => i.Type == IssueTypes.GatewayWanManagementExposed);
    }

    #endregion

    #region Helpers

    private static List<AuditIssue> Analyze(List<FirewallRule> rules, bool settingOn = false) =>
        WanGatewayExposureAnalyzer.Analyze(rules, ExternalZone, GatewayZone, settingOn);

    private static (List<AuditIssue> Issues, IReadOnlySet<string> Accepted) Gateway(string v4, string v6, string[] explained) =>
        new GatewayFirewallAnalyzer(NullLogger<GatewayFirewallAnalyzer>.Instance).AnalyzeWanManagementAccess(
            new GatewayFirewallSnapshot { Iptables = v4, Ip6tables = v6 }, explained.ToHashSet());

    private static FirewallRule DirectRemoteConnection() => new()
    {
        Id = "drc", Name = "Allow Direct Remote Connection", Action = "ALLOW", Enabled = true, Index = 30003,
        Predefined = true, OriginType = "console_setting_direct_connection", Protocol = "tcp", IpVersion = "BOTH",
        SourceZoneId = ExternalZone, DestinationZoneId = GatewayZone, SourceMatchingTarget = "ANY",
        DestinationMatchingTarget = "ANY", DestinationPort = "443", ConnectionStateType = "ALL"
    };

    /// <summary>UniFi's predefined External -> Gateway policies on a UCG-Fiber (Network 11.1.50).</summary>
    private static List<FirewallRule> StockPolicies() =>
    [
        Rule("Allow Return Traffic", "allow", 30000, protocol: "all", connectionState: "RESPOND_ONLY", predefined: true),
        Rule("Block Invalid Traffic", "block", 30001, protocol: "all", connectionState: "CUSTOM", predefined: true),
        Rule("Allow WireGuard VPNs", "allow", 30002, protocol: "udp", port: "51820", predefined: true),
        Rule("Allow Neighbor Solicitations", "allow", 30005, protocol: "icmpv6", ipVersion: "IPV6", predefined: true),
        Rule("Allow DHCPv6", "allow", 30008, protocol: "udp", port: "546", ipVersion: "IPV6", predefined: true),
        Rule("Block All Traffic", "block", int.MaxValue, protocol: "all", predefined: true)
    ];

    private static FirewallRule Rule(string name, string action, int index, string? protocol = null, string? port = null,
        string sourceTarget = "ANY", List<string>? sourceIps = null, bool oppositeIps = false, string? ipVersion = null,
        string connectionState = "ALL", string sourceZone = ExternalZone, bool predefined = false) => new()
    {
        Id = $"rule-{index}",
        Name = name,
        Action = action,
        Enabled = true,
        Index = index,
        Predefined = predefined,
        Protocol = protocol,
        DestinationPort = port,
        SourceMatchingTarget = sourceTarget,
        SourceIps = sourceIps,
        SourceMatchOppositeIps = oppositeIps,
        IpVersion = ipVersion,
        ConnectionStateType = connectionState,
        ConnectionStates = connectionState == "CUSTOM" ? ["INVALID"] : null,
        SourceZoneId = sourceZone,
        DestinationZoneId = GatewayZone,
        DestinationMatchingTarget = "ANY"
    };

    #endregion
}
