using FluentAssertions;
using NetworkOptimizer.Core.Helpers;
using Xunit;

namespace NetworkOptimizer.Core.Tests;

/// <summary>
/// Router hostnames as transit operators publish them, taken from real traces. Each operator has
/// its own scheme, so each gets a case.
/// </summary>
public class RouterHostnameLocatorTests
{
    private static readonly (double, double) Dallas = (32.78, -96.80);
    private static readonly (double, double) SaltLakeCity = (40.76, -111.89);

    [Theory]
    // NTT: CLLI place code with a site number
    [InlineData("ae-20.r22.dllstx14.us.bb.gin.ntt.net", "Dallas")]
    [InlineData("ae-0.r26.asbnva02.us.bb.gin.ntt.net", "Ashburn")]
    [InlineData("ae-22.a03.lsanca07.us.bb.gin.ntt.net", "Los Angeles")]
    [InlineData("ae-3.r23.parsfr04.fr.bb.gin.ntt.net", "Paris")]
    [InlineData("ae-5.r29.osakjp02.jp.bb.gin.ntt.net", "Osaka")]
    // Lumen and Level 3: city name with a site number
    [InlineData("ae1.3511.edge2.dallas1.net.lumen.tech", "Dallas")]
    [InlineData("ae1.3503.ear2.losangeles1.net.lumen.tech", "Los Angeles")]
    [InlineData("ae3.3.bar3.saltlakecity1.net.lumen.tech", "Salt Lake City")]
    [InlineData("ae8.edge4.atl2.sp.lumen.tech", "Atlanta")]
    [InlineData("lag-101.ear1.littlerock2.level3.net", "Little Rock")]
    [InlineData("xe-10-0-5-0.bar1.saltlakecity1.level3.net", "Salt Lake City")]
    // Arelion (Twelve99): an operator code before the router role
    [InlineData("ash-bb2-link.ip.twelve99.net", "Ashburn")]
    [InlineData("kanc-b2-link.ip.twelve99.net", "Kansas City")]
    [InlineData("ldn-b3-link.ip.twelve99.net", "London")]
    [InlineData("slou-b2-link.ip.twelve99.net", "Slough")]
    [InlineData("chi-bb2-link.ip.twelve99.net", "Chicago")]
    // Cogent: airport code with a site number
    [InlineData("be2932.ccr42.lax01.atlas.cogentco.com", "Los Angeles")]
    [InlineData("port-channel3704.ccr92.jan02.atlas.cogentco.com", "Jackson")]
    [InlineData("be2915.ccr81.tyo01.atlas.cogentco.com", "Tokyo")]
    [InlineData("be4130.ccr71.gum01.atlas.cogentco.com", "Guam")]
    // Comcast: city name or facility, then the state
    [InlineData("be-501-arsc1.saltlakecity.ut.utah.comcast.net", "Salt Lake City")]
    [InlineData("po-1-xar01.ogden.ut.utah.comcast.net", "Ogden")]
    [InlineData("be-1114-cs01.champa.co.ibone.comcast.net", "Denver")]
    [InlineData("be-203-pe11.56marietta.ga.ibone.comcast.net", "Atlanta")]
    // Uniti: CLLI split by a dash
    [InlineData("ae1.cr01.dlls02-tx.us.uniti.net", "Dallas")]
    [InlineData("ae6-0.agr01.ltrk01-ar.us.uniti.net", "Little Rock")]
    // Akamai: the city's code wins over the network tag after it, which is also an airport code
    [InlineData("ae1.r21.atl01.icn.netarch.akamai.com", "Atlanta")]
    [InlineData("ae34.r02.border101.ord02.fab.netarch.akamai.com", "Chicago")]
    [InlineData("dls-bb4-link.ip.twelve99.net", "Dallas")]
    [InlineData("nyk-bb5-link.ip.twelve99.net", "New York")]
    [InlineData("adm-bb1-link.ip.twelve99.net", "Amsterdam")]
    [InlineData("kbn-bb6-link.ip.twelve99.net", "Copenhagen")]
    [InlineData("stlo-b5-link.ip.twelve99.net", "St. Louis")]
    [InlineData("rest-bb1-link.ip.twelve99.net", "Reston")]
    // Cox: its own site code, then role, vendor, and number
    [InlineData("kscybbrj01-ae2.rd.ks.cox.net", "Kansas City")]
    [InlineData("dalsbprj01-ae2.rd.dl.cox.net", "Dallas")]
    [InlineData("dllsdsrj01-so000.rd.dl.cox.net", "Dallas")]
    [InlineData("chgobbrj02-ae10.rd.ch.cox.net", "Chicago")]
    [InlineData("LANGBBRJ01-ae0.rd.la.cox.net", "Los Angeles")]
    // IIJ: site code, site number, router role
    [InlineData("tky001bb01.iij.net", "Tokyo")]
    [InlineData("osk004ip57.iij.net", "Osaka")]
    // GTT, Linode, Tata
    [InlineData("ae3.cr3-was1.ip4.gtt.net", "Washington")]
    [InlineData("lo0-0.gw1.rin1.us.linode.com", "Dallas")]
    [InlineData("if-bundle-3-2.qcore1.hnn-haarlem.as6453.net", "Haarlem")]
    // Equinix data centers, and Comcast's Dallas Infomart
    [InlineData("eqix-ch1.opendns.com", "Chicago")]
    [InlineData("eqix-da3.example.com", "Dallas")]
    [InlineData("eqix.da4.example.com", "Dallas")]
    [InlineData("be-105-pe12.1950stemmons.tx.ibone.comcast.net", "Dallas")]
    // An exchange whose own domain is the location
    [InlineData("cloudflare.slix.net", "Salt Lake City")]
    // Others
    [InlineData("108-232-152-1.lightspeed.tukrga.sbcglobal.net", "Tucker")]
    [InlineData("lo0.mar2.slc.xmission.net", "Salt Lake City")]
    [InlineData("peer1.rtr1.dfw.opendns.com", "Dallas")]
    [InlineData("indatel-agg.ord.glcom.net", "Chicago")]
    public void Locate_KnownSchemes_NameTheCity(string hostname, string city)
    {
        RouterHostnameLocator.Locate(hostname)?.City.Name.Should().Be(city);
        RouterHostnameLocator.Locate(hostname).Should().NotBeNull();
    }

    [Theory]
    [InlineData("one.one.one.one")]
    [InlineData("dns.google")]
    [InlineData("br1.core.xmission.net")]
    [InlineData("ec2-3-218-180-192.compute-1.amazonaws.com")]
    [InlineData("dynamodb.us-east-2.amazonaws.com")]
    [InlineData("h121.217.134.40.static.ip.windstream.net")]
    [InlineData("a23-205-167-239.deploy.static.akamaitechnologies.com")]
    [InlineData("apple.as714.any2ix.coresite.com")]
    [InlineData("tulsbbrj02-ae0.rd.tu.cox.net")] // a Cox site outside the city list
    [InlineData("speedtest.rd.ks.cox.net")]
    [InlineData("dallas.net")]
    [InlineData("")]
    [InlineData(null)]
    public void Locate_NoLocationToken_ReturnsNull(string? hostname)
    {
        RouterHostnameLocator.Locate(hostname).Should().BeNull();
    }

    [Theory]
    // A CLLI code derived from GeoNames, not in the curated list: Uniti's city code, site number, and state.
    [InlineData("ae5-0.agr01.lncl01-ne.us.uniti.net", "Lincoln")]
    [InlineData("ae6-0.agr01.cmrc01-ga.us.uniti.net", "Commerce")]
    // A large airport from OurAirports, for any operator.
    [InlineData("ae1.cr1.ist02.example.net", "Istanbul")]
    // A medium airport, for an operator known to name routers by airport code (Microsoft).
    [InlineData("be6.owr01.cys13.ntwk.msn.net", "Cheyenne")]
    public void Locate_GeneratedCodes_NameTheCity(string hostname, string city)
    {
        RouterHostnameLocator.Locate(hostname)!.City.Name.Should().Be(city);
    }

    [Theory]
    // A medium airport needs an operator known to use airport codes.
    [InlineData("be6.owr01.cys13.example.net")]
    // Router words that are also airport codes: Cogent's core router (Concord), aggregation (Agra).
    [InlineData("be2093.ccr21.example.net")]
    [InlineData("ae1.agr02.example.net")]
    public void Locate_GeneratedCodesTheGatesLeaveOut_ReturnNull(string hostname)
    {
        RouterHostnameLocator.Locate(hostname).Should().BeNull();
    }

    [Fact]
    public void Locate_TheOperatorsOwnDomainIsNeverRead()
    {
        // "chicago" here is the operator's name, not where the router sits.
        RouterHostnameLocator.Locate("ae1.chicago-isp.net").Should().BeNull();
    }

    [Fact]
    public void Locate_ACityTooFarForTheRoundTrip_IsRuledOut()
    {
        // "maa" is Chennai's airport code; a hop 16 ms from Dallas cannot be in India.
        const string host = "lo1.bharent-maa3.netarch.akamai.com";
        RouterHostnameLocator.Locate(host)!.City.Name.Should().Be("Chennai");
        RouterHostnameLocator.Locate(host, 16.6, Dallas).Should().BeNull();
    }

    [Fact]
    public void Locate_AFarCityWithAMatchingRoundTrip_IsKept()
    {
        var osaka = RouterHostnameLocator.Locate("ae-5.r29.osakjp02.jp.bb.gin.ntt.net", 151.6, SaltLakeCity);
        osaka!.City.Name.Should().Be("Osaka");
    }

    [Fact]
    public void Locate_TheRoundTripLeavesOneOfTwoCities()
    {
        // Both tokens are bare codes. 3 ms from Dallas reaches Dallas, not Chicago.
        RouterHostnameLocator.Locate("dfw-ord.example.net").Should().BeNull();
        RouterHostnameLocator.Locate("dfw-ord.example.net", 3, Dallas)!.City.Name.Should().Be("Dallas");
    }

    [Fact]
    public void Locate_ARegionLabelThatDisagrees_RulesTheNameOut()
    {
        RouterHostnameLocator.Locate("cr1.portland.me.example.net").Should().BeNull();
        RouterHostnameLocator.Locate("cr1.portland.or.example.net")!.City.Name.Should().Be("Portland");
    }

    [Fact]
    public void Locate_ClliBeatsABareCode()
    {
        var found = RouterHostnameLocator.Locate("ash.r1.dllstx09.example.net");
        found!.City.Name.Should().Be("Dallas");
        found.Match.Should().Be(RouterLocationMatch.Exact);
    }

    [Theory]
    [InlineData("ae-20.r22.dllstx14.us.bb.gin.ntt.net", "dllstx14", RouterLocationMatch.Exact)]
    [InlineData("be2932.ccr42.lax01.atlas.cogentco.com", "lax01", RouterLocationMatch.NumberedCode)]
    [InlineData("ash-bb2-link.ip.twelve99.net", "ash", RouterLocationMatch.Code)]
    [InlineData("ae1.3511.edge2.dallas1.net.lumen.tech", "dallas1", RouterLocationMatch.Name)]
    public void Locate_ReportsTheTokenAndHowItMatched(string hostname, string token, RouterLocationMatch match)
    {
        var found = RouterHostnameLocator.Locate(hostname)!;
        found.Token.Should().Be(token);
        found.Match.Should().Be(match);
    }

    [Fact]
    public void Locate_IsCaseInsensitiveAndIgnoresATrailingDot()
    {
        RouterHostnameLocator.Locate("AE-20.R22.DLLSTX14.US.BB.GIN.NTT.NET.")!.City.Name.Should().Be("Dallas");
    }

    [Fact]
    public void IsWithinReach_UsesLightInFiberPlusSlack()
    {
        var atlanta = RouterLocationCities.All.Single(c => c.Name == "Atlanta");
        // Dallas to Atlanta is about 1,160 km: out of reach at 6 ms (1,100 km), in reach at 7 ms.
        RouterHostnameLocator.IsWithinReach(atlanta, 6, Dallas).Should().BeFalse();
        RouterHostnameLocator.IsWithinReach(atlanta, 7, Dallas).Should().BeTrue();
    }

    [Fact]
    public void Cities_TokensAreWellFormed()
    {
        foreach (var c in RouterLocationCities.All)
        {
            c.Latitude.Should().BeInRange(-90, 90, c.Name);
            c.Longitude.Should().BeInRange(-180, 180, c.Name);
            c.CountryCode.Should().MatchRegex("^[A-Z]{2}$", c.Name);
            c.Codes.Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^[a-z]{3,4}$"), c.Name);
            c.Clli.Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^[a-z]{6}$"), c.Name);
            c.Names.Should().OnlyContain(t => System.Text.RegularExpressions.Regex.IsMatch(t, "^[0-9]*[a-z]{4,}$"), c.Name);
        }
    }

    [Fact]
    public void Cities_NoTokenNamesTwoCities()
    {
        var tokens = RouterLocationCities.All
            .SelectMany(c => c.Codes.Concat(c.Clli).Concat(c.Names).Select(t => (Token: t, c.Name)))
            .GroupBy(x => x.Token)
            .Where(g => g.Select(x => x.Name).Distinct().Count() > 1)
            .Select(g => g.Key);
        tokens.Should().BeEmpty();
    }

    [Fact]
    public void Cities_EveryExchangeDomainNamesAListedCity()
    {
        RouterLocationCities.ExchangeDomains.Values.Concat(RouterLocationCities.EquinixMetros.Values)
            .Concat(RouterLocationCities.Operators.SelectMany(o => o.OwnCodes?.Values ?? []))
            .Should().OnlyContain(name => RouterLocationCities.All.Any(c => c.Name == name));
    }

    [Theory]
    // Arelion writes its own site codes, not airport codes.
    [InlineData("san-b2-link.ip.twelve99.net", "San Antonio")]
    [InlineData("nash-bb3-link.ip.twelve99.net", "Nashville")]
    [InlineData("omha-b3-link.ip.twelve99.net", "Omaha")]
    [InlineData("palo-bb4-link.ip.twelve99.net", "Palo Alto")]
    // Operators' own codes among their airport codes or city names.
    [InlineData("ae3-300.cr4-kan1.ip4.gtt.net", "Kansas City")]
    [InlineData("6939-3356-kac.sp.lumen.tech", "Kansas City")]
    [InlineData("lo0-0.gw1.rin1.us.linode.com", "Dallas")]
    // Comcast's facility and suburb names, Liberty Global's site codes, Zayo's and Akamai's airport codes.
    [InlineData("be-100-pe11.350ecermak.il.ibone.comcast.net", "Chicago")]
    [InlineData("be-302-cr12.1601milehigh.co.ibone.comcast.net", "Denver")]
    [InlineData("be-301-cr21.doraville.ga.ibone.comcast.net", "Atlanta")]
    [InlineData("us-chi01a-ri3-ae-10-0.aorta.net", "Chicago")]
    [InlineData("ae13.mpr1.huf2.us.zip.zayo.com", "Terre Haute")]
    [InlineData("ae3.r01.cwb01.icn.netarch.akamai.com", "Curitiba")]
    [InlineData("if-bundle-7-2.qcore1.esv00-santaclara.as6453.net", "Santa Clara")]
    [InlineData("hbg-bb2-link.ip.twelve99.net", "Hamburg")]
    [InlineData("hnk-b4-link.ip.twelve99.net", "Hong Kong")]
    [InlineData("be11.ibr02.cpt20.ntwk.msn.net", "Cape Town")]
    [InlineData("sk-bts04a-rd1-ae-7-0.aorta.net", "Bratislava")]
    public void Locate_AKnownOperator_IsReadWithItsOwnScheme(string hostname, string city)
    {
        RouterHostnameLocator.Locate(hostname)!.City.Name.Should().Be(city);
    }

    [Theory]
    // A known operator's names are read with its schemes alone: no airport code in Arelion's names,
    // no city name in NTT's, and none of Akamai's network tags.
    [InlineData("chg-bb1-link.ip.twelve99.net")]
    [InlineData("ae-1.dallas.example.ntt.net")]
    [InlineData("ae1.r01.icn.netarch.akamai.com")]
    // A country label that names another country rules a code's city out.
    [InlineData("be21002.rb-dca-3.za.example.net")]
    public void Locate_AKnownOperator_IgnoresOtherSchemes(string hostname)
    {
        RouterHostnameLocator.Locate(hostname).Should().BeNull();
    }

    [Fact]
    public void Operators_DomainsAreUniqueAndOwnCodesWellFormed()
    {
        RouterLocationCities.Operators.SelectMany(o => o.Domains).Should().OnlyHaveUniqueItems();
        foreach (var op in RouterLocationCities.Operators)
            (op.OwnCodes?.Keys ?? []).Should().OnlyContain(k => System.Text.RegularExpressions.Regex.IsMatch(k, "^[a-z]{3,4}$"), op.Name);
    }

    [Theory]
    // Router role and interface words that must never read as a city.
    [InlineData("core")] [InlineData("edge")] [InlineData("agg")] [InlineData("bar")] [InlineData("ear")]
    [InlineData("ebr")] [InlineData("ccr")] [InlineData("man")] [InlineData("lag")] [InlineData("sat")]
    [InlineData("san")] [InlineData("van")] [InlineData("tor")] [InlineData("mag")] [InlineData("ien")]
    [InlineData("fab")] [InlineData("gin")] [InlineData("net")] [InlineData("link")] [InlineData("peer")]
    public void Cities_RouterWordsAreNotCodes(string word)
    {
        RouterLocationCities.All.Should().NotContain(c => c.Codes.Contains(word) || c.Names.Contains(word), word);
    }
}
