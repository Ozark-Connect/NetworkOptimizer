using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

public class GatewayFirewallAnalyzerTests
{
    private readonly GatewayFirewallAnalyzer _analyzer = new(new Mock<ILogger<GatewayFirewallAnalyzer>>().Object);

    private const string InternalZone = "zone-internal";
    private const string ExternalZone = "zone-external";
    private const string GatewayZone = "zone-gateway";
    private const string IotZone = "zone-iot";

    private const string BlockDnsId = "11111111-1111-4111-8111-111111111111";
    private const string IotToWanId = "22222222-2222-4222-8222-222222222222";
    private const string DisabledId = "33333333-3333-4333-8333-333333333333";

    private static readonly List<UniFiFirewallZone> Zones =
    [
        new() { Id = InternalZone, ZoneKey = "internal", Name = "Internal" },
        new() { Id = ExternalZone, ZoneKey = "external", Name = "External" },
        new() { Id = GatewayZone, ZoneKey = "gateway", Name = "Gateway" },
        new() { Id = IotZone, ZoneKey = "", Name = "IoT Zone" },
    ];

    private static readonly List<UniFiNetworkConfig> Networks =
    [
        new() { Id = "net-home", Name = "Home", Purpose = "corporate", FirewallZoneId = InternalZone },
        new() { Id = "net-iot", Name = "IoT", Purpose = "corporate", VlanEnabled = true, Vlan = 20, FirewallZoneId = IotZone },
        new() { Id = "net-wan", Name = "Internet 1", Purpose = "wan", FirewallZoneId = ExternalZone },
    ];

    private static readonly List<GatewayWanPath> Wans = [new("net-wan", "Internet 1", "eth4")];

    private static FirewallRule Policy(string id, string name, string src, string dst, bool enabled = true) =>
        new() { Id = id, Name = name, Enabled = enabled, SourceZoneId = src, DestinationZoneId = dst };

    private static readonly List<FirewallRule> Policies =
    [
        Policy(BlockDnsId, "Block DoT", InternalZone, ExternalZone),
        Policy(IotToWanId, "IoT Internet", IotZone, ExternalZone),
        Policy(DisabledId, "Old Rule", InternalZone, ExternalZone, enabled: false),
    ];

    private const string Udapi = $$$"""
        [
          {"config":{"name":"LAN_WAN"},"rules":[{"id":1,"triggerTag":{"id":"{{{BlockDnsId}}}","type":"FIREWALL_POLICY"}}]},
          {"config":{"name":"CUSTOM1_WAN"},"rules":[{"id":2,"triggerTag":{"id":"{{{IotToWanId}}}","type":"FIREWALL_POLICY"}}]},
          {"config":{"name":"LAN_LAN"},"rules":[{"id":3,"triggerTag":{"id":"aaaa","type":"PREDEF_FW_RULE"}},{"id":4}]}
        ]
        """;

    private static string Iptables(string wanZone = "WAN", string iotZone = "CUSTOM1", int inputHooks = 1, string extraRules = "") => $"""
        *filter
        :INPUT ACCEPT [0:0]
        :FORWARD ACCEPT [0:0]
        :NETOPT_BLOCK - [0:0]
        :UBIOS_FORWARD_IN_USER - [0:0]
        :UBIOS_INPUT_USER_HOOK - [0:0]
        :UBIOS_LAN_IN_USER - [0:0]
        :UBIOS_LAN_WAN_USER - [0:0]
        :UBIOS_CUSTOM1_IN_USER - [0:0]
        :UBIOS_CUSTOM1_WAN_USER - [0:0]
        {string.Concat(Enumerable.Repeat("-A INPUT -j NETOPT_BLOCK\n", inputHooks))}-A INPUT -j UBIOS_INPUT_JUMP
        -A FORWARD -j NETOPT_BLOCK
        -A NETOPT_BLOCK -s 192.0.2.10/32 -p tcp -m set --match-set netopt_out_v4 dst -m tcp --dport 443 -j RETURN
        -A NETOPT_BLOCK -m set --match-set netopt_out_v4 dst -m limit --limit 30/min --limit-burst 30 -j LOG --log-prefix "NETOPT_OUT:" --log-level 6
        -A NETOPT_BLOCK -m set --match-set netopt_out_v4 dst -j DROP
        -A NETOPT_BLOCK -m set --match-set netopt_guard_v4 src -j RETURN
        -A NETOPT_BLOCK -m set --match-set netopt_block_v4 src -m state --state NEW -j DROP
        -A UBIOS_FORWARD_IN_USER -i eth4 -m comment --comment 00000001 -j UBIOS_{wanZone}_IN_USER
        -A UBIOS_FORWARD_IN_USER -i br0 -m comment --comment 00000002 -j UBIOS_LAN_IN_USER
        -A UBIOS_FORWARD_IN_USER -i br20 -m comment --comment 00000003 -j UBIOS_{iotZone}_IN_USER
        -A UBIOS_INPUT_USER_HOOK -i eth4 -m comment --comment 00000001 -j UBIOS_{wanZone}_LOCAL_USER
        -A UBIOS_LAN_IN_USER -m set ! --match-set UBIOS_local_zoned_subnets dst -j UBIOS_LAN_WAN_USER
        -A UBIOS_CUSTOM1_IN_USER -m set ! --match-set UBIOS_local_zoned_subnets dst -j UBIOS_CUSTOM1_WAN_USER
        {extraRules}
        COMMIT
        """;

    private const string Ip6tables = """
        *filter
        :NETOPT_BLOCK - [0:0]
        -A INPUT -j NETOPT_BLOCK
        -A FORWARD -j NETOPT_BLOCK
        -A NETOPT_BLOCK -m set --match-set netopt_out_v6 dst -m limit --limit 30/min --limit-burst 30 -j LOG --log-prefix "NETOPT_OUT:" --log-level 6
        -A NETOPT_BLOCK -m set --match-set netopt_out_v6 dst -j DROP
        -A NETOPT_BLOCK -m set --match-set netopt_guard_v6 src -j RETURN
        -A NETOPT_BLOCK -m set --match-set netopt_block_v6 src -m state --state NEW -j DROP
        COMMIT
        """;

    private static string Ipsets(int blockV4 = 12) => $"""
        Name: netopt_block_v4
        Type: hash:net
        Number of entries: {blockV4}
        Name: netopt_out_v4
        Type: hash:net
        Number of entries: 0
        """;

    private static readonly GatewayBlocklistExpectation Blocklist = new()
    {
        ChainName = "NETOPT_BLOCK",
        CoreRulesV4 =
        [
            "-A NETOPT_BLOCK -m set --match-set netopt_out_v4 dst -m limit --limit 30/min --limit-burst 30 -j LOG --log-prefix NETOPT_OUT: --log-level 6",
            "-A NETOPT_BLOCK -m set --match-set netopt_out_v4 dst -j DROP",
            "-A NETOPT_BLOCK -m set --match-set netopt_guard_v4 src -j RETURN",
            "-A NETOPT_BLOCK -m set --match-set netopt_block_v4 src -m state --state NEW -j DROP",
        ],
        CoreRulesV6 =
        [
            "-A NETOPT_BLOCK -m set --match-set netopt_out_v6 dst -m limit --limit 30/min --limit-burst 30 -j LOG --log-prefix NETOPT_OUT: --log-level 6",
            "-A NETOPT_BLOCK -m set --match-set netopt_out_v6 dst -j DROP",
            "-A NETOPT_BLOCK -m set --match-set netopt_guard_v6 src -j RETURN",
            "-A NETOPT_BLOCK -m set --match-set netopt_block_v6 src -m state --state NEW -j DROP",
        ],
        SentEntries = new Dictionary<string, int> { ["netopt_block_v4"] = 12, ["netopt_out_v4"] = 0 },
    };

    private List<AuditIssue> Run(string? iptables = null, string? udapi = Udapi, string? ipsets = null,
        List<UniFiNetworkConfig>? networks = null, List<FirewallRule>? policies = null, GatewayBlocklistExpectation? blocklist = null,
        List<UniFiFirewallZone>? zones = null) =>
        _analyzer.Analyze(
            new GatewayFirewallSnapshot { Iptables = iptables ?? Iptables(), Ip6tables = Ip6tables, UdapiFilterJson = udapi, IpsetHeaders = ipsets ?? Ipsets() },
            policies ?? Policies, zones ?? Zones, networks ?? Networks, Wans, blocklist, "Gateway");

    [Fact]
    public void HealthyGateway_NoIssues()
    {
        Run(blocklist: Blocklist).Should().BeEmpty();
    }

    [Fact]
    public void WanDispatchedToAnotherZone_IsCritical()
    {
        var issues = Run(Iptables(wanZone: "LAN"));

        var issue = issues.Should().ContainSingle(i => i.Type == IssueTypes.GatewayWanNotExternal).Subject;
        issue.Severity.Should().Be(AuditSeverity.Critical);
        issue.Message.Should().Contain("Internet 1 (eth4)");
    }

    [Fact]
    public void WanMissingFromDispatch_IsCritical()
    {
        var iptables = Iptables().Replace("-i eth4", "-i eth9");
        Run(iptables).Should().Contain(i => i.Type == IssueTypes.GatewayWanNotExternal);
    }

    [Fact]
    public void WanNetworkNamingAMissingZone_IsInformational_AndSaysWhetherTheGatewayHolds()
    {
        var networks = Networks.Select(n => n.Purpose == "wan"
            ? new UniFiNetworkConfig { Id = n.Id, Name = n.Name, Purpose = "wan", FirewallZoneId = "zone-gone" } : n).ToList();

        var issue = Run(networks: networks).Should().ContainSingle(i => i.Type == IssueTypes.GatewayWanZoneReference).Subject;

        issue.Severity.Should().Be(AuditSeverity.Informational);
        issue.Message.Should().Contain("zone-gone").And.Contain("still treats Internet 1 as part of the External zone");
    }

    [Fact]
    public void WanZoneNote_IgnoresAnUnusedWanProfile_AndPointsAtTheFindingOnlyWhenOneWasRaised()
    {
        // The second WAN profile has no interface on the gateway, so the WAN zone check never ran for it.
        var networks = Networks.Where(n => n.Purpose != "wan").Concat(
        [
            new UniFiNetworkConfig { Id = "net-wan", Name = "Internet 1", Purpose = "wan", FirewallZoneId = "zone-gone" },
            new UniFiNetworkConfig { Id = "net-wan2", Name = "Spare", Purpose = "wan", FirewallZoneId = "zone-gone" },
        ]).ToList();

        Run(networks: networks).Should().ContainSingle(i => i.Type == IssueTypes.GatewayWanZoneReference)
            .Which.Message.Should().Contain("Internet 1 and Spare point at").And.Contain("still treats Internet 1 as part of the External zone")
            .And.NotContain("See the WAN Not in External Zone finding");

        Run(Iptables(wanZone: "LAN"), networks: networks).Should().ContainSingle(i => i.Type == IssueTypes.GatewayWanZoneReference)
            .Which.Message.Should().Contain("See the WAN Not in External Zone finding for Internet 1");
    }

    [Fact]
    public void NetworkDispatchedToAnotherZone_IsFlagged()
    {
        var iptables = Iptables().Replace("-i br0 -m comment --comment 00000002 -j UBIOS_LAN_IN_USER",
            "-i br0 -m comment --comment 00000002 -j UBIOS_CUSTOM1_IN_USER");

        Run(iptables).Should().ContainSingle(i => i.Type == IssueTypes.GatewayNetworkZoneMismatch)
            .Which.Message.Should().Contain("Home (UniFi Network: Internal)");
    }

    [Fact]
    public void CustomZoneToken_IsLearnedFromItsPolicies()
    {
        // IoT's policy landed in CUSTOM1_WAN, so br20 must dispatch to CUSTOM1.
        Run(Iptables(iotZone: "CUSTOM2")).Should().ContainSingle(i => i.Type == IssueTypes.GatewayNetworkZoneMismatch)
            .Which.Message.Should().Contain("IoT");
    }

    [Fact]
    public void EnabledPolicyMissingFromUdapi_IsFlagged()
    {
        var udapi = Udapi.Replace(BlockDnsId, "44444444-4444-4444-8444-444444444444");

        Run(udapi: udapi).Should().ContainSingle(i => i.Type == IssueTypes.GatewayPolicyNotEnforced)
            .Which.Message.Should().Contain("Block DoT");
    }

    [Fact]
    public void UntaggedRuleNamedForThePolicy_InItsZonePair_Counts()
    {
        // UniFi tags only some compiled rules with their policy ID; the rest carry the policy's name.
        const string untaggedId = "55555555-5555-4555-8555-555555555555";
        var policies = Policies.Append(Policy(untaggedId, " Net. Opt. Exception", InternalZone, ExternalZone)).ToList();
        var udapi = Udapi.Replace("{\"id\":1,", "{\"id\":9,\"description\":\"Net. Opt. Exception\"},{\"id\":1,");

        Run(udapi: udapi, policies: policies).Should().NotContain(i => i.Type == IssueTypes.GatewayPolicyNotEnforced);
        Run(udapi: udapi.Replace("\"LAN_WAN\"", "\"LAN_LAN2\""), policies: policies)
            .Should().ContainSingle(i => i.Type == IssueTypes.GatewayPolicyNotEnforced)
            .Which.Message.Should().Contain("Net. Opt. Exception");
    }

    [Fact]
    public void OneUntaggedRule_VouchesForOnlyOnePolicyOfThatName()
    {
        var policies = Policies
            .Append(Policy("66666666-6666-4666-8666-666666666666", "Net. Opt. Exception", InternalZone, ExternalZone))
            .Append(Policy("77777777-7777-4777-8777-777777777777", "Net. Opt. Exception", InternalZone, ExternalZone))
            .ToList();
        var udapi = Udapi.Replace("{\"id\":1,", "{\"id\":9,\"description\":\"Net. Opt. Exception\"},{\"id\":1,");

        Run(udapi: udapi, policies: policies).Should().ContainSingle(i => i.Type == IssueTypes.GatewayPolicyNotEnforced)
            .Which.Message.Should().StartWith("1 enabled firewall policy");
    }

    [Fact]
    public void PolicyInTheWrongZonePair_IsFlagged()
    {
        var udapi = Udapi.Replace("\"LAN_WAN\"", "\"LAN_LAN2\"");
        Run(udapi: udapi).Should().Contain(i => i.Type == IssueTypes.GatewayPolicyNotEnforced);
    }

    [Fact]
    public void DisabledPolicyStillOnTheGateway_IsFlagged()
    {
        var udapi = Udapi.Replace("\"id\":4}", $$$"""
            "id":4,"triggerTag":{"id":"{{{DisabledId}}}","type":"FIREWALL_POLICY"}}
            """);

        Run(udapi: udapi).Should().ContainSingle(i => i.Type == IssueTypes.GatewayDisabledPolicyEnforced)
            .Which.Message.Should().Contain("Old Rule");
    }

    [Fact]
    public void PolicyChainNothingJumpsTo_IsFlagged()
    {
        var iptables = Iptables().Replace("-A UBIOS_CUSTOM1_IN_USER -m set ! --match-set UBIOS_local_zoned_subnets dst -j UBIOS_CUSTOM1_WAN_USER", "");

        Run(iptables).Should().ContainSingle(i => i.Type == IssueTypes.GatewayZonePairUnwired)
            .Which.Message.Should().Contain("IoT Zone to External");
    }

    private const string EmptyZone = "zone-empty";
    private const string EmptyToGatewayId = "88888888-8888-4888-8888-888888888888";

    // A zone with policies to the gateway; its chain exists, but no interface dispatches to CUSTOM5.
    private static (List<UniFiFirewallZone> Zones, List<FirewallRule> Policies, string Udapi, string Iptables) EmptyZoneSetup()
    {
        var zones = Zones.Append(new UniFiFirewallZone { Id = EmptyZone, ZoneKey = "", Name = "Work" }).ToList();
        var policies = Policies.Append(Policy(EmptyToGatewayId, "Work DNS", EmptyZone, GatewayZone)).ToList();
        var udapi = Udapi.TrimEnd().TrimEnd(']') + $$$"""
            ,{"config":{"name":"CUSTOM5_LOCAL"},"rules":[{"id":5,"triggerTag":{"id":"{{{EmptyToGatewayId}}}","type":"FIREWALL_POLICY"}}]}]
            """;
        var iptables = Iptables(extraRules: ":UBIOS_CUSTOM5_LOCAL_USER - [0:0]");
        return (zones, policies, udapi, iptables);
    }

    [Fact]
    public void PolicyChainOfAZoneWithNoNetworks_IsNotFlagged()
    {
        var (zones, policies, udapi, iptables) = EmptyZoneSetup();

        Run(iptables, udapi, policies: policies, zones: zones).Should().NotContain(i => i.Type == IssueTypes.GatewayZonePairUnwired);
    }

    [Fact]
    public void PolicyChainOfAZoneWithNetworks_ButNoDispatch_IsFlagged()
    {
        var (zones, policies, udapi, iptables) = EmptyZoneSetup();
        var networks = Networks.Append(new UniFiNetworkConfig
            { Id = "net-work", Name = "Work", Purpose = "corporate", VlanEnabled = true, Vlan = 30, FirewallZoneId = EmptyZone }).ToList();

        Run(iptables, udapi, networks: networks, policies: policies, zones: zones)
            .Should().ContainSingle(i => i.Type == IssueTypes.GatewayZonePairUnwired)
            .Which.Message.Should().Contain("Work to Gateway");
    }

    [Fact]
    public void NoUdapi_SkipsPolicyChecksOnly()
    {
        Run(Iptables(wanZone: "LAN"), udapi: null).Should().OnlyContain(i => i.Type == IssueTypes.GatewayWanNotExternal);
    }

    [Fact]
    public void NoZones_SkipsZoneAndPolicyChecks()
    {
        Run(Iptables(wanZone: "LAN"), zones: []).Should().BeEmpty();
    }

    [Fact]
    public void BlocklistHookMissing_IsCritical()
    {
        Run(Iptables(inputHooks: 0), blocklist: Blocklist)
            .Should().ContainSingle(i => i.Type == IssueTypes.GatewayBlocklistNotHooked)
            .Which.Message.Should().Contain("IPv4 INPUT");
    }

    [Fact]
    public void BlocklistHookDuplicated_IsInformational()
    {
        var issue = Run(Iptables(inputHooks: 2), blocklist: Blocklist)
            .Should().ContainSingle(i => i.Type == IssueTypes.GatewayBlocklistHookDuplicated).Subject;
        issue.Severity.Should().Be(AuditSeverity.Informational);
    }

    [Fact]
    public void BlocklistChainChanged_IsFlagged_ButExceptionReturnsAreNot()
    {
        Run(blocklist: Blocklist).Should().NotContain(i => i.Type == IssueTypes.GatewayBlocklistChainDrift);

        var iptables = Iptables().Replace("-A NETOPT_BLOCK -m set --match-set netopt_block_v4 src -m state --state NEW -j DROP", "");
        Run(iptables, blocklist: Blocklist).Should().ContainSingle(i => i.Type == IssueTypes.GatewayBlocklistChainDrift)
            .Which.Message.Should().Contain("IPv4");
    }

    [Fact]
    public void BlocklistSetEmptyWhileTheServerSentEntries_IsFlagged()
    {
        Run(ipsets: Ipsets(blockV4: 0), blocklist: Blocklist)
            .Should().ContainSingle(i => i.Type == IssueTypes.GatewayBlocklistSetEmpty)
            .Which.Message.Should().Contain("your blocked addresses (IPv4)").And.NotContain("netopt_");
    }

    [Fact]
    public void NoBlocklistExpectation_SkipsBlocklistChecks()
    {
        Run(Iptables(inputHooks: 0), ipsets: Ipsets(blockV4: 0)).Should().BeEmpty();
    }

    [Fact]
    public void EmptySnapshot_NoIssues()
    {
        _analyzer.Analyze(new GatewayFirewallSnapshot(), Policies, Zones, Networks, Wans, Blocklist, "Gateway").Should().BeEmpty();
    }

    [Theory]
    [InlineData(false, null, "br0")]
    [InlineData(true, 20, "br20")]
    [InlineData(true, null, "br0")]
    public void BridgeOf_UsesTheVlan(bool vlanEnabled, int? vlan, string expected)
    {
        GatewayFirewallAnalyzer.BridgeOf(new UniFiNetworkConfig { VlanEnabled = vlanEnabled, Vlan = vlan }).Should().Be(expected);
    }

    [Fact]
    public void Dispatch_HonorsInterfaceWildcards_AndIgnoresPortForwardChains()
    {
        var dump = GatewayFirewallAnalyzer.IptablesDump.Parse("""
            -A UBIOS_FORWARD_IN_USER -i wgclt+ -m comment --comment 1 -j UBIOS_WAN_IN_USER
            -A UBIOS_FORWARD_IN_USER -i eth6.7 -m comment --comment 2 -j UBIOS_WAN_PF_IN_USER
            -A UBIOS_FORWARD_IN_USER -i eth6.7 -m comment --comment 3 -j UBIOS_WAN_IN_USER
            """);

        dump.ForwardZones("wgclt3").Should().BeEquivalentTo(["WAN"]);
        dump.ForwardZones("eth6.7").Should().BeEquivalentTo(["WAN"]);
    }
}
