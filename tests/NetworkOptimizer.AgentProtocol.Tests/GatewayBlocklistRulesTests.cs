using FluentAssertions;
using Xunit;

namespace NetworkOptimizer.AgentProtocol.Tests;

public class GatewayBlocklistRulesTests
{
    private static readonly string[] Rules = GatewayBlocklistRules.Chain("NETOPT_BLOCK", "guard", "block", "out", "NETOPT_OUT:", 30);

    private static int IndexOf(string fragment) => Array.FindIndex(Rules, r => r.Contains(fragment));

    [Fact]
    public void OutboundIsCheckedBeforeTheServerExemption()
    {
        // The server on the site's LAN must not reach a listed address.
        IndexOf("out dst -j DROP").Should().BeLessThan(IndexOf("guard src -j RETURN"));
        IndexOf("-j LOG").Should().BeLessThan(IndexOf("out dst -j DROP"));
    }

    [Fact]
    public void TheServerIsNeverExemptByDestination()
    {
        // Port forwards to the server would let blocked sources through after NAT.
        Rules.Should().NotContain(r => r.Contains("guard dst"));
    }

    [Fact]
    public void BlockedSourcesDropOnlyNewConnections_AfterTheServerExemption()
    {
        Rules[^1].Should().Be("-A NETOPT_BLOCK -m set --match-set block src -m state --state NEW -j DROP");
        IndexOf("guard src -j RETURN").Should().BeLessThan(Rules.Length - 1);
    }
}
