using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The routing decisions behind multi-WAN contexts: which agent is pushed which targets, what
/// source each target is bound to, and whose results are written. The three deployment shapes
/// these have to hold for are:
///
/// A. Main site collecting for itself (no coverage flag), plus a context agent.
/// B. Main site covered by its primary agent, plus a context agent.
/// C. Managed site with a primary agent, plus a context agent.
///
/// Every case also gets its no-context counterpart: a site with no WAN contexts must behave
/// exactly as it did before contexts existed.
/// </summary>
public class WanContextRoutingTests
{
    private const int PrimaryAgent = 1;
    private const int ContextAgent = 2;

    // ---- Push composition -------------------------------------------------

    [Fact]
    public void Push_NoContexts_UnassignedTargetsStillGoToEveryAgent()
    {
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, ContextAgent, agentIsSteeredToWan: false, unassignedOwnerId: ContextAgent)
            .Should().BeTrue();
    }

    [Fact]
    public void Push_ContextAgent_GetsOnlyItsOwnContextTargets()
    {
        // Shapes A, B and C alike: everything this agent probes leaves by its WAN, so the site's
        // ordinary targets would be measured on the wrong path and filed under the primary.
        AgentProbeResultSink.ShouldPushTargetToAgent(true, ContextAgent, ContextAgent, agentIsSteeredToWan: true, unassignedOwnerId: ContextAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, ContextAgent, agentIsSteeredToWan: true, unassignedOwnerId: ContextAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void Push_PrimaryAgent_KeepsUnassignedTargetsAndNeverAnotherContexts()
    {
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(true, ContextAgent, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void Push_ServerBoundContextTargets_GoToNoAgent()
    {
        // A source-IP context is probed by the server itself, whose prober binds the source IP
        // the gateway policy-routes. An ordinary agent would probe the same target over its OWN
        // primary route while the result gets tagged with the secondary WAN's key - corrupting
        // that WAN's score now that the tag is read - so a context target with no assigned agent
        // reaches NO agent at all, on any shape.
        AgentProbeResultSink.ShouldPushTargetToAgent(true, null, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeFalse();
        AgentProbeResultSink.ShouldPushTargetToAgent(true, null, ContextAgent, agentIsSteeredToWan: true, unassignedOwnerId: ContextAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void Push_ContextWhoseRowIsGone_ReachesNoAgentRatherThanFanningOut()
    {
        // A stale WanContextId (row deleted out from under it) is conservative: pushed nowhere
        // until the assignment is cleaned up, never broadcast as if unassigned.
        AgentProbeResultSink.ShouldPushTargetToAgent(true, null, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeFalse();
    }

    // ---- Source binding on the wire ---------------------------------------

    [Fact]
    public void SourceIp_NoContext_IsEmptySoTheAgentKeepsItsOwnDefault()
    {
        AgentProbeResultSink.ResolveSpecSourceIp(null, ContextAgent).Should().BeEmpty();
    }

    [Fact]
    public void SourceIp_InterfaceContext_SendsTheInterfaceName()
    {
        var context = new WanContext { Id = 5, Name = "backup", AgentId = ContextAgent, InterfaceName = "eth8", WanInterface = "wan2" };

        AgentProbeResultSink.ResolveSpecSourceIp(context, ContextAgent).Should().Be("eth8");
    }

    [Fact]
    public void SourceIp_InterfaceWins_OverAStaleSourceIp()
    {
        var context = new WanContext
        {
            Id = 5,
            Name = "backup",
            AgentId = ContextAgent,
            InterfaceName = "ppp0",
            ProbeSourceIp = "192.0.2.10",
            WanInterface = "wan2"
        };

        AgentProbeResultSink.ResolveSpecSourceIp(context, ContextAgent).Should().Be("ppp0");
    }

    [Fact]
    public void SourceIp_AnotherAgentsContext_SendsNothing()
    {
        // The receiving agent is not behind that WAN, so binding it to that context's source would
        // either fail or measure the wrong path.
        var context = new WanContext { Id = 5, Name = "backup", AgentId = ContextAgent, InterfaceName = "eth8", WanInterface = "wan2" };

        AgentProbeResultSink.ResolveSpecSourceIp(context, PrimaryAgent).Should().BeEmpty();
    }

    [Fact]
    public void SourceIp_ServerBoundContext_SendsNothingToAgents()
    {
        // The source IP belongs to the server's own host and is policy-routed there; an agent
        // binding it would fail.
        var context = new WanContext { Id = 5, Name = "backup", ProbeSourceIp = "192.0.2.10", WanInterface = "wan2" };

        AgentProbeResultSink.ResolveSpecSourceIp(context, ContextAgent).Should().BeEmpty();
    }

    // ---- Result acceptance ------------------------------------------------

    [Fact]
    public void Results_ShapeC_ManagedSite_AllAccepted()
    {
        // A managed site's agent always covers it: nothing here is conditional on contexts.
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: true, contextAgentId: null, agentId: PrimaryAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: true, contextAgentId: ContextAgent, agentId: ContextAgent)
            .Should().BeTrue();
    }

    [Fact]
    public void Results_ShapeB_CoveredMainSite_AllAccepted()
    {
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: true, contextAgentId: null, agentId: PrimaryAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: true, contextAgentId: ContextAgent, agentId: ContextAgent)
            .Should().BeTrue();
    }

    [Fact]
    public void Results_ShapeA_NonCoveringMainSite_ContextResultsAccepted()
    {
        // The server cannot probe the secondary WAN, so this agent's results are the only
        // measurement of it - coverage governs the primary path, not this.
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: false, contextAgentId: ContextAgent, agentId: ContextAgent)
            .Should().BeTrue();
    }

    [Fact]
    public void Results_ShapeA_NonCoveringMainSite_NonContextResultsStillDiscarded()
    {
        // The sawtooth protection: the server is probing these targets too.
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: false, contextAgentId: null, agentId: PrimaryAgent)
            .Should().BeFalse();
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: false, contextAgentId: null, agentId: ContextAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void Results_ShapeA_AnotherAgentsContext_StillDiscarded()
    {
        AgentProbeResultSink.ShouldRecordResult(agentCoversPrimary: false, contextAgentId: PrimaryAgent, agentId: ContextAgent)
            .Should().BeFalse();
    }

    // ---- SNMP and speed-test recipients -----------------------------------

    [Fact]
    public void SiteCollectionConfig_NoContexts_EveryAgentStillGetsIt()
    {
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsSteeredToWan: false).Should().BeTrue();
    }

    [Fact]
    public void SiteCollectionConfig_ContextAgent_IsExcluded()
    {
        // Otherwise a context agent polls every device a second time on a managed or covered site.
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsSteeredToWan: true).Should().BeFalse();
    }

    // ---- Influx wan tag ---------------------------------------------------

    [Fact]
    public void WanTag_PrefersTheStableWanKey()
    {
        var context = new WanContext { Id = 1, Name = "Backup circuit", WanInterface = "wan2" };

        context.InfluxWanTag.Should().Be("wan2");
    }

    [Fact]
    public void WanTag_LegacyContextWithoutAWan_FallsBackToItsName()
    {
        var context = new WanContext { Id = 1, Name = "backup-wan" };

        context.InfluxWanTag.Should().Be("backup-wan");
    }

    private const int GatewayAgent = 77;

    // ---- A gateway agent can serve contexts AND collect for the site -------

    [Fact]
    public void GatewayAgent_ServingEveryExtraWan_StillCollectsForTheSite()
    {
        // Its contexts name an interface, so each probe binds to that WAN while the box itself
        // still routes out the primary. It is the site's collector as well - on a site whose only
        // agent is the one on the gateway, nothing else can be.
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, GatewayAgent, agentIsSteeredToWan: false, unassignedOwnerId: GatewayAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsSteeredToWan: false).Should().BeTrue();
    }

    [Fact]
    public void GatewayAgent_StillTakesEveryContextItOwnsAndNoOtherAgents()
    {
        AgentProbeResultSink.ShouldPushTargetToAgent(true, GatewayAgent, GatewayAgent, agentIsSteeredToWan: false, unassignedOwnerId: GatewayAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(true, ContextAgent, GatewayAgent, agentIsSteeredToWan: false, unassignedOwnerId: GatewayAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void SteeredProbeBox_TakesItsOwnWanAndNothingElse()
    {
        // No interface to bind, so the gateway policy-routes the whole box: a primary target
        // probed from here would leave by the secondary WAN and be recorded as the primary's.
        AgentProbeResultSink.ShouldPushTargetToAgent(false, null, ContextAgent, agentIsSteeredToWan: true, unassignedOwnerId: ContextAgent)
            .Should().BeFalse();
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsSteeredToWan: true).Should().BeFalse();
    }

    [Theory]
    [InlineData("eth8", false)]   // gateway agent: binds per probe, routes normally
    [InlineData(null, true)]      // probe box: the whole box sits behind the WAN
    [InlineData("", true)]
    public void SteeredIsDecidedByWhetherTheContextNamesAnInterface(string? interfaceName, bool expectedSteered)
    {
        var contexts = new[] { new WanContext { Id = 1, AgentId = ContextAgent, InterfaceName = interfaceName } };

        var steered = contexts.Any(c => c.AgentId == ContextAgent && string.IsNullOrEmpty(c.InterfaceName));

        steered.Should().Be(expectedSteered);
    }

    // ---- One prober per target -------------------------------------------

    [Fact]
    public void UnassignedTargets_GoToOneAgentOnly()
    {
        // Two collectors on a site: the primary-WAN targets belong to whichever one owns the
        // pool, not to both. Probing them twice produces two series for one number and doubles
        // the load on every target the site monitors.
        AgentProbeResultSink.ShouldPushTargetToAgent(
            false, null, PrimaryAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(
            false, null, GatewayAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeFalse();
    }

    [Fact]
    public void AGatewayAgentOwningTheWansStillTakesThemWhenAnotherAgentHoldsThePool()
    {
        // Losing the unassigned pool costs it nothing of its own: its contexts are still its.
        AgentProbeResultSink.ShouldPushTargetToAgent(
            true, GatewayAgent, GatewayAgent, agentIsSteeredToWan: false, unassignedOwnerId: PrimaryAgent)
            .Should().BeTrue();
    }
}
