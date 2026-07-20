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
    public void IsReachableForLanTest_HeartbeatFresh_ButBrokenTunnel_IsReachable()
    {
        // LAN speed tests hit the agent's nginx directly, so a heartbeat-only
        // agent (tunnel enabled but never connected) is still a valid target -
        // looser than IsAgentLive on purpose.
        var registry = Registry(tunnelEnabled: true);
        var agent = Agent(1, lastSeenAt: DateTime.UtcNow);

        registry.IsAgentLive(agent).Should().BeFalse();
        registry.IsReachableForLanTest(agent).Should().BeTrue();
    }
}
