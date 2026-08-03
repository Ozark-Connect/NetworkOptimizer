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
        AgentProbeResultSink.ShouldPushTargetToAgent(null, PrimaryAgent, agentIsContextAssigned: false)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(null, ContextAgent, agentIsContextAssigned: false)
            .Should().BeTrue();
    }

    [Fact]
    public void Push_ContextAgent_GetsOnlyItsOwnContextTargets()
    {
        // Shapes A, B and C alike: everything this agent probes leaves by its WAN, so the site's
        // ordinary targets would be measured on the wrong path and filed under the primary.
        AgentProbeResultSink.ShouldPushTargetToAgent(ContextAgent, ContextAgent, agentIsContextAssigned: true)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(null, ContextAgent, agentIsContextAssigned: true)
            .Should().BeFalse();
    }

    [Fact]
    public void Push_PrimaryAgent_KeepsUnassignedTargetsAndNeverAnotherContexts()
    {
        AgentProbeResultSink.ShouldPushTargetToAgent(null, PrimaryAgent, agentIsContextAssigned: false)
            .Should().BeTrue();
        AgentProbeResultSink.ShouldPushTargetToAgent(ContextAgent, PrimaryAgent, agentIsContextAssigned: false)
            .Should().BeFalse();
    }

    [Fact]
    public void Push_ServerBoundContextTargets_GoToNoAgent()
    {
        // A source-IP context is probed by the server itself. Its targets carry a context with no
        // agent, which reads as unassigned - so they reach agents as extra vantage points exactly
        // as any other unassigned target does, and never as that context's measurement.
        AgentProbeResultSink.ShouldPushTargetToAgent(null, PrimaryAgent, agentIsContextAssigned: false)
            .Should().BeTrue();
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
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsContextAssigned: false).Should().BeTrue();
    }

    [Fact]
    public void SiteCollectionConfig_ContextAgent_IsExcluded()
    {
        // Otherwise a context agent polls every device a second time on a managed or covered site.
        AgentProbeResultSink.ShouldPushSiteCollectionConfig(agentIsContextAssigned: true).Should().BeFalse();
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
}
