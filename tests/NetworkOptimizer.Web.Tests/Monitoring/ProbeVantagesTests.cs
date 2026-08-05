using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Network Tools offers a choice of where a probe runs from only when there is a choice to make.
/// One origin - which is every single-WAN, single-agent site - leaves the page exactly as it was,
/// and an agent that runs on the gateway stays a separate entry from the gateway's own SSH
/// vantage on purpose: same box, different execution paths, and telling them apart is what
/// separates an agent-side binding problem from a network one.
/// </summary>
public class ProbeVantagesTests
{
    private static ProbeVantageAgent Agent(
        int id, string name, bool onGateway = false, params ProbeVantageBinding[] vantages)
        => new(id, name, onGateway, vantages);

    private static ProbeVantageBinding Vantage(
        int id, string name, string? wanLabel = null, string? bind = null)
        => new(id, name, wanLabel, bind);

    [Fact]
    public void ServerOnly_OffersNoPicker()
    {
        var options = ProbeVantages.ForPicker(true, "Network Optimizer server", Array.Empty<ProbeVantageAgent>());

        options.Should().BeEmpty();
    }

    [Fact]
    public void SingleAgentSiteWhereTheAgentIsTheServerVantage_OffersNoPicker()
    {
        // A secondary site with one agent: the "server" vantage already means that agent, so
        // listing it twice would be the only thing a picker added.
        var options = ProbeVantages.ForPicker(false, "On-site agent", new[] { Agent(1, "Agent1") });

        options.Should().BeEmpty();
    }

    [Fact]
    public void ServerPlusAContextAgent_OffersBoth()
    {
        var options = ProbeVantages.ForPicker(true, "Network Optimizer server", new[]
        {
            Agent(7, "Agent1", false, Vantage(4, "backup-wan", "Backup ISP WAN2", "198.51.100.7"))
        });

        options.Select(o => o.Key).Should().Equal("server", "agent:7:4");
        options[0].AgentId.Should().BeNull();
        options[1].Label.Should().Be("Agent1 - Backup ISP WAN2");
        options[1].AgentId.Should().Be(7);
        options[1].SourceBind.Should().Be("198.51.100.7");
    }

    [Fact]
    public void OnGatewayAgent_IsListedSeparatelyAndSaysSo()
    {
        // Deliberate: the gateway is also offered as its own SSH vantage elsewhere on the page,
        // and these two are never collapsed into one entry.
        var options = ProbeVantages.ForPicker(true, "Network Optimizer server", new[]
        {
            Agent(3, "Agent1", true, Vantage(9, "wan2-context", "Backup ISP WAN2", "eth8"))
        });

        options.Should().HaveCount(2);
        options[1].Label.Should().Be("Agent1 - Backup ISP WAN2 (gateway)");
        options[1].SourceBind.Should().Be("eth8");
    }

    [Fact]
    public void OnGatewayAgentWithNoContext_StillCarriesTheMarker()
    {
        var label = ProbeVantages.LabelFor(Agent(4, "Agent2", onGateway: true), null);

        label.Should().Be("Agent2 (gateway)");
    }

    [Fact]
    public void PlainAgent_IsJustItsName()
    {
        ProbeVantages.LabelFor(Agent(5, "Agent3"), null).Should().Be("Agent3");
    }

    [Fact]
    public void ContextWithNoKnownWan_LabelsTheContextAlone()
    {
        // The console can be unreachable when the list is built; the vantage still names itself.
        ProbeVantages.LabelFor(Agent(6, "Agent4"), Vantage(2, "backup-wan"))
            .Should().Be("Agent4 - backup-wan");
    }

    [Fact]
    public void AnAgentWithSeveralVantages_OffersOneEntryEach()
    {
        // Each vantage binds differently, so each is its own place to probe from. Offered as one
        // entry per agent, the picker had to choose a binding and probes left by whichever
        // vantage sorted first.
        var options = ProbeVantages.ForPicker(true, "Network Optimizer server", new[]
        {
            Agent(67, "Agent 2", true,
                Vantage(11, "Yelcot Cable (WAN4)", "Yelcot Cable WAN4", "eth1"),
                Vantage(12, "Starlink (WAN2)", "Starlink WAN2", "eth0"))
        });

        options.Select(o => o.Key).Should().Equal("server", "agent:67:12", "agent:67:11");
        options[1].Label.Should().Be("Agent 2 - Starlink WAN2 (gateway)");
        options[1].SourceBind.Should().Be("eth0");
        options[2].Label.Should().Be("Agent 2 - Yelcot Cable WAN4 (gateway)");
        options[2].SourceBind.Should().Be("eth1");
    }

    [Fact]
    public void TwoAgentsWithNoServerVantage_AreBothOffered()
    {
        var options = ProbeVantages.ForPicker(false, "On-site agent", new[]
        {
            Agent(2, "Zulu"), Agent(1, "Alpha", false, Vantage(3, "backup-wan"))
        });

        options.Select(o => o.Key).Should().Equal("agent:1:3", "agent:2");
        options.Should().NotContain(o => o.Key == ProbeVantages.ServerKey);
    }
}
