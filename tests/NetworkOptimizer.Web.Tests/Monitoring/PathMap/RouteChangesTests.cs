using FluentAssertions;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using NetworkOptimizer.Web.Services.Monitoring.PathMap;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathMap;

public class RouteChangesTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private const string Access = "198.51.100.1";
    private const string Transit = "198.51.100.2";
    private const string OtherTransit = "198.51.100.3";

    private static readonly Dictionary<string, (string City, double Lat, double Lon)> Cities = new()
    {
        ["Lakeview"] = ("Lakeview", 35.0, -97.0),
        ["Riverton"] = ("Riverton", 36.0, -95.0),
        ["Hillsboro"] = ("Hillsboro", 30.0, -87.0),
        ["Oakdale"] = ("Oakdale", 38.0, -90.0),
    };

    private static readonly Dictionary<string, PathMapStart> Starts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wan"] = new PathMapStart("Lakeview", "US", 35.0, -97.0, "WAN"),
    };

    /// <summary>A route through (address, city, source, ASN) hops, ending at its last address.</summary>
    private static PlacedRoute Route(DateTime at, params (string Address, string City, HopLocationSource Source, int Asn)[] hops)
    {
        var stored = new StoredRoute(at, "server", null, hops[^1].Address,
            hops.Select((h, i) => new StoredRouteHop(i + 1, h.Address, null, 10.0 * (i + 1), 3, 3)).ToList(), null);
        var locations = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase);
        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hops)
        {
            var c = Cities[h.City];
            locations[h.Address] = new HopLocationView("US", $"{c.City}, United States", "placed", c.City, c.Lat, c.Lon, h.Source);
            asns[h.Address] = new AsnLookup(h.Asn, $"Network {h.Asn}");
        }
        return new PlacedRoute(stored, locations, asns);
    }

    private static PathMapTargetInput Target(string id, PlacedRoute from, PlacedRoute to, DateTime at) =>
        new(id, id, MonitoringLinks.InternetServiceCategory, "wan", null, false, to.Route.Destination!, to, from, at, Now,
            new PathTargetState(PathTargetHealth.Normal, 20, 20, 0, null, null), [], "/network-tools", ShiftTo: to);

    [Fact]
    public void IsReal_SameRouterPlacedBetterLater_IsNotARouteChange()
    {
        // The transit router was placed by GeoLite2 when the old route was stored, by its hostname since.
        var before = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (Transit, "Hillsboro", HopLocationSource.GeoLite2, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));
        var after = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (Transit, "Riverton", HopLocationSource.Hostname, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));

        RouteChanges.IsReal(before, after).Should().BeFalse();
    }

    [Fact]
    public void IsReal_DifferentRouterInAnotherCity_IsARouteChange()
    {
        var before = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (Transit, "Hillsboro", HopLocationSource.Hostname, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));
        var after = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (OtherTransit, "Riverton", HopLocationSource.Hostname, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));

        RouteChanges.IsReal(before, after).Should().BeTrue();
    }

    [Fact]
    public void RouteChangesOf_TargetsLeavingTheSamePlacesTogether_AreOneChange()
    {
        PlacedRoute Old(string dest) => Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500),
            (Transit, "Hillsboro", HopLocationSource.Hostname, 64501), (dest, "Oakdale", HopLocationSource.Hostname, 64510));
        PlacedRoute New(string dest) => Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500),
            (OtherTransit, "Riverton", HopLocationSource.Hostname, 64501), (dest, "Oakdale", HopLocationSource.Hostname, 64510));

        var changes = PathMapBuilder.RouteChangesOf(
        [
            Target("a", Old("203.0.113.10"), New("203.0.113.10"), Now),
            Target("b", Old("203.0.113.11"), New("203.0.113.11"), Now.AddMinutes(2)),
            // The same change three hours later is another one.
            Target("c", Old("203.0.113.12"), New("203.0.113.12"), Now.AddHours(3)),
        ], Starts);

        changes.Should().HaveCount(2);
        var first = changes.Single(c => c.At == Now);
        first.Targets.Select(t => t.TargetId).Should().Equal("a", "b");
        first.Network.Should().Be("Network 64501");
        first.Before.Should().Equal("Hillsboro");
        first.After.Should().Equal("Riverton");
        first.Diverge.Should().Equal(35.0, -97.0);
        first.Targets[0].RttBeforeMs.Should().Be(30.0);
    }

    [Fact]
    public void Build_TargetCarriesItsRouteChange()
    {
        var before = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (Transit, "Hillsboro", HopLocationSource.Hostname, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));
        var after = Route(Now, (Access, "Lakeview", HopLocationSource.Hostname, 64500), (OtherTransit, "Riverton", HopLocationSource.Hostname, 64501),
            ("203.0.113.10", "Oakdale", HopLocationSource.Hostname, 64510));

        var map = PathMapBuilder.Build([Target("a", before, after, Now)], Starts, [], new(), live: true, asOf: null);

        map.RouteChanges.Should().ContainSingle();
        map.Targets.Single().RouteChange.Should().Be(map.RouteChanges![0].Id);
    }
}
