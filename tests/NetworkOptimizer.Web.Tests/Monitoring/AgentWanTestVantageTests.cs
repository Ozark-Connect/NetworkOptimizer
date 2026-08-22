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

    // ---- The default path --------------------------------------------------

    [Fact]
    public void TheDefaultPathRunsOnTheCollector()
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
    public void AGatewayCollectorHandsTheDefaultPathToAnAgentThatCanRunTheTest()
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
        // Contexts exist for monitoring, but nothing was chosen: still the default path on the
        // one agent, not a refusal because a context happens to name a different box.
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

    // ---- The selector reads like every other WAN selector ------------------

    [Fact]
    public void WansAreOrderedByWanIndexTheWayLatencyTargetsOrdersThem()
    {
        // WAN1, WAN2, WAN3 - not alphabetical. Picking a WAN should look the same wherever it is
        // picked, and Latency Targets got there first.
        var contexts = new[]
        {
            Context(1, "Zephyr Cable", SecondaryWanAgent, wan: "wan2"),
            Context(2, "Alpha Fiber", SpeedTestAgent, wan: "wan"),
            Context(3, "Backup LTE", GatewayAgent, wan: "wan3"),
        };

        AgentWanTestVantageResolver.OrderForDisplay(contexts).Select(c => c.Name)
            .Should().Equal("Alpha Fiber", "Zephyr Cable", "Backup LTE");
    }

    [Fact]
    public void AContextWithNoWanKeySortsLastByName()
    {
        // Rows predating the WanInterface column have no index to sort on.
        var contexts = new[]
        {
            Context(1, "Unkeyed B", SpeedTestAgent, wan: null),
            Context(2, "Unkeyed A", SpeedTestAgent, wan: null),
            Context(3, "Fiber", SpeedTestAgent, wan: "wan"),
        };

        AgentWanTestVantageResolver.OrderForDisplay(contexts).Select(c => c.Name)
            .Should().Equal("Fiber", "Unkeyed A", "Unkeyed B");
    }

    // ---- What the selector offers ------------------------------------------

    [Fact]
    public void ContextsServedByAGatewayAgentAreNotOffered()
    {
        // The main-site shape: every WAN context bound to the agent on the gateway. None of them
        // can run this test and none ever will, so the list collapses to the default path alone -
        // which the callers render as no selector, because one possibility is not a choice.
        var contexts = new[]
        {
            Context(1, "Fiber", GatewayAgent, wan: "wan"),
            Context(2, "Backup LTE", GatewayAgent, wan: "wan2"),
        };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SpeedTestAgent })
            .Should().BeEmpty();
    }

    [Fact]
    public void AnInterfaceBoundContextIsNotOffered()
    {
        // The agent binds each PROBE to eth8; an unbound speed test from it leaves by whatever its
        // own route prefers. Offering it would measure one WAN and name it another.
        var contexts = new[]
        {
            new WanContext { Id = 1, Name = "Backup LTE", AgentId = SpeedTestAgent, WanInterface = "wan2", InterfaceName = "eth8" },
        };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SpeedTestAgent })
            .Should().BeEmpty();
    }

    [Fact]
    public void ARouteSteeredContextIsOffered()
    {
        // No InterfaceName: the gateway sends everything this box emits out that WAN, so the test
        // measures the WAN the context names.
        var contexts = new[]
        {
            new WanContext { Id = 1, Name = "Starlink", AgentId = SpeedTestAgent, WanInterface = "wan2" },
        };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SpeedTestAgent })
            .Select(c => c.Name).Should().Equal("Starlink");
    }

    [Fact]
    public void PickingAnInterfaceBoundWanIsRefusedRatherThanMisattributed()
    {
        var contexts = new[]
        {
            new WanContext { Id = 1, Name = "Backup LTE", AgentId = SpeedTestAgent, WanInterface = "wan2", InterfaceName = "eth8" },
        };

        var (vantage, refusal) = AgentWanTestVantageResolver.Decide(
            wanContextId: 1,
            contexts: contexts,
            connectedAgentIds: new[] { SpeedTestAgent },
            capableAgentIds: new[] { SpeedTestAgent },
            collectorAgentId: SpeedTestAgent);

        vantage.Should().BeNull();
        refusal.Should().Contain("binds each probe");
    }

    [Fact]
    public void AContextWithNoAgentIsNotOffered()
    {
        var contexts = new[] { Context(1, "Cable", agentId: null) };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SpeedTestAgent })
            .Should().BeEmpty();
    }

    [Fact]
    public void AnOfflineAgentsWanIsStillOffered()
    {
        // Offline is temporary, so the WAN stays listed and its entry explains itself. Dropping it
        // would make a WAN disappear from the picker every time its agent blinked.
        var contexts = new[] { Context(1, "Starlink", SecondaryWanAgent) };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SecondaryWanAgent })
            .Select(c => c.Name).Should().Equal("Starlink");
    }

    [Fact]
    public void OnlyTheRunnableWansSurviveOnAMixedSite()
    {
        var contexts = new[]
        {
            Context(1, "Fiber", GatewayAgent, wan: "wan"),
            Context(2, "Starlink", SecondaryWanAgent, wan: "wan2"),
            Context(3, "Cable", agentId: null, wan: "wan3"),
        };

        AgentWanTestVantageResolver.SelectableContexts(contexts, new[] { SpeedTestAgent, SecondaryWanAgent })
            .Select(c => c.Name).Should().Equal("Starlink");
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
        refusal.Should().Contain("Cable").And.Contain("not by an Agent");
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
