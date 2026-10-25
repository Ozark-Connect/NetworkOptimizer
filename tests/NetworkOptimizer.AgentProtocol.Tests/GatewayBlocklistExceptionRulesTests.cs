using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

/// <summary>The blocklist chain with the public lists in their own set, and exceptions to them.</summary>
public class GatewayBlocklistExceptionRulesTests
{
    private static GatewayBlockException Exception(bool inbound, string device = "", string address = "", int port = 0, params string[] protocols)
    {
        var e = new GatewayBlockException { Inbound = inbound, Device = device, Address = address, PortFrom = port, PortTo = port };
        e.Protocols.AddRange(protocols);
        return e;
    }

    private static string[] Rules(params GatewayBlockException[] exceptions) =>
        GatewayBlocklistRules.Chain("C", "guard", "block", "out", "NETOPT_OUT:", 30, "feed", exceptions);

    [Fact]
    public void WithoutAFeedSet_TheChainIsTheOlderShape()
    {
        GatewayBlocklistRules.Chain("C", "guard", "block", "out", "P:", 30, null, [Exception(false, "192.0.2.10", "", 443, "tcp")])
            .Should().Equal(GatewayBlocklistRules.Chain("C", "guard", "block", "out", "P:", 30));
    }

    [Fact]
    public void PublicListsDropAfterTheSitesOwnBlocks_NewConnectionsOnly()
    {
        var rules = Rules();
        rules.Should().HaveCount(5);
        rules[^2].Should().Be("-A C -m set --match-set block src -m state --state NEW -j DROP");
        rules[^1].Should().Be("-A C -m set --match-set feed src -m state --state NEW -j DROP");
    }

    [Fact]
    public void OutboundException_ReturnsAheadOfTheOutboundLog_ScopedToTheOutboundSet()
    {
        var rules = Rules(Exception(false, "192.168.1.50", "203.0.113.7", 443, "tcp"));

        rules[0].Should().Be("-A C -s 192.168.1.50/32 -d 203.0.113.7/32 -p tcp -m set --match-set out dst -m tcp --dport 443 -j RETURN");
        Array.FindIndex(rules, r => r.Contains("-j LOG")).Should().Be(1);
    }

    [Fact]
    public void InboundException_ReturnsAfterTheSitesOwnBlocks_AndLetsRepliesOut()
    {
        var rules = Rules(Exception(true, "", "", 51820, "udp"));

        var own = Array.FindIndex(rules, r => r.Contains("block src"));
        var allow = Array.FindIndex(rules, r => r == "-A C -p udp -m set --match-set feed src -m udp --dport 51820 -j RETURN");
        var feeds = Array.FindIndex(rules, r => r.Contains("feed src -m state --state NEW -j DROP"));
        allow.Should().BeGreaterThan(own, "a source the site blocked itself stays blocked");
        allow.Should().BeLessThan(feeds);
        // A listed address may also be blocked outbound, which matches every state: its replies need their own way out.
        rules[0].Should().Be("-A C -p udp -m set --match-set out dst -m udp --sport 51820 -m conntrack --ctstate RELATED,ESTABLISHED -j RETURN");
    }

    [Fact]
    public void TcpAndUdp_IsOneRulePerProtocol_AndARangeUsesAColon()
    {
        var e = Exception(false, "", "198.51.100.0/24", 3478, "tcp", "udp");
        e.PortTo = 3481;

        Rules(e).Where(r => r.Contains("-j RETURN") && r.Contains("out dst")).Should().Equal(
            "-A C -d 198.51.100.0/24 -p tcp -m set --match-set out dst -m tcp --dport 3478:3481 -j RETURN",
            "-A C -d 198.51.100.0/24 -p udp -m set --match-set out dst -m udp --dport 3478:3481 -j RETURN");
    }

    [Fact]
    public void AnExceptionOfOneFamily_IsLeftOutOfTheOther()
    {
        var v6 = Exception(false, "2001:db8::5", "", 443, "tcp");

        GatewayBlocklistRules.Chain("C", "g", "b", "o", "P:", 30, "f", [v6], v6: false).Should().NotContain(r => r.Contains("2001:db8::5"));
        GatewayBlocklistRules.Chain("C", "g", "b", "o", "P:", 30, "f", [v6], v6: true)
            .Should().Contain("-A C -s 2001:db8::5/128 -p tcp -m set --match-set o dst -m tcp --dport 443 -j RETURN");
    }

    [Fact]
    public void AnExceptionNamingNoAddress_GoesToBothFamilies()
    {
        var e = Exception(true, "", "", 443, "tcp");

        GatewayBlocklistRules.Chain("C", "g", "b", "o", "P:", 30, "f", [e], v6: false).Should().Contain(r => r.Contains("--dport 443"));
        GatewayBlocklistRules.Chain("C", "g", "b", "o", "P:", 30, "f", [e], v6: true).Should().Contain(r => r.Contains("--dport 443"));
    }

    [Fact]
    public void AProtocolWithoutAPort_MatchesTheProtocolOnly()
    {
        Rules(Exception(false, "192.168.1.50", "", 0, "udp"))[0]
            .Should().Be("-A C -s 192.168.1.50/32 -p udp -m set --match-set out dst -j RETURN");
    }
}
