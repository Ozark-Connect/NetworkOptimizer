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
        // An operator's block GeoLite2 places at its headquarters, far from the routers that answer.
        "192.0.2.80" => new GeoInfo { CountryCode = "US", City = "Akron", Latitude = 41.08, Longitude = -81.52, AccuracyRadiusKm = 500, Asn = 64510 },
        "192.0.2.71" => new GeoInfo { CountryCode = "FR", Latitude = 48.86, Longitude = 2.34, AccuracyRadiusKm = 1000 },
        _ => GeoInfo.Empty,
    };

    private const string KansasCityRouter = "bbr01.mci01.example.net";

    private static Dictionary<string, HopLocationSetting> SetTo(string address, string city, double lat, double lon,
        string country = "US", string? by = "Admin") =>
        new(StringComparer.OrdinalIgnoreCase) { [address] = new HopLocationSetting(address, city, null, country, lat, lon, by) };

    [Fact]
    public void Resolve_AHostnameCityTwoHopsAgreeOn_IsNotRuledOutByTheEnd()
    {
        // Two routers 0.1 ms apart both name Little Rock; GeoLite2 puts the last hop 1,150 km away.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.20", "agr02.ltrk01-ar.example.net", 8.0),
            ("203.0.113.21", "cdn27.ltrk01-ar.example.net", 8.1),
            ("192.0.2.80", null, 8.0),
        ], Geo);

        found["203.0.113.20"].Text.Should().Be("Little Rock, United States");
        found["203.0.113.21"].Text.Should().Be("Little Rock, United States");
        found["192.0.2.80"].Text.Should().Be("Little Rock, United States");
        found["192.0.2.80"].Source.Should().Be(HopLocationSource.Neighbor);
    }

    [Fact]
    public void Resolve_ACitySetOnADelayedHop_LendsItToNoNeighbor()
    {
        // An MPLS hop answers at its tunnel's far end (30 ms) between hops at 2 and 25 ms: set by hand to Atlanta, it is
        // Atlanta itself, but its round trip says nothing about the routers after it.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.70", null, 2.0),
            ("203.0.113.71", null, 30.0),
            ("203.0.113.72", null, 25.0),
            ("203.0.113.73", null, 25.2),
        ], Geo, SetTo("203.0.113.71", "Atlanta", 33.75, -84.39));

        found["203.0.113.71"].City.Should().Be("Atlanta");
        found.GetValueOrDefault("203.0.113.72")?.City.Should().NotBe("Atlanta");
        found.GetValueOrDefault("203.0.113.73")?.City.Should().NotBe("Atlanta");
    }

    [Fact]
    public void Resolve_ACitySetFarOverAnAnchorInItsOwnMetro_LendsItToNoNeighbor()
    {
        // Set to Kansas City, it answers 24 ms past a Kansas City router, and only 0.5 ms over the hops after it: the
        // 24 ms is not distance, so the routers at 26 ms are not beside it.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 0.5),
            ("203.0.113.5", KansasCityRouter, 2.0),
            ("203.0.113.81", null, 26.5),
            ("203.0.113.82", null, 26.0),
            ("203.0.113.83", null, 26.2),
        ], Geo, SetTo("203.0.113.81", "Kansas City", 39.10, -94.58));

        found["203.0.113.81"].City.Should().Be("Kansas City");
        found.GetValueOrDefault("203.0.113.82")?.City.Should().NotBe("Kansas City");
        found.GetValueOrDefault("203.0.113.83")?.City.Should().NotBe("Kansas City");
    }

    [Fact]
    public void Resolve_ACitySetOnASlowHopWithANarrowRange_StillLendsIt()
    {
        // Slow to answer (9 ms), but the hops either side hold its round trip to 2.0 to 2.2 ms: it still anchors.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.70", null, 2.0),
            ("203.0.113.71", null, 9.0),
            ("203.0.113.72", null, 2.2),
        ], Geo, SetTo("203.0.113.71", "Atlanta", 33.75, -84.39));

        found["203.0.113.72"].City.Should().Be("Atlanta");
        found["203.0.113.72"].Source.Should().Be(HopLocationSource.Neighbor);
    }

    [Fact]
    public void Resolve_AnchorWithNoPtr_IsNamedByItsWholeAddress()
    {
        // The vantage reports a hop with no PTR record by its address as its hostname.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.61", null, 4.5),
            ("203.0.113.60", "203.0.113.60", 4.0),
        ], Geo, SetTo("203.0.113.60", "Little Rock", 34.75, -92.29));

        found["203.0.113.61"].Tooltip.Should().StartWith("0.5 ms from 203.0.113.60, a Little Rock router");
    }

    [Fact]
    public void Resolve_ACitySetByHand_RulesOutAFarHostnameRightBesideIt_NotOneFurtherAway()
    {
        // A handoff port named for Kansas City answers 0.5 ms from a router set to Salt Lake City, at the trace's end.
        // A Kansas City router four hops earlier, its round trip bounded by later hops, keeps its hostname city.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 50.0),
            ("203.0.113.40", null, 61.0),
            ("203.0.113.41", null, 61.5),
            ("203.0.113.42", "ae-0.examplecust.mci01.example.net", 62.0),
            ("203.0.113.43", null, 61.5),
        ], Geo, SetTo("203.0.113.43", "Salt Lake City", 40.76, -111.89));

        found["203.0.113.42"].Source.Should().NotBe(HopLocationSource.Hostname);
        found["203.0.113.5"].Source.Should().Be(HopLocationSource.Hostname);
    }

    [Fact]
    public void Resolve_ASetLocation_WinsOverTheHostname()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
        ], Geo, SetTo("203.0.113.5", "Tulsa", 36.15, -95.99));

        found["203.0.113.5"].Text.Should().Be("Tulsa, United States");
        found["203.0.113.5"].Source.Should().Be(HopLocationSource.Set);
        found["203.0.113.5"].Tooltip.Should().Be("Set by Admin");
    }

    [Fact]
    public void Resolve_ASetLocation_AnchorsTheHopsBesideIt()
    {
        // The hop 1 ms on, with only a country from GeoLite2, borrows the set city.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", null, 12.8),
            ("198.51.100.21", null, 13.8),
        ], Geo, SetTo("203.0.113.5", "Tulsa", 36.15, -95.99));

        found["198.51.100.21"].Text.Should().Be("Tulsa, United States");
        found["198.51.100.21"].Source.Should().Be(HopLocationSource.Neighbor);
    }

    [Fact]
    public void Resolve_ASetLocation_IsNotRuledOutByTheRoundTrip()
    {
        // 0.5 ms from a Dallas vantage cannot reach Tokyo, but a person said so.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", null, 1.5),
            ("198.51.100.30", null, 9.0),
        ], Geo, SetTo("203.0.113.5", "Tokyo", 35.68, 139.69, country: "JP", by: null));

        found["203.0.113.5"].Text.Should().Be("Tokyo, Japan");
        found["203.0.113.5"].Tooltip.Should().Be("Set by hand");
    }

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
        // 1 ms before a hop in Kansas City's suburbs cannot be Los Angeles: an interface named for the
        // far end of its link. A hop after it keeps it from being the final PoP.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("203.0.113.7", "ae1.lax01.example.net", 40.0),
            ("203.0.113.9", null, 40.5),
            ("198.51.100.22", null, 41.0),
        ], Geo);

        // Borrowed from the Overland Park end hop, so shown at its hub.
        found["203.0.113.7"].Text.Should().Be("Kansas City, United States");
    }

    [Fact]
    public void Resolve_ANamedFinalPopRightBeforeTheDestination_IsNotRuledOutByTheDestinationsGeoLite2City()
    {
        // The router right before the destination is where the destination was reached: its name
        // outranks the destination's GeoLite2 city, and the destination 1 ms past it is there too.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.32", null, 1.0),
            ("203.0.113.7", "ae1.lax01.example.net", 40.0),
            ("198.51.100.22", null, 41.0),
        ], Geo);

        found["203.0.113.7"].Text.Should().Be("Los Angeles, United States");
        found["198.51.100.22"].Text.Should().Be("Los Angeles, United States");
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

    [Fact]
    public void Resolve_AnExchangeHopPlacesItselfButAnchorsNothing()
    {
        // Arelion's "san" is San Antonio. The hop after it, which GeoLite2 places in France, takes San
        // Antonio, not the Equinix Dallas exchange hop: that name is the fabric's, and the member
        // answering on it can sit far from it.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.30", null, 2.4),
            ("203.0.113.11", "kanc-bb2-link.ip.twelve99.net", 12.9),
            ("203.0.113.12", "dls-bb4-link.ip.twelve99.net", 19.9),
            ("203.0.113.14", "san-b2-link.ip.twelve99.net", 26.0),
            ("192.0.2.71", null, 32.5),
            ("203.0.113.13", "eqix.da4.example.com", 27.0),
        ], Geo);

        found["203.0.113.14"].Text.Should().Be("San Antonio, United States");
        found["192.0.2.71"].Text.Should().Be("San Antonio, United States");
        found["203.0.113.13"].Text.Should().Be("Dallas, United States");
    }

    [Fact]
    public void Resolve_AForeignCountryTooFarFromAnAnchor_HasNoLocation()
    {
        // 6 ms past a Dallas router cannot be in the United Kingdom, and no anchor is within 3 ms.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.30", null, 3.0),
            ("203.0.113.7", "ae1.r1.dllstx09.example.net", 19.9),
            ("192.0.2.70", null, 26.0),
        ], Geo);

        found["192.0.2.70"].Text.Should().Be("-");
        found["192.0.2.70"].CountryCode.Should().BeNull();
        found["192.0.2.70"].Tooltip.Should().Be("GeoLite2 says United Kingdom: too far from Dallas for 6.1 ms.");
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

    // An anycast resolver GeoLite2 places far from where a trace reaches it.
    private static GeoInfo GeoAnycast(string address) => address switch
    {
        "4.2.2.1" or "4.2.2.2" => new GeoInfo { CountryCode = "US", City = "Greenville", Latitude = 34.85, Longitude = -82.40, AccuracyRadiusKm = 5, Asn = 3356 },
        "198.51.100.50" => new GeoInfo { CountryCode = "US", City = "Birmingham", Latitude = 33.52, Longitude = -86.80, AccuracyRadiusKm = 5 },
        _ => Geo(address),
    };

    [Fact]
    public void Resolve_AnycastAtTheEnd_DoesNotRuleOutTheHostnameCityBeforeIt()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.6", "ae1.3511.edge2.Dallas1.net.lumen.tech", 15.7),
            ("4.2.2.1", "a.resolvers.level3.net", 15.6),
        ], GeoAnycast);

        found["203.0.113.6"].City.Should().Be("Dallas");
        found["203.0.113.6"].Source.Should().Be(HopLocationSource.Hostname);
        found["4.2.2.1"].City.Should().Be("Dallas");
        found["4.2.2.1"].Tooltip.Should().StartWith("Anycast:");
    }

    [Fact]
    public void Resolve_Anycast_IsPlacedAtTheMetroItsRoundTripFits()
    {
        // 4.5 ms past a Houston router (beyond a neighbor's few ms): Dallas is the interconnection metro in reach.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.7", "ae1.edge1.Houston1.Level3.net", 10.0),
            ("4.2.2.2", "b.resolvers.level3.net", 14.5),
        ], GeoAnycast);

        found["203.0.113.7"].City.Should().Be("Houston");
        found["4.2.2.2"].City.Should().Be("Dallas");
        found["4.2.2.2"].Text.Should().NotContain("Greenville");
    }

    [Fact]
    public void Resolve_ADestinationRightAfterANamedFinalPop_TakesThePopsCity()
    {
        // GeoLite2 places the destination ~920 km away; it answered 0.4 ms past a Houston router.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.7", "ae1.edge1.Houston1.Level3.net", 10.0),
            ("198.51.100.50", null, 10.4),
        ], GeoAnycast);

        found["203.0.113.7"].City.Should().Be("Houston");
        found["198.51.100.50"].City.Should().Be("Houston");
        found["198.51.100.50"].Source.Should().Be(HopLocationSource.Neighbor);
    }

    [Fact]
    public void Resolve_TheDestinationsGeoLite2City_DoesNotRuleOutANamedFinalPop()
    {
        // The end's GeoLite2 city cannot reach Houston in 0.4 ms; the final PoP's name wins.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", null, 5.0),
            ("203.0.113.7", "ae1.edge1.Houston1.Level3.net", 10.0),
            ("198.51.100.50", null, 10.4),
        ], GeoAnycast);

        found["203.0.113.7"].Source.Should().Be(HopLocationSource.Hostname);
        found["203.0.113.7"].City.Should().Be("Houston");
    }

    [Fact]
    public void Resolve_AHandoffPortNamedForTheDestinationsNetwork_IsNotTrustedAsTheFinalPop()
    {
        // NTT's port to a customer network, named for the Los Angeles router at its other end, answers
        // from the customer's city: the end of the trace still rules its Los Angeles name out.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.20", "ae-2.a03.lsanca07.us.bb.gin.ntt.net", 46.3),
            ("203.0.113.21", "ae-0.examplenet.lsanca07.us.bb.gin.ntt.net", 62.0),
            ("198.51.100.60", "mar2.core.examplenet.net", 61.3),
        ], GeoHandoff);

        found["203.0.113.21"].City.Should().NotBe("Los Angeles");
        found["198.51.100.60"].City.Should().Be("Lindon");
    }

    private static GeoInfo GeoHandoff(string address) => address switch
    {
        "198.51.100.60" => new GeoInfo { CountryCode = "US", City = "Lindon", Latitude = 40.34, Longitude = -111.72, AccuracyRadiusKm = 5, Asn = 64600, AsnOrg = "Examplenet" },
        _ => Geo(address),
    };

    [Fact]
    public void Start_ACitySetByHandForTheWanAddress_WinsOverGeoLite2()
    {
        HopLocations.Start("198.51.100.1", Geo, SetTo("198.51.100.1", "Tulsa", 36.15, -95.99))!.Value.City.Should().Be("Tulsa");
        HopLocations.Start("198.51.100.1", Geo, null)!.Value.City.Should().Be("Dallas");
        HopLocations.Start("192.0.2.200", Geo, null).Should().BeNull();
    }

    [Fact]
    public void NamesNetwork_ReadsTheOtherHopsDomainAndNetworkName_NotGenericWords()
    {
        HopLocations.NamesNetwork("ae-0.examplenet.lsanca07.us.bb.gin.ntt.net", "mar2.core.examplenet.net", null).Should().BeTrue();
        HopLocations.NamesNetwork("ae-0.examplenet.lsanca07.us.bb.gin.ntt.net", null, "Examplenet, L.C.").Should().BeTrue();
        HopLocations.NamesNetwork("ae1.edge1.Houston1.Level3.net", null, "Example Communications Cable").Should().BeFalse();
        HopLocations.NamesNetwork("ae1.edge1.Houston1.Level3.net", "host.example.com", null).Should().BeFalse();
    }

    [Fact]
    public void Resolve_AnycastRightAfterANamedFinalPop_TakesThePopsCity()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.6", "ae1.3511.edge2.Dallas1.net.lumen.tech", 15.1),
            ("4.2.2.1", "a.resolvers.level3.net", 15.6),
        ], GeoAnycast);

        found["4.2.2.1"].City.Should().Be("Dallas");
        found["4.2.2.1"].Tooltip.Should().Contain("0.5 ms past");
    }

    [Fact]
    public void Resolve_Anycast_WithSeveralMetrosInReach_IsNotGuessed()
    {
        // 8 ms past a Kansas City router reaches Chicago, Dallas, and Denver: no one metro to name.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 12.8),
            ("4.2.2.2", "b.resolvers.level3.net", 20.8),
        ], GeoAnycast);

        found["4.2.2.2"].City.Should().BeNull();
        found["4.2.2.2"].Text.Should().Be("Anycast");
        found["4.2.2.2"].Tooltip.Should().Contain("Chicago").And.Contain("Dallas");
    }

    [Fact]
    public void Resolve_Anycast_SlowToAnswer_KeepsTheHopsOwnMetroInReach()
    {
        // 6 ms past a Dallas router can be the resolver's own slow reply, not distance: Dallas stays a
        // candidate, so it is not placed in Houston by elimination.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.6", "ae1.3511.edge2.Dallas1.net.lumen.tech", 15.0),
            ("4.2.2.2", "b.resolvers.level3.net", 21.0),
        ], GeoAnycast);

        found["4.2.2.2"].City.Should().NotBe("Houston");
        found["4.2.2.2"].Tooltip.Should().Contain("Dallas");
    }

    [Fact]
    public void Resolve_Anycast_WithNothingPlacedBeforeIt_HasNoCity()
    {
        var found = HopLocations.Resolve([("4.2.2.2", null, 13.0)], GeoAnycast);

        found["4.2.2.2"].City.Should().BeNull();
        found["4.2.2.2"].Text.Should().Be("Anycast");
    }

    [Fact]
    public void Resolve_AGeoLite2CityPastAHostnameRouter_IsHeldToCityScaleSlack()
    {
        // About 920 km from Houston in 4.9 ms: out of reach once the anchor is an exact city. A hostname
        // router after it keeps the end of the trace from anchoring it.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.7", "ae1.edge1.Houston1.Level3.net", 10.0),
            ("198.51.100.50", null, 14.9),
            ("203.0.113.8", "ae2.edge1.Houston1.Level3.net", 19.0),
        ], GeoAnycast);

        found["198.51.100.50"].City.Should().BeNull();
        found["198.51.100.50"].Tooltip.Should().Contain("too far from Houston");
    }

    private static Dictionary<string, LearnedHop> Learned(string address, string city, double lat, double lon,
        HopLocationSource source = HopLocationSource.Hostname) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            [address] = new LearnedHop(address, city, "US", lat, lon, source, "From the router's hostname (mci01)", DateTime.UtcNow),
        };

    [Fact]
    public void Resolve_AGeoLite2OnlyHop_TakesTheCityLearnedOnAnEarlierTrace()
    {
        // GeoLite2 places the router at its network's registration; an earlier trace read its hostname.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("198.51.100.31", null, 10.0),
        ], Geo, learned: Learned("198.51.100.31", "Kansas City", 39.10, -94.58));

        found["198.51.100.31"].Text.Should().Be("Kansas City, United States");
        found["198.51.100.31"].Source.Should().Be(HopLocationSource.Learned);
        found["198.51.100.31"].Tooltip.Should().Contain("This trace alone says Pensacola");
    }

    [Fact]
    public void Resolve_AHostnameOrAHandSetCity_IsNeverReplacedByALearnedOne()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 10.0),
        ], Geo, SetTo("198.51.100.1", "Plano", 33.02, -96.70), Learned("203.0.113.5", "Chicago", 41.88, -87.63));

        found["203.0.113.5"].Source.Should().Be(HopLocationSource.Hostname);
        found["198.51.100.1"].Source.Should().Be(HopLocationSource.Set);
    }

    [Fact]
    public void Resolve_ANearbyHopCity_IsNeverReplacedByALearnedOne()
    {
        // The trace's own neighbor places the hop; a hostname learned elsewhere never overrides it.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 10.0),
            ("198.51.100.20", null, 11.0),
        ], Geo, learned: Learned("198.51.100.20", "Los Angeles", 34.05, -118.24));

        found["198.51.100.20"].Source.Should().Be(HopLocationSource.Neighbor);
        found["198.51.100.20"].City.Should().Be("Kansas City");
    }

    [Fact]
    public void Resolve_ALearnedCityTheRoundTripsRuleOut_IsNotUsed()
    {
        // GeoLite2 alone for the hop, 1 ms past a Kansas City router: a learned Los Angeles is 2,200 km too far.
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("203.0.113.5", KansasCityRouter, 10.0),
            ("198.51.100.31", null, 11.0),
        ], Geo, learned: Learned("198.51.100.31", "Los Angeles", 34.05, -118.24));

        found.GetValueOrDefault("198.51.100.31")?.Source.Should().NotBe(HopLocationSource.Learned);
    }

    [Fact]
    public void Learnable_AHandoffPortNamedForTheNextNetwork_IsNotLearned()
    {
        static GeoInfo AsnOf(string address) => new() { Asn = 64500, AsnOrg = "Example Transit" };
        (string?, string?, double?)[] hops =
        [
            ("203.0.113.5", "ae-0.examplecust.lsanca07.example.net", 60.0),
            ("203.0.113.6", "core1.examplecust.net", 61.0),
            ("192.0.2.9", null, 62.0),
        ];
        var found = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase)
        {
            ["203.0.113.5"] = new("US", "Los Angeles, United States", "x", "Los Angeles", 34.05, -118.24, HopLocationSource.Hostname),
        };

        HopLocations.Learnable(hops, found, AsnOf, DateTime.UtcNow).Should().BeEmpty();
    }

    [Fact]
    public void Resolve_AnAnycastAddress_NeverTakesALearnedCity()
    {
        var found = HopLocations.Resolve(
        [
            ("198.51.100.1", null, 1.0),
            ("8.8.8.8", null, 30.0),
        ], Geo, learned: Learned("8.8.8.8", "Chicago", 41.88, -87.63));

        found.GetValueOrDefault("8.8.8.8")?.Source.Should().NotBe(HopLocationSource.Learned);
    }

    [Fact]
    public void Learnable_KeepsTransitHopsPlacedByHostnameOrANearbyHop()
    {
        static GeoInfo AsnOf(string address) => new() { Asn = address.StartsWith("192.0.2.") ? 64999 : 64500 };
        var at = DateTime.UtcNow;
        (string?, string?, double?)[] hops =
        [
            ("10.0.0.1", null, 0.5),
            ("203.0.113.5", null, 5), ("203.0.113.6", null, 6), ("203.0.113.7", null, 7), ("198.51.100.8", null, 8),
            ("8.8.8.8", null, 9),
            ("192.0.2.9", null, 20), ("192.0.2.10", null, 21),
        ];
        HopLocationView At(HopLocationSource source) => new("US", "Kansas City, United States", "x", "Kansas City", 39.1, -94.58, source);
        var found = new Dictionary<string, HopLocationView>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.1"] = At(HopLocationSource.Neighbor),
            ["203.0.113.5"] = At(HopLocationSource.Hostname),
            ["203.0.113.6"] = At(HopLocationSource.Neighbor),
            ["203.0.113.7"] = At(HopLocationSource.GeoLite2),
            ["198.51.100.8"] = At(HopLocationSource.Set),
            ["8.8.8.8"] = At(HopLocationSource.Neighbor),
            // The destination's own network: a CDN edge in front of it, and the destination itself.
            ["192.0.2.9"] = At(HopLocationSource.Hostname),
            ["192.0.2.10"] = At(HopLocationSource.Hostname),
        };

        HopLocations.Learnable(hops, found, AsnOf, at).Select(h => h.Address).Should().BeEquivalentTo(["203.0.113.5", "203.0.113.6"]);
    }

    [Fact]
    public void LearnedForSite_SharesWhatSitesAgreeOn_AndKeepsADisagreementToEachSite()
    {
        var now = DateTime.UtcNow;
        LearnedHop Hop(string address, string city, HopLocationSource source = HopLocationSource.Hostname) =>
            new(address, city, "US", 0, 0, source, "", now.AddHours(-1));
        (string, LearnedHop)[] rows =
        [
            ("site-a", Hop("203.0.113.5", "Kansas City")),
            ("site-b", Hop("203.0.113.5", "Kansas City", HopLocationSource.Neighbor)),
            ("site-a", Hop("203.0.113.6", "Chicago")),
            ("site-b", Hop("203.0.113.6", "Atlanta")),
        ];

        var other = LearnedHopLocations.ForSite(rows, "site-c", now);
        other["203.0.113.5"].Source.Should().Be(HopLocationSource.Hostname);
        other.Should().NotContainKey("203.0.113.6");
        LearnedHopLocations.ForSite(rows, "site-b", now)["203.0.113.6"].City.Should().Be("Atlanta");
    }

    [Fact]
    public void LearnedReplaces_StrongerOrChangedOrStale_ButNotWeaker()
    {
        var now = DateTime.UtcNow;
        LearnedHop Hop(string city, HopLocationSource source, DateTime seen) => new("203.0.113.5", city, "US", 0, 0, source, "", seen);

        LearnedHopLocations.Replaces(Hop("Dallas", HopLocationSource.Hostname, now), Hop("Dallas", HopLocationSource.Neighbor, now), now).Should().BeTrue();
        LearnedHopLocations.Replaces(Hop("Tulsa", HopLocationSource.Neighbor, now), Hop("Dallas", HopLocationSource.Hostname, now), now).Should().BeFalse();
        LearnedHopLocations.Replaces(Hop("Tulsa", HopLocationSource.Hostname, now), Hop("Dallas", HopLocationSource.Hostname, now), now).Should().BeTrue();
        LearnedHopLocations.Replaces(Hop("Dallas", HopLocationSource.Hostname, now), Hop("Dallas", HopLocationSource.Hostname, now.AddMinutes(-5)), now).Should().BeFalse();
        LearnedHopLocations.Replaces(Hop("Tulsa", HopLocationSource.Neighbor, now), Hop("Dallas", HopLocationSource.Hostname, now.AddDays(-31)), now).Should().BeTrue();
        // An older trace of the same source does not undo what a newer one learned.
        LearnedHopLocations.Replaces(Hop("Tulsa", HopLocationSource.Hostname, now.AddDays(-3)), Hop("Dallas", HopLocationSource.Hostname, now.AddDays(-1)), now).Should().BeFalse();
    }
}
