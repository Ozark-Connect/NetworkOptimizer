using System.Text;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.OntProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class ZyxelGponSfpOntProviderTests
{
    // Captured verbatim from the live Zyxel PMG3000 GPON-SFP stick on 2026-07-20, with the
    // real cloned serial and cur_pass scrubbed to placeholders. These are JavaScript object
    // literals, not strict JSON: unquoted keys, whitespace after colons, mixed value types,
    // and a terminal "(ASCII)" suffix inside string values.
    private const string SnBody =
        """{cur_sn:"TW00ABCD1234(ASCII)",sn:    "TW00ABCD1234(ASCII)",cur_pass:"placeholder(ASCII)"}""";

    private const string GponInfoBody =
        """{line_status:"5",loid_status:0,up_fec:"Disable",down_fec:"Disable",encrypt:"Disable",temp:"67.85",voltage:"3.30",current:"28.89",tx_power:"3.01",rx_power:"-17.17"}""";

    // --- Mapping cases -------------------------------------------------------

    [Fact]
    public void ApplyResponses_HappyPath_MapsAllFieldsWithUnits()
    {
        var stats = new OntStats();

        var ok = ZyxelGponSfpOntProvider.ApplyResponses(SnBody, GponInfoBody, stats);

        ok.Should().BeTrue();
        stats.RxPowerDbm.Should().BeApproximately(-17.17, 0.0001);
        stats.TxPowerDbm.Should().BeApproximately(3.01, 0.0001);
        stats.TemperatureC.Should().BeApproximately(67.85, 0.0001);
        stats.VoltageV.Should().BeApproximately(3.30, 0.0001);
        stats.BiasMa.Should().BeApproximately(28.89, 0.0001);
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.OperationalStatus.Should().Be("Up");
        stats.LinkState.Should().Be("Connected (O5)");
        stats.VendorSn.Should().Be("TW00ABCD1234");
        stats.VendorName.Should().Be("Zyxel");
        stats.DeviceModel.Should().Be("Zyxel PMG3000");
        stats.PonType.Should().Be("GPON");
    }

    [Fact]
    public void ApplyResponses_NoUpFecMappedToFecErrors()
    {
        var stats = new OntStats();

        ZyxelGponSfpOntProvider.ApplyResponses(SnBody, GponInfoBody, stats);

        // up_fec/down_fec are enablement flags ("Disable"), not error counts.
        stats.FecErrors.Should().BeNull();
    }

    [Fact]
    public void ApplyResponses_MissingGetSn_StillMapsOptics()
    {
        var stats = new OntStats();

        var ok = ZyxelGponSfpOntProvider.ApplyResponses(null, GponInfoBody, stats);

        ok.Should().BeTrue();
        stats.RxPowerDbm.Should().BeApproximately(-17.17, 0.0001);
        stats.TxPowerDbm.Should().BeApproximately(3.01, 0.0001);
        stats.PonLinkStatus.Should().Be(PonLinkState.Operation);
        stats.VendorSn.Should().BeNull();
    }

    [Fact]
    public void ApplyResponses_MalformedGetSn_DoesNotDiscardOptics()
    {
        var stats = new OntStats();

        var ok = ZyxelGponSfpOntProvider.ApplyResponses("<html>login</html>", GponInfoBody, stats);

        ok.Should().BeTrue();
        stats.RxPowerDbm.Should().BeApproximately(-17.17, 0.0001);
        stats.VendorSn.Should().BeNull();
    }

    [Fact]
    public void ApplyResponses_GponInfoWithoutRecognizedField_ReturnsFalse()
    {
        var stats = new OntStats();

        // Parses fine, but contains no recognized PON status field.
        var ok = ZyxelGponSfpOntProvider.ApplyResponses(SnBody, """{loid_status:0,encrypt:"Disable"}""", stats);

        ok.Should().BeFalse();
        stats.RxPowerDbm.Should().BeNull();
        stats.VendorName.Should().BeNull();
        stats.DeviceModel.Should().Be("");
    }

    [Theory]
    [InlineData("<html><body>Login</body></html>")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not an object")]
    public void ApplyResponses_MalformedGponInfo_ReturnsFalseWithoutThrowing(string gponBody)
    {
        var stats = new OntStats();

        var act = () => ZyxelGponSfpOntProvider.ApplyResponses(SnBody, gponBody, stats);

        act.Should().NotThrow();
        ZyxelGponSfpOntProvider.ApplyResponses(SnBody, gponBody, stats).Should().BeFalse();
        stats.RxPowerDbm.Should().BeNull();
    }

    [Theory]
    [InlineData("1", PonLinkState.Initial, "Down", "Initializing (O1)")]
    [InlineData("2", PonLinkState.Standby, "Down", "Standby (O2)")]
    [InlineData("3", PonLinkState.SerialNumber, "Down", "Authenticating (O3)")]
    [InlineData("4", PonLinkState.Ranging, "Down", "Ranging (O4)")]
    [InlineData("5", PonLinkState.Operation, "Up", "Connected (O5)")]
    [InlineData("6", PonLinkState.Popup, "Down", "Signal Lost (O6)")]
    [InlineData("7", PonLinkState.EmergencyStop, "Down", "Disabled (O7)")]
    public void ApplyResponses_LineStatus_MapsToPonState(
        string lineStatus, PonLinkState expectedState, string expectedOp, string expectedLabel)
    {
        var stats = new OntStats();
        var gpon = $$"""{line_status:"{{lineStatus}}",rx_power:"-17.17"}""";

        var ok = ZyxelGponSfpOntProvider.ApplyResponses(null, gpon, stats);

        ok.Should().BeTrue();
        stats.PonLinkStatus.Should().Be(expectedState);
        stats.OperationalStatus.Should().Be(expectedOp);
        stats.LinkState.Should().Be(expectedLabel);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("51")]  // must NOT substring-match O5 -> Operation
    [InlineData("15")]  // must NOT substring-match O1 -> Initial
    [InlineData("55")]
    [InlineData("O5")]  // literal, not a bare ordinal
    [InlineData("")]
    public void ApplyResponses_LineStatusUnknown_LeavesOperationalStatusNull(string lineStatus)
    {
        var stats = new OntStats();
        var gpon = $$"""{line_status:"{{lineStatus}}",rx_power:"-17.17"}""";

        var ok = ZyxelGponSfpOntProvider.ApplyResponses(null, gpon, stats);

        ok.Should().BeTrue();
        stats.PonLinkStatus.Should().Be(PonLinkState.Unknown);
        stats.OperationalStatus.Should().BeNull();
        stats.LinkState.Should().Be("Unknown");
    }

    [Theory]
    [InlineData("51")]
    [InlineData("15")]
    [InlineData("57")]
    public void MapLineStatus_MultiDigit_IsNotMisreadAsHealthy(string lineStatus)
    {
        // Regression: substring parsing would have turned "51" into O5/Operation and reported
        // the link Up, potentially suppressing a down alert.
        ZyxelGponSfpOntProvider.MapLineStatus(lineStatus).Should().Be(PonLinkState.Unknown);
    }

    [Fact]
    public void ApplyResponses_QuotedAndBareLineStatus_BothMapEqually()
    {
        var quoted = new OntStats();
        var bare = new OntStats();

        ZyxelGponSfpOntProvider.ApplyResponses(null, """{line_status:"5",rx_power:"-17.17"}""", quoted);
        ZyxelGponSfpOntProvider.ApplyResponses(null, """{line_status:5,rx_power:-17.17}""", bare);

        quoted.PonLinkStatus.Should().Be(PonLinkState.Operation);
        bare.PonLinkStatus.Should().Be(PonLinkState.Operation);
        bare.RxPowerDbm.Should().BeApproximately(-17.17, 0.0001);
    }

    // --- Serial handling -----------------------------------------------------

    [Fact]
    public void ApplyResponses_PrefersCurSnOverSn()
    {
        var stats = new OntStats();
        var sn = """{cur_sn:"AAAA1111(ASCII)",sn:"BBBB2222(ASCII)"}""";

        ZyxelGponSfpOntProvider.ApplyResponses(sn, GponInfoBody, stats);

        stats.VendorSn.Should().Be("AAAA1111");
    }

    [Fact]
    public void ApplyResponses_FallsBackToSnWhenCurSnMissing()
    {
        var stats = new OntStats();
        var sn = """{sn:"BBBB2222(ASCII)",cur_pass:"placeholder(ASCII)"}""";

        ZyxelGponSfpOntProvider.ApplyResponses(sn, GponInfoBody, stats);

        stats.VendorSn.Should().Be("BBBB2222");
    }

    [Fact]
    public void ApplyResponses_StripsAsciiSuffixOnlyWhenTerminal()
    {
        var stats = new OntStats();
        // "(ASCII)" appears mid-value, so it must be preserved.
        var sn = """{cur_sn:"AB(ASCII)CD"}""";

        ZyxelGponSfpOntProvider.ApplyResponses(sn, GponInfoBody, stats);

        stats.VendorSn.Should().Be("AB(ASCII)CD");
    }

    // --- Parser cases --------------------------------------------------------

    [Fact]
    public void TryParseObjectLiteral_SnKeyNotMatchedInsideCurSn()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral(SnBody, out var fields).Should().BeTrue();

        fields.Should().ContainKey("cur_sn");
        fields.Should().ContainKey("sn");
        fields["cur_sn"].Should().Be("TW00ABCD1234(ASCII)");
        fields["sn"].Should().Be("TW00ABCD1234(ASCII)");
    }

    [Fact]
    public void TryParseObjectLiteral_KeyLikeStringInsideValueIsNotAKey()
    {
        // A value that itself looks like "key:value" must stay a single value.
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{note:"sn:should_not_be_key",rx_power:"-17.17"}""", out var fields)
            .Should().BeTrue();

        fields["note"].Should().Be("sn:should_not_be_key");
        fields.Should().NotContainKey("should_not_be_key");
        fields["rx_power"].Should().Be("-17.17");
    }

    [Fact]
    public void TryParseObjectLiteral_WhitespaceAfterColon()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{sn:    "value"}""", out var fields).Should().BeTrue();

        fields["sn"].Should().Be("value");
    }

    [Fact]
    public void TryParseObjectLiteral_LeadingWhitespaceAndBom()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("﻿  \n {a:1}", out var fields).Should().BeTrue();

        fields["a"].Should().Be("1");
    }

    [Fact]
    public void TryParseObjectLiteral_QuotedKeys()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{"line_status":"5","rx_power":"-17.17"}""", out var fields)
            .Should().BeTrue();

        fields["line_status"].Should().Be("5");
        fields["rx_power"].Should().Be("-17.17");
    }

    [Fact]
    public void TryParseObjectLiteral_SingleQuotedValues()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{a:'hello',b:'wo,rld'}""", out var fields).Should().BeTrue();

        fields["a"].Should().Be("hello");
        fields["b"].Should().Be("wo,rld");
    }

    [Theory]
    [InlineData("""{v:-17.17}""", "-17.17")]
    [InlineData("""{v:3.30}""", "3.30")]
    [InlineData("""{v:1e3}""", "1e3")]
    [InlineData("""{v:0}""", "0")]
    public void TryParseObjectLiteral_BareNumbers(string body, string expected)
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral(body, out var fields).Should().BeTrue();

        fields["v"].Should().Be(expected);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void ApplyResponses_NonFiniteNumbers_RejectedAsNull(string value)
    {
        var stats = new OntStats();
        var gpon = $$"""{line_status:"5",rx_power:{{value}}}""";

        var ok = ZyxelGponSfpOntProvider.ApplyResponses(null, gpon, stats);

        ok.Should().BeTrue(); // line_status keeps it a recognized response
        stats.RxPowerDbm.Should().BeNull();
    }

    [Fact]
    public void ApplyResponses_ExponentAndNegativeNumbersParse()
    {
        var stats = new OntStats();

        ZyxelGponSfpOntProvider.ApplyResponses(null, """{line_status:"5",rx_power:-1.5e1,tx_power:2}""", stats);

        stats.RxPowerDbm.Should().BeApproximately(-15.0, 0.0001);
        stats.TxPowerDbm.Should().BeApproximately(2.0, 0.0001);
    }

    [Fact]
    public void TryParseObjectLiteral_ValuesWithCommasColonsAndEscapes()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral(
            """{a:"x,y:z",b:"quote\"here",c:"back\\slash"}""", out var fields).Should().BeTrue();

        fields["a"].Should().Be("x,y:z");
        fields["b"].Should().Be("quote\"here");
        fields["c"].Should().Be("back\\slash");
    }

    [Fact]
    public void TryParseObjectLiteral_TrailingComma()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{a:1,b:2,}""", out var fields).Should().BeTrue();

        fields["a"].Should().Be("1");
        fields["b"].Should().Be("2");
    }

    [Fact]
    public void TryParseObjectLiteral_EmptyObject()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("{}", out var fields).Should().BeTrue();

        fields.Should().BeEmpty();
    }

    [Fact]
    public void TryParseObjectLiteral_UnknownFieldsAndNullAndBool()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{x:null,y:true,z:false}""", out var fields).Should().BeTrue();

        fields["x"].Should().Be("null");
        fields["y"].Should().Be("true");
        fields["z"].Should().Be("false");
    }

    [Fact]
    public void TryParseObjectLiteral_DuplicateKeysLastWins()
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral("""{a:1,a:2,a:3}""", out var fields).Should().BeTrue();

        fields["a"].Should().Be("3");
    }

    [Theory]
    [InlineData("""{a 1}""")]          // missing colon
    [InlineData("""{a:"unterminated}""")] // unterminated quote
    [InlineData("""{a:1""")]            // missing closing brace
    [InlineData("""{a:1}extra""")]      // trailing content
    [InlineData("not an object")]      // no opening brace
    [InlineData("")]                    // empty
    public void TryParseObjectLiteral_MalformedInput_ReturnsFalse(string body)
    {
        ZyxelGponSfpOntProvider.TryParseObjectLiteral(body, out var fields).Should().BeFalse();
        fields.Should().BeEmpty();
    }

    // --- Client / auth -------------------------------------------------------

    [Fact]
    public void CreateClient_SetsPreemptiveBasicAuthFromContext()
    {
        var context = new OntPollContext
        {
            Id = 1,
            Name = "Zyxel",
            Host = "10.10.1.1",
            Username = "user",
            Password = "secret",
        };

        using var client = ZyxelGponSfpOntProvider.CreateClient(context);

        client.DefaultRequestHeaders.Authorization.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization!.Scheme.Should().Be("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(client.DefaultRequestHeaders.Authorization.Parameter!))
            .Should().Be("user:secret");
        client.Timeout.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CreateClient_DefaultsToAdmin1234WhenBlank()
    {
        var context = new OntPollContext
        {
            Id = 1,
            Name = "Zyxel",
            Host = "10.10.1.1",
            Username = "",
            Password = "",
        };

        using var client = ZyxelGponSfpOntProvider.CreateClient(context);

        Encoding.UTF8.GetString(Convert.FromBase64String(client.DefaultRequestHeaders.Authorization!.Parameter!))
            .Should().Be("admin:1234");
    }
}
