using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Moq;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.UniFi.Tests.Fixtures;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

/// <summary>
/// Tracing a client through a named access point rather than the one topology reported.
///
/// A speed test can finish against an access point the console has not caught up to, and the
/// time series knows which one actually served it. The override must produce the same path the
/// analyzer would have built had topology said so itself - the access point AND everything above
/// it - and must change nothing when it is absent, unknown, or already correct.
/// </summary>
public class ForcedAccessPointPathTests
{
    private readonly NetworkPathAnalyzer _analyzer;

    public ForcedAccessPointPathTests()
    {
        var clientProvider = new Mock<IUniFiClientProvider>();
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(f => f.CreateLogger(It.IsAny<string>())).Returns(new Mock<ILogger>().Object);

        _analyzer = new NetworkPathAnalyzer(
            clientProvider.Object, new MemoryCache(new MemoryCacheOptions()), loggerFactory.Object);
    }

    private static (NetworkTopology Topology, DiscoveredClient Client, ServerPosition Server) Scenario()
    {
        var topology = NetworkTestData.CreateBasicTopology();
        var client = topology.Clients.First(c => c.Mac == NetworkTestData.ClientWirelessMac);
        return (topology, client, NetworkTestData.CreateGatewayServerPosition());
    }

    private NetworkPath Trace(
        NetworkTopology topology, DiscoveredClient client, ServerPosition server, string? forceApMac)
    {
        var path = new NetworkPath
        {
            SourceHost = server.IpAddress,
            DestinationHost = client.IpAddress,
            SourceVlanId = server.VlanId ?? 1,
            DestinationVlanId = 1,
            RequiresRouting = false
        };

        _analyzer.BuildHopList(
            path, server, null, client, topology,
            new Dictionary<string, UniFiDeviceResponse>(),
            priorSnapshot: null, wanIp: null, resolvedWanGroup: null, forceApMac: forceApMac);

        return path;
    }

    [Fact]
    public void Without_an_override_the_trace_uses_the_access_point_topology_reported()
    {
        var (topology, client, server) = Scenario();

        var path = Trace(topology, client, server, forceApMac: null);

        path.Hops.Should().Contain(h => h.DeviceMac == NetworkTestData.ApWiredMac);
        path.Hops.Should().NotContain(h => h.DeviceMac == NetworkTestData.ApMeshMac);
    }

    [Fact]
    public void An_override_re_roots_the_trace_on_the_named_access_point()
    {
        var (topology, client, server) = Scenario();

        var path = Trace(topology, client, server, forceApMac: NetworkTestData.ApMeshMac);

        var meshAt = path.Hops.FindIndex(h => h.DeviceMac == NetworkTestData.ApMeshMac);
        var wiredAt = path.Hops.FindIndex(h => h.DeviceMac == NetworkTestData.ApWiredMac);

        meshAt.Should().BeGreaterThanOrEqualTo(0, "the trace should run through the access point named");

        // The wired access point is the mesh one's uplink in this topology, so it stays on the path -
        // above the mesh access point, not in place of it. That ordering is the whole point: the hops
        // after the access point are its own uplink, which a relabel could never produce.
        wiredAt.Should().BeGreaterThan(meshAt, "the client's access point comes before its uplink");
    }

    /// <summary>
    /// The point of re-tracing rather than relabelling: the hops above the access point have to be
    /// that access point's own uplink, not the other one's.
    /// </summary>
    [Fact]
    public void An_override_produces_the_same_path_as_if_topology_had_said_so()
    {
        var (topology, client, server) = Scenario();
        var forced = Trace(topology, client, server, forceApMac: NetworkTestData.ApMeshMac);

        // The same trace, but with topology itself reporting the mesh access point.
        var (naturalTopology, naturalClient, naturalServer) = Scenario();
        naturalClient.ConnectedToDeviceMac = NetworkTestData.ApMeshMac;
        var natural = Trace(naturalTopology, naturalClient, naturalServer, forceApMac: null);

        forced.Hops.Select(h => (h.Type, h.DeviceMac))
            .Should().Equal(natural.Hops.Select(h => (h.Type, h.DeviceMac)));
    }

    [Fact]
    public void An_override_naming_the_access_point_already_traced_changes_nothing()
    {
        var (topology, client, server) = Scenario();

        var forced = Trace(topology, client, server, forceApMac: NetworkTestData.ApWiredMac);
        var natural = Trace(topology, client, server, forceApMac: null);

        forced.Hops.Select(h => (h.Type, h.DeviceMac))
            .Should().Equal(natural.Hops.Select(h => (h.Type, h.DeviceMac)));
    }

    /// <summary>An access point this topology does not know cannot re-root anything.</summary>
    [Fact]
    public void An_unknown_access_point_falls_back_to_topology()
    {
        var (topology, client, server) = Scenario();

        var forced = Trace(topology, client, server, forceApMac: "aa:bb:cc:99:99:99");
        var natural = Trace(topology, client, server, forceApMac: null);

        forced.Hops.Select(h => (h.Type, h.DeviceMac))
            .Should().Equal(natural.Hops.Select(h => (h.Type, h.DeviceMac)));
    }

    /// <summary>A wired client has no access point to override, so the override must be ignored.</summary>
    [Fact]
    public void A_wired_client_ignores_the_override()
    {
        var topology = NetworkTestData.CreateBasicTopology();
        var wired = topology.Clients.First(c => c.Mac == NetworkTestData.ClientWiredMac);
        var server = NetworkTestData.CreateGatewayServerPosition();

        var forced = Trace(topology, wired, server, forceApMac: NetworkTestData.ApMeshMac);
        var natural = Trace(topology, wired, server, forceApMac: null);

        forced.Hops.Select(h => (h.Type, h.DeviceMac))
            .Should().Equal(natural.Hops.Select(h => (h.Type, h.DeviceMac)));
        forced.Hops.Should().NotContain(h => h.DeviceMac == NetworkTestData.ApMeshMac);
    }
}
