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
        int id, string name, bool onGateway = false, string? context = null,
        string? wanLabel = null, string? bind = null)
        => new(id, name, onGateway, context, wanLabel, bind);

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
            Agent(7, "Agent1", context: "backup-wan", wanLabel: "Backup ISP WAN2", bind: "198.51.100.7")
        });

        options.Select(o => o.Key).Should().Equal("server", "agent:7");
        options[0].AgentId.Should().BeNull();
        options[1].Label.Should().Be("Agent1 (backup-wan, Backup ISP WAN2)");
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
            Agent(3, "Agent1", onGateway: true, context: "wan2-context", wanLabel: "Backup ISP WAN2", bind: "eth8")
        });

        options.Should().HaveCount(2);
        options[1].Label.Should().Be("Agent1 (wan2-context, Backup ISP WAN2, on the gateway)");
        options[1].SourceBind.Should().Be("eth8");
    }

    [Fact]
    public void OnGatewayAgentWithNoContext_StillCarriesTheMarker()
    {
        var label = ProbeVantages.LabelFor(Agent(4, "Agent2", onGateway: true));

        label.Should().Be("Agent2 (on the gateway)");
    }

    [Fact]
    public void PlainAgent_IsJustItsName()
    {
        ProbeVantages.LabelFor(Agent(5, "Agent3")).Should().Be("Agent3");
    }

    [Fact]
    public void ContextWithNoKnownWan_LabelsTheContextAlone()
    {
        // The console can be unreachable when the list is built; the context still names itself.
        ProbeVantages.LabelFor(Agent(6, "Agent4", context: "backup-wan"))
            .Should().Be("Agent4 (backup-wan)");
    }

    [Fact]
    public void TwoAgentsWithNoServerVantage_AreBothOffered()
    {
        var options = ProbeVantages.ForPicker(false, "On-site agent", new[]
        {
            Agent(2, "Zulu"), Agent(1, "Alpha", context: "backup-wan")
        });

        options.Select(o => o.Key).Should().Equal("agent:1", "agent:2");
        options.Should().NotContain(o => o.Key == ProbeVantages.ServerKey);
    }
}
