using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class AgentTunnelRegistryTests
{
    private static AgentTunnelRegistry Registry(bool tunnelEnabled) =>
        new(new AgentTunnelOptions(Enabled: tunnelEnabled, Port: 0));

    private static SiteAgent Agent(int id, DateTime? lastSeenAt) =>
        new() { Id = id, Name = "Agent", EnrolledAt = DateTime.UtcNow, LastSeenAt = lastSeenAt };

    [Fact]
    public void IsAgentLive_TunnelConnected_IsLive()
    {
        var registry = Registry(tunnelEnabled: true);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);
        registry.Register(agent.Id, "branch-office", "Agent");

        registry.IsAgentLive(agent).Should().BeTrue();
    }

    [Fact]
    public void IsAgentLive_TunnelEnabled_HeartbeatOnly_NeverTunneled_IsOffline()
    {
        // The reported bug: the gRPC tunnel path isn't reverse-proxied, so the
        // agent never tunnels but its REST heartbeat stays fresh. That must NOT
        // read as online when the server offers a tunnel.
        var registry = Registry(tunnelEnabled: true);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);

        registry.IsAgentLive(agent).Should().BeFalse();
    }

    [Fact]
    public void IsAgentLive_TunnelEnabled_WithinReconnectGraceAfterDrop_StaysLive()
    {
        // A previously-connected tunnel that just dropped is mid-reconnect, not an
        // outage - the grace keeps it live so the status doesn't flap.
        var registry = Registry(tunnelEnabled: true);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);
        var connection = registry.Register(agent.Id, "branch-office", "Agent");
        registry.Unregister(connection);

        registry.IsConnected(agent.Id).Should().BeFalse();
        registry.IsAgentLive(agent).Should().BeTrue();
    }

    [Fact]
    public void IsAgentLive_TunnelDisabled_HeartbeatFresh_IsLive()
    {
        // No tunnel listener at all (single-box / legacy deployment): REST
        // heartbeat freshness is the best signal and keeps the agent online.
        var registry = Registry(tunnelEnabled: false);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);

        registry.IsAgentLive(agent).Should().BeTrue();
    }

    [Fact]
    public void IsAgentLive_TunnelDisabled_HeartbeatStale_IsOffline()
    {
        var registry = Registry(tunnelEnabled: false);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow - AgentEnrollmentService.OnlineWindow - TimeSpan.FromMinutes(1));

        registry.IsAgentLive(agent).Should().BeFalse();
    }

    [Fact]
    public void IsReachable_HeartbeatFresh_ButBrokenTunnel_IsReachable()
    {
        // LAN speed tests hit the agent's nginx directly, so a heartbeat-only
        // agent (tunnel enabled but never connected) is still reachable - looser
        // than IsAgentLive on purpose.
        var registry = Registry(tunnelEnabled: true);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);

        registry.IsAgentLive(agent).Should().BeFalse();
        registry.IsReachable(agent).Should().BeTrue();
    }

    [Fact]
    public void ReconnectsWithin_FirstConnect_IsNotAReconnect()
    {
        var registry = Registry(tunnelEnabled: true);
        registry.Register(1, "branch-office", "Agent");

        registry.ReconnectsWithin(1, AgentTunnelRegistry.UnstableWindow).Should().Be(0);
        registry.IsUnstable(1).Should().BeFalse();
    }

    [Fact]
    public void IsUnstable_BelowThreshold_IsFalse()
    {
        var registry = Registry(tunnelEnabled: true);
        for (var i = 0; i < AgentTunnelRegistry.UnstableReconnects; i++)
            registry.Register(1, "branch-office", "Agent");

        registry.ReconnectsWithin(1, AgentTunnelRegistry.UnstableWindow).Should().Be(AgentTunnelRegistry.UnstableReconnects - 1);
        registry.IsUnstable(1).Should().BeFalse();
    }

    [Fact]
    public void IsUnstable_AtThreshold_IsTrue()
    {
        var registry = Registry(tunnelEnabled: true);
        for (var i = 0; i <= AgentTunnelRegistry.UnstableReconnects; i++)
            registry.Unregister(registry.Register(1, "branch-office", "Agent"));

        registry.IsUnstable(1).Should().BeTrue();
        registry.DescribeUnstable(1).Should().StartWith($"Reconnected {AgentTunnelRegistry.UnstableReconnects} times in the last 15 minutes.");
    }

    [Fact]
    public void Register_SameAgent_DropsTheDisplacedConnection()
    {
        // A proxy may never tell us the agent abandoned the old stream; left running, its
        // watchdog would flag it stale and take the console off the new tunnel.
        var registry = Registry(tunnelEnabled: true);
        var old = registry.Register(1, "branch-office", "Agent");
        var fresh = registry.Register(1, "branch-office", "Agent");

        old.DropToken.IsCancellationRequested.Should().BeTrue();
        fresh.DropToken.IsCancellationRequested.Should().BeFalse();
        registry.IsCurrent(old).Should().BeFalse();
        registry.IsCurrent(fresh).Should().BeTrue();
    }

    [Fact]
    public void Unregister_SupersededConnection_KeepsTheNewOne()
    {
        var registry = Registry(tunnelEnabled: true);
        var old = registry.Register(1, "branch-office", "Agent");
        var fresh = registry.Register(1, "branch-office", "Agent");

        registry.Unregister(old).Should().BeFalse();

        registry.GetForSite("branch-office").Should().ContainSingle().Which.Should().BeSameAs(fresh);
        registry.Unregister(fresh).Should().BeTrue();
        registry.IsConnected(1).Should().BeFalse();
    }

    [Fact]
    public void Register_DifferentAgents_DropsNeither()
    {
        var registry = Registry(tunnelEnabled: true);
        var first = registry.Register(1, "branch-office", "Agent");
        var second = registry.Register(2, "branch-office", "Agent 2");

        first.DropToken.IsCancellationRequested.Should().BeFalse();
        registry.IsCurrent(first).Should().BeTrue();
        registry.IsCurrent(second).Should().BeTrue();
    }

    [Fact]
    public void PickForSite_SkipsASilentAgent_AndReturnsToItOnceFresh()
    {
        var registry = Registry(tunnelEnabled: true);
        registry.Register(1, "branch-office", "Agent");
        registry.Register(2, "branch-office", "Agent 2");
        var usual = registry.PickForSite("branch-office")!;
        var other = registry.GetForSite("branch-office").Single(c => !ReferenceEquals(c, usual));

        usual.LastMessageAt = DateTime.UtcNow - AgentTunnelConnection.StaleThreshold - TimeSpan.FromSeconds(1);
        registry.PickForSite("branch-office").Should().BeSameAs(other);
        registry.HasOtherLiveForSite(usual).Should().BeTrue();

        usual.LastMessageAt = DateTime.UtcNow;
        registry.PickForSite("branch-office").Should().BeSameAs(usual);
    }

    [Fact]
    public void PickForSite_SkipsAnAgentTheCallerRejects_UntilNoneIsLeft()
    {
        // The proxy rejects an agent whose open just timed out; a live alternative takes the dial.
        var registry = Registry(tunnelEnabled: true);
        registry.Register(1, "branch-office", "Agent");
        registry.Register(2, "branch-office", "Agent 2");
        var usual = registry.PickForSite("branch-office")!;
        var other = registry.GetForSite("branch-office").Single(c => !ReferenceEquals(c, usual));

        registry.PickForSite("branch-office", c => !ReferenceEquals(c, usual)).Should().BeSameAs(other);
        // Nothing qualifies: the first live agent, so the caller still sees why it cannot dial.
        registry.PickForSite("branch-office", _ => false).Should().BeSameAs(usual);
    }

    [Fact]
    public void PickForSite_AllSilent_StillReturnsOne()
    {
        // The proxy's stale gate then refuses the dial and flips the console, as for a single agent.
        var registry = Registry(tunnelEnabled: true);
        var only = registry.Register(1, "branch-office", "Agent");
        only.LastMessageAt = DateTime.UtcNow - AgentTunnelConnection.StaleThreshold - TimeSpan.FromSeconds(1);

        registry.PickForSite("branch-office").Should().BeSameAs(only);
        registry.HasOtherLiveForSite(only).Should().BeFalse();
        registry.PickForSite("elsewhere").Should().BeNull();
    }

    [Fact]
    public void IsUnstable_CountsPerAgent()
    {
        var registry = Registry(tunnelEnabled: true);
        for (var i = 0; i <= AgentTunnelRegistry.UnstableReconnects; i++)
            registry.Register(1, "branch-office", "Agent");
        registry.Register(2, "branch-office", "Agent 2");

        registry.IsUnstable(1).Should().BeTrue();
        registry.IsUnstable(2).Should().BeFalse();
    }
}
