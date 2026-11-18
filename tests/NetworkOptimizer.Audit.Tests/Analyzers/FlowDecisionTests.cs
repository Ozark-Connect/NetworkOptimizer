using FluentAssertions;
using NetworkOptimizer.Audit.Analyzers;
using NetworkOptimizer.Audit.Models;
using NetworkOptimizer.Core.Enums;
using Xunit;

namespace NetworkOptimizer.Audit.Tests.Analyzers;

/// <summary>Deciding one live connection: host matching that can say "cannot tell", and first-match order.</summary>
public class FlowDecisionTests
{
    private const string Host = "192.0.2.20";
    private const string HostMac = "aa:bb:cc:dd:ee:02";
    private const string Resolver = "198.51.100.9";

    private static FirewallRule Rule(string name, string action, int index, string srcTarget = "ANY", string dstTarget = "ANY",
        List<string>? srcIps = null, List<string>? srcMacs = null, List<string>? dstIps = null, string? dstPort = null,
        string protocol = "all", string states = "ALL") => new()
    {
        Id = name,
        Name = name,
        Action = action,
        Index = index,
        Enabled = true,
        Protocol = protocol,
        SourceMatchingTarget = srcTarget,
        SourceIps = srcIps,
        SourceClientMacs = srcMacs,
        DestinationMatchingTarget = dstTarget,
        DestinationIps = dstIps,
        DestinationPort = dstPort,
        ConnectionStateType = states,
    };

    private static bool? Matches(FirewallRule r, string? mac, string port = "443", string protocol = "tcp")
    {
        var s = r.MatchesSourceHost("net-lan", Host, mac);
        var d = r.MatchesDestinationHost(null, Resolver);
        if (s == false || d == false || !FirewallGroupHelper.RuleMatchesPortAndProtocol(r, port, protocol)) return false;
        return s == null || d == null ? null : true;
    }

    [Fact]
    public void HostExceptionAheadOfABlockDecides()
    {
        var rules = new[]
        {
            Rule("Block DoH", "BLOCK", 2000, dstTarget: "IP", dstIps: [Resolver], dstPort: "443"),
            Rule("[NAS] Upstream DoH", "ALLOW", 1000, srcTarget: "IP", srcIps: [Host], dstTarget: "IP", dstIps: [Resolver], dstPort: "443"),
            Rule("Allow All Traffic", "ALLOW", int.MaxValue),
        };

        var decision = FirewallRuleEvaluator.DecideFlow(rules, r => Matches(r, HostMac), IpFamily.IPv4);

        decision.Rule!.Name.Should().Be("[NAS] Upstream DoH");
        decision.Undecided.Should().BeNull();
    }

    [Fact]
    public void ClientSourceMatchesByMacAndCannotTellWithoutOne()
    {
        var rule = Rule("By client", "ALLOW", 1000, srcTarget: "CLIENT", srcMacs: ["AA-BB-CC-DD-EE-02"]);

        rule.MatchesSourceHost("net-lan", Host, HostMac).Should().BeTrue();
        rule.MatchesSourceHost("net-lan", Host, "aa:bb:cc:dd:ee:99").Should().BeFalse();
        rule.MatchesSourceHost("net-lan", Host, null).Should().BeNull();
    }

    [Fact]
    public void AnUnjudgeableRuleAheadStopsTheAnswer()
    {
        var rules = new[]
        {
            Rule("Block a website", "BLOCK", 1000, dstTarget: "WEB"),
            Rule("Allow All Traffic", "ALLOW", int.MaxValue),
        };

        var decision = FirewallRuleEvaluator.DecideFlow(rules, r => Matches(r, HostMac), IpFamily.IPv4);

        decision.Rule.Should().BeNull("a web rule may take this connection; the default would be a guess");
        decision.Undecided!.Name.Should().Be("Block a website");
    }

    [Fact]
    public void AnUnjudgeableRuleThatCannotMatchOnPortIsPassed()
    {
        var rules = new[]
        {
            Rule("Block a website on 80", "BLOCK", 1000, dstTarget: "WEB", dstPort: "80"),
            Rule("Allow All Traffic", "ALLOW", int.MaxValue),
        };

        var decision = FirewallRuleEvaluator.DecideFlow(rules, r => Matches(r, HostMac), IpFamily.IPv4);

        decision.Rule!.Name.Should().Be("Allow All Traffic");
    }

    [Fact]
    public void RespondOnlyAllowDoesNotDecideANewConnection()
    {
        var rules = new[]
        {
            Rule("Allow Return Traffic", "ALLOW", 1000, states: "RESPOND_ONLY"),
            Rule("Block All Traffic", "BLOCK", 2000),
        };

        var decision = FirewallRuleEvaluator.DecideFlow(rules, r => Matches(r, HostMac), IpFamily.IPv4);

        decision.Rule!.Name.Should().Be("Block All Traffic");
    }

    [Theory]
    [InlineData("dns.quad9.net", null, true)]          // listed domain by name
    [InlineData("dns9.quad9.net", true, true)]         // another name, but a listed domain resolves to the address
    [InlineData("api.quad9.net", null, true)]          // subdomain of a listed "*.quad9.net"
    [InlineData("example.net", false, false)]          // a known name nothing covers
    [InlineData(null, true, true)]                     // no name, address covered
    [InlineData(null, false, false)]                   // no name, every domain answered without the address
    [InlineData(null, null, null)]                     // no name, a domain not answered yet: cannot tell
    public void WebDestinationMatchesByNameOrResolvedAddress(string? hostname, bool? resolves, bool? expected)
    {
        var rule = new FirewallRule
        {
            Id = "doh", Name = "Upstream DoH", Action = "ALLOW", Index = 1000, Enabled = true,
            DestinationMatchingTarget = "WEB", WebDomains = ["dns.quad9.net", "*.quad9.net"],
        };

        rule.MatchesDestinationDomain(hostname, _ => resolves).Should().Be(expected);
    }

    [Theory]
    [InlineData("US", true)]
    [InlineData("us", true)]
    [InlineData("CN", false)]
    [InlineData(null, null)]   // no country known for the address: cannot tell
    public void RegionDestinationMatchesByCountry(string? country, bool? expected)
    {
        var rule = new FirewallRule
        {
            Id = "r", Name = "US only", Action = "ALLOW", Index = 1000, Enabled = true,
            DestinationMatchingTarget = "REGION", DestinationRegions = ["US"],
        };

        rule.MatchesDestinationRegion(country).Should().Be(expected);
    }

    [Fact]
    public void RegionRuleWithNoCountriesCannotBeJudged()
    {
        var rule = new FirewallRule { Id = "r", Name = "r", Action = "ALLOW", Enabled = true, DestinationMatchingTarget = "REGION" };

        rule.MatchesDestinationRegion("US").Should().BeNull();
    }

    [Fact]
    public void ParserReadsDestinationRegions()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"_id":"p1","name":"US only","action":"ALLOW","enabled":true,"index":10010,"protocol":"tcp",
             "source":{"matching_target":"ANY","zone_id":"z1"},
             "destination":{"matching_target":"REGION","port":"443","port_matching_type":"SPECIFIC","regions":["US"],"zone_id":"z2"}}
            """);

        var rule = new FirewallRuleParser(Microsoft.Extensions.Logging.Abstractions.NullLogger<FirewallRuleParser>.Instance)
            .ParseFirewallPolicy(doc.RootElement);

        rule!.DestinationRegions.Should().Equal("US");
    }

    [Fact]
    public void UnresolvedDomainGroupCannotBeJudged()
    {
        var rule = new FirewallRule { Id = "g", Name = "g", Action = "ALLOW", Enabled = true, DestinationMatchingTarget = "WEB", HasUnresolvedDestinationDomainGroup = true };

        rule.MatchesDestinationDomain("dns.quad9.net", _ => true).Should().BeNull();
    }

    [Fact]
    public void NoRuleMatchesNoAnswer()
    {
        var rules = new[] { Rule("Other host", "ALLOW", 1000, srcTarget: "IP", srcIps: ["192.0.2.99"]) };

        var decision = FirewallRuleEvaluator.DecideFlow(rules, r => Matches(r, HostMac), IpFamily.IPv4);

        decision.Should().Be(new FirewallRuleEvaluator.FlowDecision(null, null));
    }
}
