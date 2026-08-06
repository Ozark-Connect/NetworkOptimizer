using FluentAssertions;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// A policy route names a MAC, so it identifies the probing box as well as the WAN. That matters:
/// a vantage bound to the wrong box - or to none at all on an agent-collected site - has its
/// targets pushed to nobody.
/// </summary>
public class PinnedProbeContextBuilderTests
{
    private const string ServerMac = "aa:bb:cc:00:00:01";
    private const string AgentMac = "aa:bb:cc:00:00:02";

    private static UniFiTrafficRouteResponse RouteFor(string mac, string networkId) => new()
    {
        Id = "r-" + mac,
        Enabled = true,
        MatchingTarget = "INTERNET",
        NetworkId = networkId,
        TargetDevices = new List<UniFiTrafficRouteTargetDevice>
        {
            new() { ClientMac = mac, Type = "CLIENT" }
        }
    };

    private static List<UniFiClientResponse> Clients() => new()
    {
        new() { Mac = ServerMac, Ip = "192.0.2.10" },
        new() { Mac = AgentMac, Ip = "192.0.2.20" },
    };

    private static List<NetworkInfo> Networks() => new()
    {
        new() { Id = "net-wan2", Name = "Cable", Purpose = "wan", WanNetworkgroup = "WAN2", Enabled = true },
        new() { Id = "net-lan", Name = "Default", Purpose = "corporate", Enabled = true },
    };

    [Fact]
    public void Names_the_agent_whose_probes_the_route_steers()
    {
        var plan = PinnedProbeContextBuilder.Build(
            new[] { RouteFor(AgentMac, "net-wan2") }, Networks(), Clients(),
            new[]
            {
                new PinnedProbeContextBuilder.ProbeHost(7, "192.0.2.20"),
                new PinnedProbeContextBuilder.ProbeHost(null, "192.0.2.10"),
            });

        plan.Should().NotBeNull();
        plan!.AgentId.Should().Be(7);
        plan.WanInterface.Should().Be("wan2");
        plan.ContextName.Should().Be("Cable");
    }

    [Fact]
    public void Names_the_server_with_a_null_agent_when_the_route_pins_it()
    {
        var plan = PinnedProbeContextBuilder.Build(
            new[] { RouteFor(ServerMac, "net-wan2") }, Networks(), Clients(),
            new[]
            {
                new PinnedProbeContextBuilder.ProbeHost(7, "192.0.2.20"),
                new PinnedProbeContextBuilder.ProbeHost(null, "192.0.2.10"),
            });

        plan!.AgentId.Should().BeNull();
    }

    [Fact]
    public void Declines_a_route_onto_a_network_that_is_not_a_wan()
    {
        PinnedProbeContextBuilder.Build(
            new[] { RouteFor(ServerMac, "net-lan") }, Networks(), Clients(),
            new[] { new PinnedProbeContextBuilder.ProbeHost(null, "192.0.2.10") })
            .Should().BeNull();
    }

    [Fact]
    public void Declines_when_no_probing_box_is_a_known_client()
    {
        PinnedProbeContextBuilder.Build(
            new[] { RouteFor(ServerMac, "net-wan2") }, Networks(), Clients(),
            new[] { new PinnedProbeContextBuilder.ProbeHost(null, "198.51.100.99") })
            .Should().BeNull();
    }

    [Fact]
    public void Declines_when_two_devices_claim_the_same_address()
    {
        var clients = Clients();
        clients.Add(new UniFiClientResponse { Mac = "aa:bb:cc:00:00:03", Ip = "192.0.2.10" });

        PinnedProbeContextBuilder.MacForAddress(clients, "192.0.2.10").Should().BeNull();
    }

    [Fact]
    public void Ignores_a_stale_last_ip_when_matching()
    {
        var clients = new List<UniFiClientResponse>
        {
            new() { Mac = ServerMac, Ip = "192.0.2.55", LastIp = "192.0.2.10" }
        };

        PinnedProbeContextBuilder.MacForAddress(clients, "192.0.2.10").Should().BeNull();
    }
}
