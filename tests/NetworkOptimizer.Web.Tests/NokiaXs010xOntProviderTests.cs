using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class NokiaXs010xOntProviderTests
{
    // Shape captured from a live XS-010X-Q's /GponForm/getUpdateinfo response; identifying
    // serial/MAC replaced with generic placeholders per the no-PII-in-tests rule.
    private const string FullUpdateInfoJson = """
        {
          "CurrentPonPw" : "000000000000000000000000000000000000000000000000000000000000000000000000",
          "VendorID" : "ALCL",
          "VersionID" : "3FE49331AAAA01",
          "SerialNum" : "ALCLaabbccdd",
          "Mac" : "001122334455",
          "ActiveSwVer" : "3FE49337BOCK48",
          "StandbySwVer" : "3FE49337BOCK35",
          "RxOptPwr" : "-13.3"
        }
        """;

    [Fact]
    public void ApplyUpdateInfo_FullFixture_MapsAllFields()
    {
        var stats = new OntStats();

        NokiaXs010xOntProvider.ApplyUpdateInfo(FullUpdateInfoJson, stats);

        stats.RxPowerDbm.Should().BeApproximately(-13.3, 0.0001);
        stats.VendorName.Should().Be("ALCL");
        stats.VendorPn.Should().Be("3FE49331AAAA01");
        stats.VendorSn.Should().Be("ALCLaabbccdd");
        stats.PonType.Should().Be("XGS-PON");
        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Up");
        stats.TxPowerDbm.Should().BeNull();
    }

    [Fact]
    public void ApplyUpdateInfo_MissingRxPower_LeavesStatusUnknown()
    {
        var stats = new OntStats();
        var json = """{"VendorID":"ALCL","SerialNum":"ALCLaabbccdd"}""";

        NokiaXs010xOntProvider.ApplyUpdateInfo(json, stats);

        stats.RxPowerDbm.Should().BeNull();
        stats.OperationalStatus.Should().BeNull();
        stats.LinkState.Should().BeNull();
        stats.VendorName.Should().Be("ALCL");
    }

    [Fact]
    public void ApplyUpdateInfo_MalformedJson_DoesNotThrowAndLeavesDefaults()
    {
        var stats = new OntStats();

        var act = () => NokiaXs010xOntProvider.ApplyUpdateInfo("not json", stats);

        act.Should().NotThrow();
        stats.RxPowerDbm.Should().BeNull();
        stats.OperationalStatus.Should().BeNull();
    }

    [Fact]
    public void ComputeCmt_MatchesLiveDeviceDigest()
    {
        // Verified against a live unit: sha256("admin" + "ea" + "1234").
        NokiaXs010xOntProvider.ComputeCmt("admin", "ea", "1234")
            .Should().Be("b7290cb39156057010fd604590fe9c01ee72d700cf20f301a51d8fdef3f22fc7");
    }

    [Fact]
    public void ParseLoginConfig_ReturnsNonceAndSalt()
    {
        var json = """
            {"XError":0,"XStopTime":300,"XPasswdTip":" ","nonce":"AbCdEfGhIjKlMnOpQrStUvWxYz012345","saltval":"ea"}
            """;

        var (nonce, salt) = NokiaXs010xOntProvider.ParseLoginConfig(json);

        nonce.Should().Be("AbCdEfGhIjKlMnOpQrStUvWxYz012345");
        salt.Should().Be("ea");
    }

    [Fact]
    public void ParseLoginConfig_MalformedJson_ReturnsNulls()
    {
        var (nonce, salt) = NokiaXs010xOntProvider.ParseLoginConfig("not json");

        nonce.Should().BeNull();
        salt.Should().BeNull();
    }

    [Fact]
    public void ParseCookieId_SuccessResponse_ReturnsCookie()
    {
        var json = """{"login_result":"success","cookieid":"deadbeefcafe0123"}""";

        NokiaXs010xOntProvider.ParseCookieId(json).Should().Be("deadbeefcafe0123");
    }

    [Fact]
    public void ParseCookieId_ErrorResponse_ReturnsNull()
    {
        NokiaXs010xOntProvider.ParseCookieId("""{"login_result":"error"}""").Should().BeNull();
    }

    [Fact]
    public void ParseCookieId_MissingCookie_ReturnsNull()
    {
        NokiaXs010xOntProvider.ParseCookieId("""{"login_result":"success"}""").Should().BeNull();
    }
}
