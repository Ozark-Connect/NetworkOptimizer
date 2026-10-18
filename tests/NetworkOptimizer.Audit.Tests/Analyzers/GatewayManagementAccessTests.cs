using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Audit.Services;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>
/// Gateway management access (TCP 22/80/443 to the Gateway zone) and the Management Access network purpose.
/// </summary>
public class GatewayManagementAccessTests
{
    private const string InternalZone = "zone-internal";
    private const string GatewayZone = "zone-gateway";
    private const string ExternalZone = "zone-external";
    private const string HotspotZone = "zone-hotspot";

    private readonly FirewallRuleAnalyzer _analyzer = new(
        Mock.Of<ILogger<FirewallRuleAnalyzer>>(),
        new FirewallRuleParser(Mock.Of<ILogger<FirewallRuleParser>>()));

    #region Scope

    [Fact]
    public void NoGatewayZone_ReturnsNoIssues()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);

        var issues = _analyzer.CheckGatewayManagementAccess([StockAllowAll()], [iot], gatewayZoneId: null);

        issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData(NetworkPurpose.IoT)]
    [InlineData(NetworkPurpose.Guest)]
    [InlineData(NetworkPurpose.Security)]
    [InlineData(NetworkPurpose.Printer)]
    [InlineData(NetworkPurpose.Media)]
    [InlineData(NetworkPurpose.Dmz)]
    public void StockAllowAll_UntrustedPurpose_FlaggedCritical(NetworkPurpose purpose)
    {
        var net = Network("Untrusted", purpose, 20);

        var issues = Check([StockAllowAll()], net);

        var issue = issues.Should().ContainSingle().Subject;
        issue.Type.Should().Be(IssueTypes.GatewayManagementExposed);
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.ScoreImpact.Should().Be(12);
        issue.RuleId.Should().Be("FW-GATEWAY-MGMT");
        issue.Message.Should().Contain("TCP 22, 80, 443").And.NotContain("through rule");
        issue.Metadata!["ports"].Should().Be("22, 80, 443");
    }

    [Theory]
    [InlineData(NetworkPurpose.Home)]
    [InlineData(NetworkPurpose.Corporate)]
    [InlineData(NetworkPurpose.Gaming)]
    [InlineData(NetworkPurpose.Server)]
    [InlineData(NetworkPurpose.Management)]
    [InlineData(NetworkPurpose.Unknown)]
    public void StockAllowAll_NoAccessNetwork_TrustedPurposeNotFlagged(NetworkPurpose purpose)
    {
        var net = Network("Trusted", purpose, 10);

        Check([StockAllowAll()], net).Should().BeEmpty();
    }

    [Fact]
    public void DisabledNetwork_Skipped()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20, enabled: false);

        Check([StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void IsolatedNetwork_StillChecked()
    {
        // UniFi's "Isolated Networks" rules never target the Gateway zone
        var iot = Network("IoT", NetworkPurpose.IoT, 20, isolated: true);

        Check([StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void HotspotZone_PredefinedBlockAll_Passes()
    {
        var guest = Network("Guest", NetworkPurpose.Guest, 30, zone: HotspotZone);
        var blockAll = Rule("Block All Traffic", "block", index: int.MaxValue, sourceZone: HotspotZone, predefined: true);

        Check([StockAllowAll(), blockAll], guest).Should().BeEmpty();
    }

    [Fact]
    public void HotspotZone_FullPredefinedSet_Passes()
    {
        // Newer UniFi Network reports Portal Redirects as TCP to any port; the gateway enforces
        // it against the redirector ports only
        var guest = Network("Guest", NetworkPurpose.Guest, 30, zone: HotspotZone);
        List<FirewallRule> rules =
        [
            Rule("Allow mDNS", "allow", index: 30000, sourceZone: HotspotZone, protocol: "udp", port: "5353",
                destTarget: "IP", destIps: ["224.0.0.251", "ff02::fb"], predefined: true),
            Rule("Allow DNS", "allow", index: 30001, sourceZone: HotspotZone, protocol: "tcp_udp", port: "53", predefined: true),
            Rule("Allow ICMP", "allow", index: 30002, sourceZone: HotspotZone, protocol: "icmp", ipVersion: "IPV4", predefined: true),
            Rule("Allow DHCP", "allow", index: 30003, sourceZone: HotspotZone, protocol: "udp", port: "67",
                sourcePort: "68", ipVersion: "IPV4", predefined: true),
            Rule("Allow Hotspot Portal Authentication", "allow", index: 30004, sourceZone: HotspotZone, protocol: "all",
                port: "8880,8843", destTarget: "IP", destIps: ["192.168.30.1"], ipVersion: "IPV4", predefined: true),
            Rule("Allow Hotspot Portal Redirects", "allow", index: 30005, sourceZone: HotspotZone, protocol: "tcp",
                ipVersion: "IPV4", predefined: true),
            Rule("Allow ICMPv6", "allow", index: 30006, sourceZone: HotspotZone, protocol: "icmpv6", ipVersion: "IPV6", predefined: true),
            Rule("Allow DHCPv6", "allow", index: 30007, sourceZone: HotspotZone, protocol: "udp", port: "547",
                sourcePort: "546", ipVersion: "IPV6", predefined: true),
            Rule("Allow Return Traffic", "allow", index: 30008, sourceZone: HotspotZone, protocol: "all",
                connectionState: "RESPOND_ONLY", predefined: true),
            Rule("Block All Traffic", "block", index: 30009, sourceZone: HotspotZone, protocol: "all", predefined: true)
        ];

        Check(rules, guest).Should().BeEmpty();
    }

    [Fact]
    public void UserRuleNamedLikePortalRedirects_StillCounts()
    {
        var guest = Network("Guest", NetworkPurpose.Guest, 30, zone: HotspotZone);
        var allow = Rule("Allow Hotspot Portal Redirects", "allow", index: 1, sourceZone: HotspotZone, protocol: "tcp");
        var block = Rule("Block All Traffic", "block", index: 30009, sourceZone: HotspotZone, protocol: "all", predefined: true);

        Check([allow, block], guest).Should().ContainSingle()
            .Which.Message.Should().Contain("through rule 'Allow Hotspot Portal Redirects'");
    }

    [Fact]
    public void PredefinedTcpAnyAllowWithOtherName_StillCounts()
    {
        var guest = Network("Guest", NetworkPurpose.Guest, 30, zone: HotspotZone);
        var allow = Rule("Some Other Predefined", "allow", index: 30005, sourceZone: HotspotZone, protocol: "tcp", predefined: true);
        var block = Rule("Block All Traffic", "block", index: 30009, sourceZone: HotspotZone, protocol: "all", predefined: true);

        Check([allow, block], guest).Should().ContainSingle();
    }

    #endregion

    #region Block rules

    [Fact]
    public void NetworkBlockOnAllThreePorts_Passes()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block mgmt", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void PartialPortBlock_ReportsOnlyOpenPorts()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block SSH and HTTP", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80");

        var issue = Check([block, StockAllowAll()], iot).Should().ContainSingle().Subject;

        issue.Message.Should().Contain("TCP 443");
        issue.Metadata!["ports"].Should().Be("443");
    }

    [Fact]
    public void PortRangeAndTcpUdpBlock_Passes()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block range", "block", sourceTarget: "ANY", protocol: "tcp_udp", port: "1-1024");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void FlattenedPortGroupBlock_Passes()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("[ZT] Block Gateway Access", "block", sourceTarget: "ANY", protocol: "tcp_udp",
            port: "22,80,161,162,443,8080");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void BlockAllProtocolsNoPort_Passes()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block all to gateway", "block", networkIds: [iot.Id], protocol: "all");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void UdpOnlyBlock_DoesNotProtect()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block UDP", "block", networkIds: [iot.Id], protocol: "udp", port: "22,80,443");

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void BlockExceptMgmtPorts_DoesNotProtect()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block all but mgmt", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443",
            oppositePorts: true);

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void KeyDeviceBlock_MatchOppositeIps_Passes()
    {
        // Block everyone except the key devices: the network itself is still blocked
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Allow Select Console Management", "block", sourceTarget: "IP",
            sourceIps: ["192.168.10.5/32", "192.168.10.6/32"], oppositeIps: true, protocol: "tcp", port: "22,80,443");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void BlockToOtherZone_DoesNotProtect()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block internet", "block", networkIds: [iot.Id], protocol: "all", destZone: ExternalZone);

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void BlockToSpecificGatewayAddress_DoesNotDecide()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block one IP", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443",
            destTarget: "IP", destIps: ["192.168.20.1"]);

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void BlockWithSourcePort_DoesNotDecide()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Odd block", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443", sourcePort: "1000");

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void DisabledBlock_DoesNotProtect()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block mgmt", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443", enabled: false);

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    [Fact]
    public void BlockForOtherNetwork_DoesNotProtect()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block mgmt", "block", networkIds: ["other-net"], protocol: "tcp", port: "22,80,443");

        Check([block, StockAllowAll()], iot).Should().ContainSingle();
    }

    #endregion

    #region Allow rules

    [Fact]
    public void NetworkAllowBeforeBlock_Exposed_NamesRule()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("Let IoT manage", "allow", index: 1, networkIds: [iot.Id], protocol: "tcp", port: "443");
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        var issue = Check([allow, block, StockAllowAll()], iot).Should().ContainSingle().Subject;

        issue.Message.Should().Contain("TCP 443 through rule 'Let IoT manage'");
        issue.Metadata!["rule_name"].Should().Be("Let IoT manage");
    }

    [Fact]
    public void HostScopedAllowBeforeBlock_NotExposed()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("Hub admin", "allow", index: 1, sourceTarget: "IP", sourceIps: ["192.168.20.5/32"],
            protocol: "tcp", port: "22,80,443");
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([allow, block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void ClientScopedAllowBeforeBlock_NotExposed()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("Hub admin", "allow", index: 1, sourceTarget: "CLIENT", protocol: "tcp", port: "443");
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([allow, block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void RespondOnlyAllowBeforeBlock_NotExposed()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("Allow Return Traffic", "allow", index: 1, sourceTarget: "ANY", protocol: "all",
            connectionState: "RESPOND_ONLY");
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([allow, block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void AllowAllExceptSsh_ExposesWebPorts()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("All but SSH", "allow", index: 1, networkIds: [iot.Id], protocol: "tcp", port: "22",
            oppositePorts: true);
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        var issue = Check([allow, block, StockAllowAll()], iot).Should().ContainSingle().Subject;

        issue.Metadata!["ports"].Should().Be("80, 443");
    }

    [Fact]
    public void UdpAllowBeforeBlock_NotExposed()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("DNS to gateway", "allow", index: 1, networkIds: [iot.Id], protocol: "udp", port: "53");
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([allow, block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void AppScopedAllow_DoesNotDecide()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var allow = Rule("App allow", "allow", index: 1, networkIds: [iot.Id], protocol: "tcp", appIds: [123]);
        var block = Rule("Block mgmt", "block", index: 2, networkIds: [iot.Id], protocol: "tcp", port: "22,80,443");

        Check([allow, block, StockAllowAll()], iot).Should().BeEmpty();
    }

    #endregion

    #region IPv6

    [Fact]
    public void Ipv4OnlyBlock_OnDualStackNetwork_FlaggedOverIpv6()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20, ipv6: true);
        var block = Rule("Block mgmt", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443", ipVersion: "IPV4");

        var issue = Check([block, StockAllowAll()], iot).Should().ContainSingle().Subject;

        issue.Message.Should().EndWith(IpFamilyText.OverIpv6);
        issue.Metadata![IpFamilyText.MetadataKey].Should().Be(IpFamilyText.Ipv6);
    }

    [Fact]
    public void BlockBothFamilies_OnDualStackNetwork_Passes()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20, ipv6: true);
        var block = Rule("Block mgmt", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443", ipVersion: "BOTH");

        Check([block, StockAllowAll()], iot).Should().BeEmpty();
    }

    [Fact]
    public void ExposedOverBothFamilies_NotTaggedIpv6Only()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20, ipv6: true);

        var issue = Check([StockAllowAll()], iot).Should().ContainSingle().Subject;

        issue.Message.Should().NotEndWith(IpFamilyText.OverIpv6);
        issue.Metadata!.Should().NotContainKey(IpFamilyText.MetadataKey);
    }

    [Fact]
    public void Ipv6OnlyNetworkRule_IgnoredForIpv4OnlyNetwork()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var block = Rule("Block mgmt v6", "block", networkIds: [iot.Id], protocol: "tcp", port: "22,80,443", ipVersion: "IPV6");

        Check([block, StockAllowAll()], iot).Should().ContainSingle()
            .Which.Message.Should().NotEndWith(IpFamilyText.OverIpv6);
    }

    #endregion

    #region Management Access network (strict mode)

    [Fact]
    public void AccessNetworkSet_TrustedPurposesFlaggedRecommended()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var home = Network("Home", NetworkPurpose.Home, 10);
        var mgmt = Network("Mgmt", NetworkPurpose.Management, 99);

        var issues = Check([StockAllowAll()], access, home, mgmt);

        issues.Should().HaveCount(2);
        issues.Should().OnlyContain(i => i.Severity == AuditSeverity.Recommended && i.ScoreImpact == 7);
        issues.Select(i => i.Metadata!["source_network"]).Should().BeEquivalentTo(new[] { "Home", "Mgmt" });
    }

    [Fact]
    public void AccessNetworkSet_AccessItselfNeverFlagged()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);

        Check([StockAllowAll()], access).Should().BeEmpty();
    }

    [Fact]
    public void AccessNetworkSet_UntrustedStaysCritical()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var iot = Network("IoT", NetworkPurpose.IoT, 20);

        Check([StockAllowAll()], access, iot).Should().ContainSingle()
            .Which.Severity.Should().Be(AuditSeverity.Critical);
    }

    [Fact]
    public void AccessNetworkSet_RecommendationKeepsAccessNetwork()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var home = Network("Home", NetworkPurpose.Home, 10);

        var issue = Check([StockAllowAll()], access, home).Should().ContainSingle().Subject;

        issue.RecommendedAction.Should().Be("Block TCP 22, 80, 443 from Home to the Gateway zone. Allow access from Workstations (Management Access).");
        issue.Metadata!["management_access_networks"].Should().Be("Workstations");
    }

    [Fact]
    public void NoAccessNetwork_RecommendationHasNoAccessClause()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);

        Check([StockAllowAll()], iot).Should().ContainSingle()
            .Which.RecommendedAction.Should().Be("Block TCP 22, 80, 443 from IoT to the Gateway zone.");
    }

    [Fact]
    public void DisabledAccessNetwork_DoesNotEnableStrictMode()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5, enabled: false);
        var home = Network("Home", NetworkPurpose.Home, 10);

        Check([StockAllowAll()], access, home).Should().BeEmpty();
    }

    [Fact]
    public void AccessNetworkSet_KeyDeviceBlockOnHome_Passes()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var home = Network("Home", NetworkPurpose.Home, 10);
        var block = Rule("Key devices only", "block", sourceTarget: "IP", sourceIps: ["192.168.10.5/32"],
            oppositeIps: true, protocol: "tcp", port: "22,80,443");

        // The opposite-IP block also covers the Management Access network, which is not checked
        Check([block, StockAllowAll()], access, home).Should().BeEmpty();
    }

    #endregion

    #region Wiring

    [Fact]
    public void AnalyzeFirewallRules_WithGatewayZone_RunsCheck()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);
        var zones = new FirewallZoneLookup(
        [
            new UniFiFirewallZone { Id = InternalZone, ZoneKey = FirewallZoneKeys.Internal, Name = "Internal" },
            new UniFiFirewallZone { Id = GatewayZone, ZoneKey = FirewallZoneKeys.Gateway, Name = "Gateway" }
        ]);

        var issues = _analyzer.AnalyzeFirewallRules([StockAllowAll()], [iot], zoneLookup: zones);

        issues.Should().Contain(i => i.Type == IssueTypes.GatewayManagementExposed);
    }

    [Fact]
    public void AnalyzeFirewallRules_WithoutZoneData_SkipsCheck()
    {
        var iot = Network("IoT", NetworkPurpose.IoT, 20);

        var issues = _analyzer.AnalyzeFirewallRules([StockAllowAll()], [iot]);

        issues.Should().NotContain(i => i.Type == IssueTypes.GatewayManagementExposed);
    }

    #endregion

    #region Management Access purpose in VLAN isolation

    [Fact]
    public void Isolation_AccessToManagement_NotFlagged()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var mgmt = Network("Mgmt", NetworkPurpose.Management, 99);

        var issues = _analyzer.CheckInterVlanIsolation([], [access, mgmt]);

        issues.Should().NotContain(i => i.Metadata != null && Equals(i.Metadata["source_network"], "Workstations")
            && Equals(i.Metadata["dest_network"], "Mgmt"));
    }

    [Fact]
    public void Isolation_HomeToManagement_StillFlagged()
    {
        var home = Network("Home", NetworkPurpose.Home, 10);
        var mgmt = Network("Mgmt", NetworkPurpose.Management, 99);

        var issues = _analyzer.CheckInterVlanIsolation([], [home, mgmt]);

        issues.Should().Contain(i => i.Type == IssueTypes.MissingIsolation && i.RuleId == "FW-ISOLATION-MGMT");
    }

    [Fact]
    public void Isolation_IotToAccess_Flagged()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var iot = Network("IoT", NetworkPurpose.IoT, 20);

        var issues = _analyzer.CheckInterVlanIsolation([], [access, iot]);

        issues.Should().Contain(i => i.Type == IssueTypes.MissingIsolation && i.RuleId == "FW-ISOLATION-IOT"
            && Equals(i.Metadata!["dest_network"], "Workstations"));
    }

    [Fact]
    public void Isolation_MediaToAccess_Flagged()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var media = Network("Media", NetworkPurpose.Media, 40);

        var issues = _analyzer.CheckInterVlanIsolation([], [access, media]);

        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-MEDIA" && Equals(i.Metadata!["dest_network"], "Workstations"));
    }

    [Fact]
    public void Isolation_GuestToAccess_Critical()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5);
        var guest = Network("Guest", NetworkPurpose.Guest, 30);

        var issues = _analyzer.CheckInterVlanIsolation([], [access, guest]);

        issues.Should().Contain(i => i.RuleId == "FW-ISOLATION-GUEST" && Equals(i.Metadata!["dest_network"], "Workstations")
            && i.Severity == AuditSeverity.Critical);
    }

    [Fact]
    public void Isolation_IotAllowToAccess_ReportedAsBypassed()
    {
        var access = Network("Workstations", NetworkPurpose.ManagementAccess, 5, zone: null);
        var iot = Network("IoT", NetworkPurpose.IoT, 20, zone: null);
        var allow = new FirewallRule
        {
            Id = "allow-iot-access",
            Name = "IoT to Workstations",
            Action = "allow",
            Enabled = true,
            Index = 1,
            SourceMatchingTarget = "NETWORK",
            SourceNetworkIds = [iot.Id],
            DestinationMatchingTarget = "NETWORK",
            DestinationNetworkIds = [access.Id],
            Protocol = "all"
        };

        var issues = _analyzer.CheckInterVlanIsolation([allow], [access, iot]);

        issues.Should().Contain(i => i.Type == IssueTypes.IsolationBypassed && i.Message.Contains("IoT to Workstations"));
    }

    #endregion

    #region Helpers

    private List<AuditIssue> Check(List<FirewallRule> rules, params NetworkInfo[] networks) =>
        _analyzer.CheckGatewayManagementAccess(rules, networks.ToList(), GatewayZone);

    /// <summary>UniFi's predefined Internal -> Gateway policy.</summary>
    private static FirewallRule StockAllowAll() =>
        Rule("Allow All Traffic", "allow", index: int.MaxValue, sourceTarget: "ANY", protocol: "all", predefined: true);

    private static NetworkInfo Network(string name, NetworkPurpose purpose, int vlanId, bool enabled = true,
        bool isolated = false, bool ipv6 = false, string? zone = InternalZone) => new()
    {
        Id = $"net-{name.ToLowerInvariant()}",
        Name = name,
        VlanId = vlanId,
        Purpose = purpose,
        Subnet = $"192.168.{vlanId}.0/24",
        Gateway = $"192.168.{vlanId}.1",
        Enabled = enabled,
        NetworkIsolationEnabled = isolated,
        FirewallZoneId = zone,
        HasIpv6 = ipv6,
        Ipv6Subnets = ipv6 ? [$"2001:db8:{vlanId}::/64"] : null
    };

    private static FirewallRule Rule(
        string name,
        string action,
        int index = 100,
        List<string>? networkIds = null,
        string? sourceTarget = null,
        List<string>? sourceIps = null,
        bool oppositeIps = false,
        string? protocol = null,
        string? port = null,
        bool oppositePorts = false,
        string? sourcePort = null,
        string sourceZone = InternalZone,
        string destZone = GatewayZone,
        string destTarget = "ANY",
        List<string>? destIps = null,
        string? ipVersion = null,
        string? connectionState = null,
        List<int>? appIds = null,
        bool enabled = true,
        bool predefined = false) => new()
    {
        Id = $"rule-{name}-{index}",
        Name = name,
        Action = action,
        Enabled = enabled,
        Index = index,
        Predefined = predefined,
        SourceZoneId = sourceZone,
        DestinationZoneId = destZone,
        SourceMatchingTarget = sourceTarget ?? (networkIds != null ? "NETWORK" : "ANY"),
        SourceNetworkIds = networkIds,
        SourceIps = sourceIps,
        SourceMatchOppositeIps = oppositeIps,
        SourcePort = sourcePort,
        DestinationMatchingTarget = destTarget,
        DestinationIps = destIps,
        DestinationPort = port,
        DestinationMatchOppositePorts = oppositePorts,
        Protocol = protocol,
        IpVersion = ipVersion,
        ConnectionStateType = connectionState ?? "ALL",
        AppIds = appIds
    };

    #endregion
}
