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
        "198.51.100.20" => new GeoInfo { CountryCode = "US", City = "San Diego", Latitude = 32.72, Longitude = -117.16 },
        "198.51.100.21" => new GeoInfo { CountryCode = "US" },
        "198.51.100.22" => new GeoInfo { CountryCode = "US", City = "Overland Park", Latitude = 38.98, Longitude = -94.67 },
        "198.51.100.30" => new GeoInfo { CountryCode = "US", City = "Tulsa", Latitude = 36.15, Longitude = -95.99 },
        "198.51.100.31" => new GeoInfo { CountryCode = "US", City = "Pensacola", Latitude = 30.42, Longitude = -87.22 },
        "198.51.100.32" => new GeoInfo { CountryCode = "US", City = "Kansas City", Latitude = 39.10, Longitude = -94.58 },
        // A transit block GeoLite2 places far from its routers, and a destination network with no city.
        "198.51.100.40" => new GeoInfo { CountryCode = "US", City = "Warner Robins", Latitude = 32.62, Longitude = -83.63, AccuracyRadiusKm = 5, Asn = 64500 },
        "192.0.2.60" or "192.0.2.61" => new GeoInfo { CountryCode = "US", Latitude = 37.75, Longitude = -97.82, AccuracyRadiusKm = 1000, Asn = 64501 },
        "192.0.2.70" => new GeoInfo { CountryCode = "GB", Latitude = 51.50, Longitude = -0.12, AccuracyRadiusKm = 200 },
        _ => GeoInfo.Empty,
    };

    private const string KansasCityRouter = "bbr01.mci01.example.net";

    [Fact]
    public void Resolve_AGeoLite2CityTooFarFromTheNamedNeighbor_TakesTheNeighborsCity()
    {
        // 1 ms past a Kansas City router cannot be San Diego.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("198.51.100.20", null, 13.8),
        ], Geo);

        found["198.51.100.20"].Text.Should().Be("Kansas City, United States");
        found["198.51.100.20"].Tooltip.Should().Be("1.0 ms from bbr01, a Kansas City router. GeoLite2 says San Diego.");
    }

    [Fact]
    public void Resolve_AGeoLite2CityInReachOfTheNamedNeighbor_Stands()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("198.51.100.22", null, 13.8),
        ], Geo);

        found["198.51.100.22"].Text.Should().Be("Overland Park, United States");
    }

    [Theory]
    [InlineData("198.51.100.21")] // GeoLite2 gives the country only
    [InlineData("192.0.2.50")]    // GeoLite2 gives nothing (anycast)
    public void Resolve_AHopWithoutACity_TakesTheNamedNeighborsCity(string address)
    {
        // The named neighbor comes after it in the trace: either direction counts.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            (address, null, 12.4),
            ("203.0.113.5", KansasCityRouter, 12.8),
        ], Geo);

        found[address].Text.Should().Be("Kansas City, United States");
    }

    [Fact]
    public void Resolve_ANamedNeighborTooManyMsAway_IsNotBorrowed()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("198.51.100.20", null, 12.8 + HopLocations.NeighborGapMs + 1),
            ("192.0.2.50", null, 30.0),
        ], Geo);

        found["198.51.100.20"].Text.Should().Be("San Diego, United States");
        found.Should().NotContainKey("192.0.2.50");
    }

    [Fact]
    public void Resolve_AHopBetweenTwoRoutersInOneCity_TakesThatCityOverGeoLite2()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("198.51.100.22", null, 12.8),
            ("203.0.113.6", "ae2.mci02.example.net", 13.0),
        ], Geo);

        found["198.51.100.22"].Text.Should().Be("Kansas City, United States");
        found["198.51.100.22"].Tooltip.Should().Be("0.0 ms from bbr01, a Kansas City router. GeoLite2 says Overland Park.");
    }

    [Fact]
    public void Resolve_NoHostnames_TransitHopsGiveWayToTheLastPlacedHop()
    {
        // Pensacola and San Diego cannot sit within 1 ms of Kansas City; the vantage can reach Kansas City.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.30", null, 2.7),
            ("100.64.0.1", null, 5.3),
            ("198.51.100.31", null, 12.6),
            ("198.51.100.20", null, 13.7),
            ("198.51.100.32", null, 13.4),
            ("192.0.2.50", null, 12.8),
        ], Geo);

        found["198.51.100.30"].Text.Should().Be("Tulsa, United States");
        found["198.51.100.31"].Text.Should().Be("Kansas City, United States");
        found["198.51.100.31"].Tooltip.Should().Be("0.8 ms from 198.51.100.32, which GeoLite2 places in Kansas City. GeoLite2 says Pensacola.");
        found["198.51.100.20"].Text.Should().Be("Kansas City, United States");
        found["192.0.2.50"].Text.Should().Be("Kansas City, United States");
        found.Should().NotContainKey("100.64.0.1");
    }

    [Fact]
    public void Resolve_ALastPlacedHopOutOfReachOfTheVantage_AnchorsNothing()
    {
        // San Diego is too far from Tulsa for 11 ms, so the Pensacola hop keeps GeoLite2's answer.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.30", null, 2.7),
            ("198.51.100.31", null, 13.0),
            ("198.51.100.20", null, 13.7),
        ], Geo);

        found["198.51.100.31"].Text.Should().Be("Pensacola, United States");
        found["198.51.100.20"].Text.Should().Be("United States");
    }

    [Fact]
    public void Resolve_AGeoLite2CityTooFarFromAnAnchor_ShowsTheCountryOnly()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("198.51.100.20", null, 8.0),
            ("203.0.113.7", "ae1.lax01.example.net", 40.0),
        ], Geo);

        found["198.51.100.20"].Text.Should().Be("United States");
        found["198.51.100.20"].Tooltip.Should().Be("GeoLite2 says San Diego: too far from Kansas City for 7.0 ms.");
    }

    [Fact]
    public void Resolve_AHostnameCityTheEndOfTheTraceCannotReach_IsRuledOut()
    {
        // 1 ms before a hop in Kansas City's suburbs cannot be Los Angeles.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("203.0.113.7", "ae1.lax01.example.net", 40.0),
            ("198.51.100.22", null, 41.0),
        ], Geo);

        // Borrowed from the Overland Park end hop, so shown at its hub.
        found["203.0.113.7"].Text.Should().Be("Kansas City, United States");
    }

    [Fact]
    public void Resolve_ACityBorrowedFromASuburb_IsShownAtTheNearestHub()
    {
        var found = HopLocations.Resolve(
        [
            ("203.0.113.7", "108-0-2-1.lightspeed.tukrga.example.net", 2.5),
            ("192.0.2.61", null, 2.6),
        ], Geo);

        found["203.0.113.7"].Text.Should().Be("Tucker, United States");
        found["192.0.2.61"].Text.Should().Be("Atlanta, United States");
        found["192.0.2.61"].Tooltip.Should().Be("0.1 ms from 108-0-2-1, a Tucker router, near Atlanta");
    }

    [Fact]
    public void Resolve_ASlowAnsweringHop_IsBoundedByTheFasterHopAfterIt()
    {
        // The second Kansas City router answers its own probes in 6.4 ms, but the hop behind it answers
        // in 2.1 ms, so both sit within 3 ms of the first.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("203.0.113.7", KansasCityRouter, 1.9),
            ("198.51.100.22", null, 1.9),
            ("203.0.113.6", "ae2.mci02.example.net", 6.4),
            ("192.0.2.50", null, 2.1),
        ], Geo);

        found["198.51.100.22"].Text.Should().Be("Kansas City, United States");
        found["192.0.2.50"].Text.Should().Be("Kansas City, United States");
    }

    [Fact]
    public void Resolve_AHostnamePlacedVantage_StillLetsTheLastPlacedHopAnchor()
    {
        // The anycast hop borrows from the slow-answering hop right before it, not the router three hops back.
        var found = HopLocations.Resolve(
        [
            ("203.0.113.5", KansasCityRouter, 1.6),
            ("198.51.100.22", null, 2.7),
            ("198.51.100.32", null, 5.4),
            ("192.0.2.50", null, 2.8),
        ], Geo);

        found["192.0.2.50"].Tooltip.Should().Be("2.6 ms from 198.51.100.32, which GeoLite2 places in Kansas City");
    }

    [Fact]
    public void Resolve_ACountryOnlyHop_IsNeverTheVantage()
    {
        // The anycast hop answers fastest, but GeoLite2 has only the country's center for it.
        var found = HopLocations.Resolve(
        [
            ("203.0.113.7", "ae-41.a03.atlnga05.example.net", 2.7),
            ("192.0.2.61", null, 2.6),
        ], Geo);

        found["203.0.113.7"].Text.Should().Be("Atlanta, United States");
        found["192.0.2.61"].Text.Should().Be("Atlanta, United States");
    }

    [Fact]
    public void Resolve_ATransitHopOutsideTheDestinationsNetwork_DoesNotAnchorTheEnd()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.30", null, 3.0),
            ("203.0.113.8", "dalsbprj01-ae2.rd.dl.cox.net", 16.5),
            ("198.51.100.40", null, 15.8),
            ("192.0.2.60", null, 15.1),
            ("192.0.2.61", null, 15.4),
        ], Geo);

        found["203.0.113.8"].Text.Should().Be("Dallas, United States");
        found["192.0.2.60"].Text.Should().Be("Dallas, United States");
        found["192.0.2.61"].Text.Should().Be("Dallas, United States");
    }

    [Fact]
    public void Resolve_AForeignCountryTooFarForTheRoundTrip_TakesTheNeighborsCity()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("203.0.113.7", KansasCityRouter, 1.9),
            ("192.0.2.70", null, 2.7),
        ], Geo);

        found["192.0.2.70"].Text.Should().Be("Kansas City, United States");
        found["192.0.2.70"].Tooltip.Should().Be("0.8 ms from bbr01, a Kansas City router. GeoLite2 says United Kingdom.");
    }

    [Theory]
    [InlineData("192.168.1.254")]
    [InlineData("100.64.0.1")]
    public void Resolve_APrivateHop_NeverBorrowsANeighborsCity(string address)
    {
        var found = HopLocations.Resolve(
        [
            (address, null, 1.4),
            ("203.0.113.5", KansasCityRouter, 2.7),
        ], Geo);

        found.Should().NotContainKey(address);
    }

    [Fact]
    public void Resolve_ANeighborInAnotherCountry_IsNotBorrowed()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("203.0.113.9", null, 13.0),
        ], Geo);

        found["203.0.113.9"].Text.Should().Be("Japan");
    }

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
