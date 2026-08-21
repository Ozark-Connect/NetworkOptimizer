using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Which of a site's agents runs its WAN speed test, and which WAN that measures.
/// Mirrors the collector-selection tests: the rules are a pure function, so they are exercised
/// without a tunnel, a console or a database.
/// </summary>
public class AgentWanTestVantageTests
{
    private const int SpeedTestAgent = 3;
    private const int GatewayAgent = 7;
    private const int SecondaryWanAgent = 9;

    private static WanContext Context(int id, string name, int? agentId, string? wan = "wan2") =>
        new() { Id = id, Name = name, AgentId = agentId, WanInterface = wan };

    // ---- The primary WAN ---------------------------------------------------

    [Fact]
    public void ThePrimaryWanRunsOnTheCollector()
    {
        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            capableAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            collectorAgentId: SecondaryWanAgent);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SecondaryWanAgent);
        vantage.Context.Should().BeNull();
    }

    [Fact]
    public void AGatewayCollectorHandsThePrimaryWanToAnAgentThatCanRunTheTest()
    {
        // The mixed topology: a gateway agent monitors, a bare-metal agent speed tests. The
        // gateway agent is a legitimate collector and still cannot run this.
        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { SpeedTestAgent, GatewayAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: GatewayAgent);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SpeedTestAgent);
    }

    [Fact]
    public void TheFallbackTakesTheLowestIdSoTheChoiceDoesNotMoveBetweenRuns()
    {
        var (vantage, _) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { SecondaryWanAgent, SpeedTestAgent },
            capableAgentIds: new[] { SecondaryWanAgent, SpeedTestAgent },
            collectorAgentId: null);

        vantage!.AgentId.Should().Be(SpeedTestAgent);
    }

    [Fact]
    public void ASiteWhoseOnlyAgentIsOnTheGatewayRefusesRatherThanRunning()
    {
        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { GatewayAgent },
            capableAgentIds: Array.Empty<int>(),
            collectorAgentId: GatewayAgent);

        vantage.Should().BeNull();
        refusal.Should().NotBeNullOrEmpty();
    }

    // ---- The cases that already worked, which must keep working ------------

    [Fact]
    public void ASingleAgentSiteRunsOnThatAgent()
    {
        // The only shape that exists on installs today. It ran on the site's one agent before and
        // has to keep doing exactly that, whether or not the site has WAN contexts.
        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SpeedTestAgent);
    }

    [Fact]
    public void ASingleAgentSiteRunsOnThatAgentEvenWithNoCollector()
    {
        // A site whose collector cannot be resolved (console down, agent just connected) still has
        // one obvious answer, and refusing there would take away a test that works today.
        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: Array.Empty<WanContext>(),
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: null);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SpeedTestAgent);
    }

    [Fact]
    public void ASingleAgentSiteWithWanContextsStillDefaultsToItsOneAgent()
    {
        // Contexts exist for monitoring, but nothing was chosen: still the primary WAN on the one
        // agent, not a refusal because a context happens to name a different box.
        var contexts = new[] { Context(1, "Starlink", SecondaryWanAgent) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: null,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SpeedTestAgent);
        vantage.Context.Should().BeNull();
    }

    // ---- A chosen WAN context ---------------------------------------------

    [Fact]
    public void AChosenContextRunsOnItsOwnAgent()
    {
        var contexts = new[] { Context(1, "Starlink", SecondaryWanAgent) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 1,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            capableAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            collectorAgentId: SpeedTestAgent);

        refusal.Should().BeNull();
        vantage!.AgentId.Should().Be(SecondaryWanAgent);
        vantage.Context!.Name.Should().Be("Starlink");
    }

    [Fact]
    public void AnOfflineContextAgentIsNeverSubstituted()
    {
        // Another agent is connected and capable. Running on it would measure a different WAN and
        // file the number under this one's name.
        var contexts = new[] { Context(1, "Starlink", SecondaryWanAgent) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 1,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        vantage.Should().BeNull();
        refusal.Should().Contain("Starlink").And.Contain("not connected");
    }

    [Fact]
    public void AContextOnAGatewayAgentPointsAtTheGatewayTest()
    {
        var contexts = new[] { Context(1, "Backup LTE", GatewayAgent) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 1,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent, GatewayAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        vantage.Should().BeNull();
        refusal.Should().Contain("Gateway test");
    }

    [Fact]
    public void ASourceIpContextHasNoAgentToRunOn()
    {
        // Probed by this server over a bound source address, so there is no agent at all.
        var contexts = new[] { Context(1, "Cable", agentId: null) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 1,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        vantage.Should().BeNull();
        refusal.Should().Contain("Cable").And.Contain("not by an agent");
    }

    [Fact]
    public void AScheduleWhoseWanWasDeletedRefusesInsteadOfPickingAnother()
    {
        var contexts = new[] { Context(1, "Starlink", SecondaryWanAgent) };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 42,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            capableAgentIds: new[] { SpeedTestAgent, SecondaryWanAgent },
            collectorAgentId: SpeedTestAgent);

        vantage.Should().BeNull();
        refusal.Should().Contain("no longer exists");
    }
}
