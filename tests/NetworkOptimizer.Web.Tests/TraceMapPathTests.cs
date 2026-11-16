using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class TraceMapPathTests
{
    private static HopLocationView City(string city, double lat, double lon, HopLocationSource source) =>
        new("US", $"{city}, United States", "", city, lat, lon, source);

    private static readonly Dictionary<string, HopLocationView> Locations = new()
    {
        ["198.51.100.1"] = City("Dallas", 32.78, -96.80, HopLocationSource.GeoLite2),
        ["198.51.100.2"] = City("Dallas", 32.78, -96.80, HopLocationSource.Hostname),
        ["198.51.100.3"] = City("Kansas City", 39.10, -94.58, HopLocationSource.Neighbor),
        ["198.51.100.4"] = new HopLocationView("US", "United States", ""),
    };

    [Fact]
    public void Build_HopsInOneCityShareAPoint_AndTheStrongestSourceWins()
    {
        var path = TraceMapPath.Build(
        [
            (1, "198.51.100.1", null, 1.0),
            (2, "198.51.100.2", "ae1.dllstx09.example.net", 1.2),
            (3, "198.51.100.3", null, 12.0),
        ], Locations);

        path.Nodes.Should().HaveCount(2);
        path.Nodes[0].Source.Should().Be("hostname");
        path.Nodes[0].Hops.Select(h => h.Number).Should().Equal(1, 2);
        path.Nodes[0].Hops[1].Label.Should().Be("ae1.dllstx09.example.net");
        path.Legs.Should().ContainSingle().Which.Kilometers.Should().BeInRange(700, 760);
        path.HasRoute.Should().BeTrue();
    }

    [Fact]
    public void Build_HopNumbersThatDidNotAnswer_CountTowardTheNextLeg()
    {
        // Hops 2 and 3 answered nothing; hop 4 answered but has only a country, which is not silence.
        var path = TraceMapPath.Build(
        [
            (1, "198.51.100.2", null, 1.0),
            (4, "198.51.100.4", null, 8.0),
            (5, "198.51.100.3", null, 12.0),
        ], Locations);

        path.Legs.Should().ContainSingle().Which.SilentHops.Should().Be(2);

        var heard = TraceMapPath.Build([(1, "198.51.100.2", null, 1.0), (2, "198.51.100.3", null, 12.0)], Locations);
        heard.Legs.Single().SilentHops.Should().Be(0);
    }

    [Fact]
    public void Build_UnplacedPublicHopsAreCounted_PrivateOnesAreNot()
    {
        var path = TraceMapPath.Build(
        [
            (1, "192.168.1.1", null, 0.3),
            (2, "198.51.100.1", null, 1.0),
            (3, "198.51.100.4", null, 5.0),
            (4, "203.0.113.200", null, 6.0),
            (5, "198.51.100.3", null, 12.0),
        ], Locations);

        path.UnplacedHops.Should().Be(2);
        path.Nodes.Should().HaveCount(2);
    }

    [Fact]
    public void Build_APathThatReturnsToACity_ReusesItsPointAndDrawsBothLegs()
    {
        var path = TraceMapPath.Build(
        [
            (1, "198.51.100.1", null, 1.0),
            (2, "198.51.100.3", null, 12.0),
            (3, "198.51.100.2", null, 24.0),
        ], Locations);

        path.Nodes.Should().HaveCount(2);
        path.Legs.Select(l => (l.From, l.To)).Should().Equal((0, 1), (1, 0));
    }

    [Fact]
    public void Build_TheStartIsTheFirstPoint_AndKeepsItsMarker()
    {
        var path = TraceMapPath.Build(
            [(1, "198.51.100.2", "ae1.dllstx09.example.net", 1.0), (2, "198.51.100.3", null, 12.0)],
            Locations, ("203.0.113.9", "Dallas", "US", 32.78, -96.80));

        path.Nodes[0].Source.Should().Be("start");
        path.Nodes[0].Hops.Select(h => h.Number).Should().Equal(0, 1);
    }

    [Fact]
    public void Build_OnePlace_HasNoRoute()
    {
        TraceMapPath.Build([(1, "198.51.100.1", null, 1.0)], Locations).HasRoute.Should().BeFalse();
    }
}
