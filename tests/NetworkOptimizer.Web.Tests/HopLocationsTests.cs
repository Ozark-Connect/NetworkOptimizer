using FluentAssertions;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class HopLocationsTests
{
    // GeoLite2 stand-in: the first public hop sits in Dallas; the transit router's block is registered in Virginia.
    private static GeoInfo Geo(string address) => address switch
    {
        "198.51.100.1" => new GeoInfo { CountryCode = "US", City = "Dallas", Latitude = 32.78, Longitude = -96.80 },
        "203.0.113.5" => new GeoInfo { CountryCode = "US", City = "Ashburn", Latitude = 39.04, Longitude = -77.49 },
        "203.0.113.9" => new GeoInfo { CountryCode = "JP" },
        _ => GeoInfo.Empty,
    };

    [Fact]
    public void Resolve_HostnameCityWinsOverGeoLite2()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", "ae-1.r21.dllstx09.us.bb.gin.ntt.net", 2.0),
        ], Geo);

        found["203.0.113.5"].Text.Should().Be("Dallas, United States");
        found["203.0.113.5"].CountryCode.Should().Be("US");
        found["203.0.113.5"].Tooltip.Should().Contain("dllstx09");
    }

    [Fact]
    public void Resolve_NoCityInTheHostname_FallsBackToGeoLite2()
    {
        var found = HopLocations.Resolve([("203.0.113.5", "core1.example.net", 30.0)], Geo);

        found["203.0.113.5"].Text.Should().Be("Ashburn, United States");
        found["203.0.113.5"].Tooltip.Should().Contain("GeoLite2");
    }

    [Fact]
    public void Resolve_GeoLite2WithoutACity_ShowsTheCountry()
    {
        HopLocations.Resolve([("203.0.113.9", null, 150.0)], Geo)["203.0.113.9"].Text.Should().Be("Japan");
    }

    [Fact]
    public void Resolve_TheFirstPlacedHopIsTheVantage_SoAFarHostnameCityIsRuledOut()
    {
        // "maa" is Chennai: 15 ms from Dallas cannot reach India, so GeoLite2's answer stands.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", "lo1.bharent-maa3.netarch.akamai.com", 15.0),
        ], Geo);

        found["203.0.113.5"].Text.Should().Be("Ashburn, United States");
    }

    [Fact]
    public void Resolve_UnplacedAndAddresslessHopsAreLeftOut()
    {
        var found = HopLocations.Resolve(
        [
            (null, "ae-1.r21.dllstx09.us.bb.gin.ntt.net", 2.0),
            ("192.0.2.1", "gw.example.net", 0.5),
        ], Geo);

        found.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_TheVantageIsTheNearestPlacedHop_NotTheFirstInOrder()
    {
        // The Virginia-registered router is listed first but answers slower than the Dallas edge.
        var found = HopLocations.Resolve(
        [
            ("203.0.113.5", "ae-1.r21.dllstx09.us.bb.gin.ntt.net", 3.0),
            ("198.51.100.1", null, 1.0),
        ], Geo);

        found["203.0.113.5"].Text.Should().Be("Dallas, United States");
    }

    [Fact]
    public void Resolve_TheVantageHopIsNotCheckedAgainstItself()
    {
        // Its own GeoLite2 location is Virginia, but nothing else places the start: the hostname stands.
        var found = HopLocations.Resolve([("203.0.113.5", "ae-1.r21.dllstx09.us.bb.gin.ntt.net", 2.0)], Geo);

        found["203.0.113.5"].Text.Should().Be("Dallas, United States");
    }

    [Fact]
    public void Resolve_AnAddressSeenTwiceKeepsItsFirstReading()
    {
        var found = HopLocations.Resolve(
        [
            ("203.0.113.5", "ae-1.r21.dllstx09.us.bb.gin.ntt.net", 2.0),
            ("203.0.113.5", "core1.example.net", 2.0),
        ], Geo);

        found.Should().ContainSingle().Which.Value.Text.Should().Be("Dallas, United States");
    }
}
