using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathMap;

public class PathMapBuilderTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // Places and networks, all made up: the access hop, a transit city, and two destination cities.
    private const string Access = "198.51.100.1";
    private const string Transit = "198.51.100.2";
    private const string TransitOtherNetwork = "198.51.100.3";

    private static readonly Dictionary<string, (string City, double Lat, double Lon, int Asn)> Places = new()
    {
        [Access] = ("Lakeview", 35.0, -97.0, 64500),
        [Transit] = ("Riverton", 36.0, -95.0, 64501),
        [TransitOtherNetwork] = ("Riverton", 36.0, -95.0, 64502),
        ["203.0.113.10"] = ("Oakdale", 38.0, -90.0, 64510),
        ["203.0.113.11"] = ("Oakdale", 38.0, -90.0, 64510),
        ["203.0.113.20"] = ("Springfield", 39.0, -89.0, 64520),
    };

    private static readonly Dictionary<string, PathMapStart> Starts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wan"] = new PathMapStart("Lakeview", "US", 35.0, -97.0, "WAN"),
    };

    private static PlacedRoute Route(params string[] addresses)
    {
        var hops = addresses.Select((a, i) => new StoredRouteHop(i + 1, a, null, 5.0 * (i + 1), 3, 3)).ToList();
        var route = new StoredRoute(Now, "server", null, addresses[^1], hops, null);
        var locations = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in addresses)
        {
            var p = Places[a];
            locations[a] = new HopLocationView("US", $"{p.City}, United States", "From the router's hostname",
                p.City, p.Lat, p.Lon, HopLocationSource.Hostname);
            asns[a] = new AsnLookup(p.Asn, $"Network {p.Asn}");
        }
        return new PlacedRoute(route, locations, asns);
    }

    private static PathTargetState State(PathTargetHealth health, double? rise = null) =>
        new(health, 20, rise is double r ? 20 + r : 20, 0, null, null);

    private static PathMapTargetInput Target(string id, string category, string address, PathTargetHealth health,
        PlacedRoute? route, double? rise = null) =>
        new(id, id, category, "wan", null, false, address, route, null, null, Now, State(health, rise), [], "/network-tools");

    private static PathMapView Build(params PathMapTargetInput[] targets) =>
        PathMapBuilder.Build(targets, Starts, [], new IspHealthOptions(), live: true, asOf: null);

    private static PathMapTargetInput Internet(string id, string destination, PathTargetHealth health, double? rise = null) =>
        Target(id, MonitoringLinks.InternetServiceCategory, destination, health, Route(Access, Transit, destination), rise);

    [Fact]
    public void Build_SharedFirstHops_MergeIntoOneTrunk()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Normal),
            Internet("b", "203.0.113.11", PathTargetHealth.Normal),
            Internet("c", "203.0.113.20", PathTargetHealth.Normal));

        map.Nodes.Count(n => n.City == "Riverton").Should().Be(1);
        var trunk = map.Legs.Single(l => map.Nodes[l.From].City == "Lakeview" && map.Nodes[l.To].City == "Riverton");
        trunk.TargetCount.Should().Be(3);
        trunk.Weight.Should().Be(2);
    }

    [Fact]
    public void Build_OneCityTwoNetworks_AreTwoPlaces()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Normal),
            Target("b", MonitoringLinks.InternetServiceCategory, "203.0.113.20", PathTargetHealth.Normal,
                Route(Access, TransitOtherNetwork, "203.0.113.20")));

        map.Nodes.Count(n => n.City == "Riverton").Should().Be(2);
    }

    [Fact]
    public void Build_SlowIspHopWithCleanTargetsBeyond_IsAbsolved()
    {
        var map = Build(
            Target("isp", MonitoringLinks.AccessIspCategory, Access, PathTargetHealth.Elevated, null, rise: 30),
            Internet("a", "203.0.113.10", PathTargetHealth.Normal));

        var isp = map.Targets.Single(t => t.TargetId == "isp");
        isp.Absolved.Should().BeTrue();
        isp.Health.Should().Be(PathTargetHealth.Normal);
        map.Problems.Should().BeEmpty();
        map.Nodes[isp.EndNode!.Value].City.Should().Be("Lakeview");
    }

    [Fact]
    public void Build_SlowIspHopCarriedToEveryTarget_IsNotAbsolved()
    {
        var map = Build(
            Target("isp", MonitoringLinks.AccessIspCategory, Access, PathTargetHealth.Degraded, null, rise: 30),
            Internet("a", "203.0.113.10", PathTargetHealth.Degraded, rise: 30));

        map.Targets.Single(t => t.TargetId == "isp").Absolved.Should().BeFalse();
        map.Problems.Should().Contain("isp");
    }

    [Fact]
    public void Build_TwoSlowTargetsBeyondALeg_WithACleanSibling_GetTheHalo()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Elevated, rise: 14),
            Internet("b", "203.0.113.11", PathTargetHealth.Elevated, rise: 12),
            Internet("c", "203.0.113.20", PathTargetHealth.Normal));

        var halo = map.Legs.Single(l => l.Halo != null);
        map.Nodes[halo.From].City.Should().Be("Riverton");
        map.Nodes[halo.To].City.Should().Be("Oakdale");
        halo.Halo.Should().Be("All 2 targets beyond Riverton are slow, 1 other through Riverton is not");
    }

    [Fact]
    public void Build_EveryTargetSlow_HasNoHalo()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Elevated),
            Internet("b", "203.0.113.11", PathTargetHealth.Elevated),
            Internet("c", "203.0.113.20", PathTargetHealth.Elevated));

        map.Legs.Should().OnlyContain(l => l.Halo == null);
    }

    [Fact]
    public void Build_Problems_AreWorstFirst()
    {
        var map = Build(
            Internet("elevated-small", "203.0.113.10", PathTargetHealth.Elevated, rise: 9),
            Internet("elevated-big", "203.0.113.11", PathTargetHealth.Elevated, rise: 40),
            Internet("degraded", "203.0.113.20", PathTargetHealth.Degraded, rise: 10),
            Target("unreachable", MonitoringLinks.CustomCategory, "203.0.113.20", PathTargetHealth.Unreachable,
                Route(Access, Transit, "203.0.113.20")),
            Target("unknown", MonitoringLinks.CustomCategory, "203.0.113.20", PathTargetHealth.Unknown, null));

        map.Problems.Should().Equal("unreachable", "degraded", "elevated-big", "elevated-small");
    }

    [Fact]
    public void Build_WaitingTargets_LeaveOutRoutersDrawnFromOtherRoutesAndOnesNeverTraced()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Normal),
            // An ISP router another route crosses: drawn from that route, never traced alone.
            Target("isp", MonitoringLinks.AccessIspCategory, Access, PathTargetHealth.Normal, null),
            // A router discovery gave no row to: the loop never traces it.
            Target("orphan", MonitoringLinks.TransitCategory, "198.51.100.99", PathTargetHealth.Normal, null) with { Traced = false },
            // An Internet target not traced yet: the one actually waiting.
            Target("new", MonitoringLinks.CustomCategory, "203.0.113.20", PathTargetHealth.Normal, null));

        map.WaitingTargets.Should().Be(1);
    }

    [Fact]
    public void Build_UntracedTarget_IsCountedNotDrawn()
    {
        var map = Build(Target("new", MonitoringLinks.CustomCategory, "203.0.113.20", PathTargetHealth.Normal, null));

        map.UntracedTargets.Should().Be(1);
        map.Targets.Single().EndNode.Should().BeNull();
    }

    [Fact]
    public void Build_LocatedSegment_MarksTheLegAndTheOnset()
    {
        var map = PathMapBuilder.Build([Internet("a", "203.0.113.10", PathTargetHealth.Degraded)], Starts,
            [new PathMapLocated(Access, Transit)], new IspHealthOptions(), live: true, asOf: null);

        map.Nodes.Single(n => n.City == "Riverton").Located.Should().BeTrue();
        map.Legs.Single(l => l.Located).Should().Match<PathMapLeg>(l =>
            map.Nodes[l.From].City == "Lakeview" && map.Nodes[l.To].City == "Riverton");
    }

    [Fact]
    public void Build_ARouterTracedAlone_TakesThePlaceATraceThroughItGives()
    {
        static HopLocationView At(string city, double lat, double lon, HopLocationSource source) =>
            new("US", $"{city}, United States", source.ToString(), city, lat, lon, source);
        static PlacedRoute Placed(string[] addresses, Dictionary<string, HopLocationView> at)
        {
            var hops = addresses.Select((a, i) => new StoredRouteHop(i + 1, a, null, 5.0 * (i + 1), 3, 3)).ToList();
            var asns = addresses.ToDictionary(a => a, a => (AsnLookup?)new AsnLookup(a == Access ? 64500 : 64501, "Network"),
                StringComparer.OrdinalIgnoreCase);
            return new PlacedRoute(new StoredRoute(Now, "server", null, addresses[^1], hops, null), at, asns);
        }
        var access = At("Lakeview", 35.0, -97.0, HopLocationSource.Set);
        // Traced alone, the router has nothing near it to anchor it: GeoLite2 places it far off.
        var alone = Placed([Access, Transit], new(StringComparer.OrdinalIgnoreCase)
        {
            [Access] = access,
            [Transit] = At("Bayport", 30.4, -87.2, HopLocationSource.GeoLite2),
        });
        // A trace through it places it from a nearby hop.
        var through = Placed([Access, Transit, "203.0.113.10"], new(StringComparer.OrdinalIgnoreCase)
        {
            [Access] = access,
            [Transit] = At("Riverton", 36.0, -95.0, HopLocationSource.Neighbor),
            ["203.0.113.10"] = At("Riverton", 36.0, -95.0, HopLocationSource.Hostname),
        });

        var map = Build(
            Target("router", MonitoringLinks.TransitCategory, Transit, PathTargetHealth.Normal, alone),
            Target("beyond", MonitoringLinks.InternetServiceCategory, "203.0.113.10", PathTargetHealth.Normal, through));

        map.Nodes.Should().NotContain(n => n.City == "Bayport");
        var router = map.Targets.Single(t => t.TargetId == "router");
        map.Nodes[router.EndNode!.Value].City.Should().Be("Riverton");
    }

    // A route whose hops are given whole: null addresses are silent hops.
    private static PlacedRoute RouteOf(params StoredRouteHop[] hops)
    {
        var placed = Route(hops.Where(h => h.Address != null).Select(h => h.Address!).ToArray());
        return placed with { Route = placed.Route with { Hops = hops } };
    }

    [Fact]
    public void Build_SilentHopsBetweenTwoPlaces_MarkTheLegUnlessAnotherTraceHeardItAll()
    {
        var gap = RouteOf(
            new StoredRouteHop(1, Access, null, 3, 3, 3),
            new StoredRouteHop(2, null, null, null, 3, 0),
            new StoredRouteHop(3, null, null, null, 3, 0),
            new StoredRouteHop(4, Transit, null, 12, 3, 3),
            new StoredRouteHop(5, "203.0.113.10", null, 20, 3, 3));
        var alone = Build(Target("a", MonitoringLinks.InternetServiceCategory, "203.0.113.10", PathTargetHealth.Normal, gap));

        Leg(alone, "Lakeview", "Riverton").SilentHops.Should().Be(2);
        Leg(alone, "Riverton", "Oakdale").SilentHops.Should().Be(0);

        var both = Build(
            Target("a", MonitoringLinks.InternetServiceCategory, "203.0.113.10", PathTargetHealth.Normal, gap),
            Internet("b", "203.0.113.11", PathTargetHealth.Normal));
        Leg(both, "Lakeview", "Riverton").SilentHops.Should().Be(0);
    }

    [Fact]
    public void Build_HopFilledFromAnEarlierTrace_IsListedWithWhenAndCountsNoLoss()
    {
        var seen = Now.AddMinutes(-20);
        var route = RouteOf(
            new StoredRouteHop(1, Access, null, 3, 3, 3),
            new StoredRouteHop(2, Transit, null, 12, 3, 0, SeenAt: seen),
            new StoredRouteHop(3, "203.0.113.10", null, 20, 3, 3));

        var map = Build(Target("a", MonitoringLinks.InternetServiceCategory, "203.0.113.10", PathTargetHealth.Normal, route));

        var riverton = map.Nodes.Single(n => n.City == "Riverton");
        riverton.Hops.Single().SeenAt.Should().Be(seen);
        riverton.LossPct.Should().BeNull();
        Leg(map, "Lakeview", "Riverton").SilentHops.Should().Be(0);
    }

    // Lakeview (0), Riverton (1), Oakdale (2), Springfield (3), Bayport (4, another network).
    private static readonly (string, double, double, double?, int?)[] ViaNodes =
    [
        ("Lakeview", 35.0, -97.0, 0, 64500), ("Riverton", 36.0, -95.0, 10, 64501), ("Oakdale", 38.0, -90.0, 15, 64501),
        ("Springfield", 39.0, -89.0, 30, 64501), ("Bayport", 36.5, -94.0, 9, 64502),
    ];

    [Fact]
    public void Via_PicksThePlaceMostRoutesTakeBetweenTheEnds_InReach()
    {
        // Two routes go through Riverton to Springfield, three through Oakdale.
        ((int, int, string), int)[] legs = [((0, 1, "wan"), 2), ((1, 3, "wan"), 2), ((0, 2, "wan"), 3), ((2, 3, "wan"), 3), ((0, 3, "wan"), 1)];

        PathMapBuilder.Via(0, 3, "wan", legs, ViaNodes).Pick.Should().Be(2);
        // Too little round trip for the detour through either: nothing.
        var tight = ViaNodes.Select((n, i) => i == 3 ? n with { Item4 = 3.0 } : n).ToArray();
        PathMapBuilder.Via(0, 3, "wan", legs, tight).Pick.Should().BeNull();
        // Another WAN's routes say nothing about this one.
        PathMapBuilder.Via(0, 3, "wan2", legs, ViaNodes).Pick.Should().BeNull();
    }

    [Fact]
    public void Via_WithNoRouteThroughToTheEnd_DrawsTheLeastDetourWayIntoItsNetwork_AndNamesAll()
    {
        // Routes enter the end's network at Riverton (fewer routes, the shorter way: 825 km against 840) and
        // Oakdale, never continuing to Springfield; Bayport is another network. Drawn through Riverton, the least
        // detour, whatever the route counts; both named, the drawn one first.
        ((int, int, string), int)[] several = [((0, 1, "wan"), 1), ((0, 2, "wan"), 4), ((0, 4, "wan"), 6), ((0, 3, "wan"), 1), ((1, 4, "wan"), 1), ((2, 4, "wan"), 1)];
        var either = PathMapBuilder.Via(0, 3, "wan", several, ViaNodes);
        either.Pick.Should().Be(1);
        either.Either.Should().Equal("Riverton", "Oakdale");

        ((int, int, string), int)[] one = [((0, 1, "wan"), 4), ((0, 4, "wan"), 6), ((0, 3, "wan"), 1), ((1, 4, "wan"), 1)];
        PathMapBuilder.Via(0, 3, "wan", one, ViaNodes).Pick.Should().Be(1);
    }

    [Fact]
    public void Via_NeverAPlaceRoutesOnlyEndAt()
    {
        // Oakdale is only another route's end (no leg leaves it): it is not a router the stretch goes through.
        ((int, int, string), int)[] legs = [((0, 2, "wan"), 1), ((0, 3, "wan"), 1)];

        PathMapBuilder.Via(0, 3, "wan", legs, ViaNodes).Pick.Should().BeNull();
    }

    [Fact]
    public void Via_NeverAPlaceTheEndCannotBeReachedThrough()
    {
        // Riverton answers at 10 ms; Springfield, 620 km past it, answers at 12 ms: 6 ms short of the way through.
        var nodes = ViaNodes.Select((n, i) => i == 3 ? n with { Item4 = 12.0 } : n).ToArray();
        ((int, int, string), int)[] legs = [((0, 1, "wan"), 4), ((1, 4, "wan"), 1), ((0, 3, "wan"), 1)];

        PathMapBuilder.Via(0, 3, "wan", legs, nodes).Pick.Should().BeNull();
    }

    [Fact]
    public void Build_ARouterNeverTracedAlone_HighlightsTheWayRoutesReachIt()
    {
        var map = Build(
            Internet("a", "203.0.113.10", PathTargetHealth.Normal),
            Target("router", MonitoringLinks.TransitCategory, Transit, PathTargetHealth.Normal, null));

        var router = map.Targets.Single(t => t.TargetId == "router");
        // The start, the access hop (its own place in the same city), then the router.
        router.Path.Select(n => map.Nodes[n].City).Should().Equal("Lakeview", "Lakeview", "Riverton");
    }

    private static PathMapLeg Leg(PathMapView map, string from, string to) =>
        map.Legs.Single(l => map.Nodes[l.From].City == from && map.Nodes[l.To].City == to);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    [InlineData(6, 3)]
    [InlineData(40, 3)]
    public void Weight_IsThreeCappedSteps(int targets, int weight) =>
        PathMapBuilder.Weight(targets).Should().Be(weight);

    [Fact]
    public void Readings_LossTheHopsBeyondDoNotCarry_IsNotForwardingLoss()
    {
        var readings = PathMapBuilder.Readings(
        [
            new StoredRouteHop(1, Access, null, 1, 3, 1),
            new StoredRouteHop(2, Transit, null, 5, 3, 3),
        ]);

        readings[0].LossPct.Should().BeApproximately(66.7, 0.1);
        readings[0].EffectiveLossPct.Should().Be(0);
    }
}
