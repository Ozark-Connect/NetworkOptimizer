using System.Text;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class VodafoneStationProviderTests
{
    private static CmPollContext Context() => new()
    {
        Id = 1,
        Name = "Cable Modem",
        Host = "192.0.2.10",
        Port = 80,
    };

    // Shape of /php/status_docsis_data.php: the channel arrays are JS variables embedded in the
    // page, not a JSON body.
    private const string DocsisPage = """
        <html><body><script type="text/javascript">
        var json_dsData = [
          {"__id":"1","ChannelID":"9","LockStatus":"ACTIVE","ChannelType":"SC-QAM","Modulation":"256QAM","Frequency":"602000000","PowerLevel":"-1.2 dBmV/1158.8 dBuV","SNRLevel":"41.8 dB"},
          {"__id":"2","ChannelID":"10","LockStatus":"ACTIVE","ChannelType":"SC-QAM","Modulation":"256QAM","Frequency":"610000000","PowerLevel":"0.8 dBmV/1160.8 dBuV","SNRLevel":"40.2 dB"},
          {"__id":"3","ChannelID":"33","LockStatus":"ACTIVE","ChannelType":"OFDM","Modulation":"","Frequency":"151000000~299000000","PowerLevel":"2.4 dBmV/1162.4 dBuV","SNRLevel":"43.0 dB"}
        ];
        var json_usData = [
          {"__id":"1","ChannelID":"1","LockStatus":"ACTIVE","ChannelType":"SC-QAM","Modulation":"64QAM","Frequency":"51000000","PowerLevel":"43.3 dBmV","SymbolRate":"5120000"},
          {"__id":"2","ChannelID":"5","LockStatus":"ACTIVE","ChannelType":"OFDMA","Modulation":"","Frequency":"29000000~45000000","PowerLevel":"44.0 dBmV","SymbolRate":"0"}
        ];
        </script></body></html>
        """;

    [Fact]
    public void ParseDocsis_ParsesDownstreamChannels()
    {
        var stats = VodafoneStationProvider.ParseDocsis(DocsisPage, Context(), "ARRIS TG3442DE");

        stats.DownstreamChannels.Should().HaveCount(3);

        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(9);
        first.LockStatus.Should().Be("Locked");
        first.Modulation.Should().Be("256QAM");
        first.Frequency.Should().Be(602_000_000);
        first.Power.Should().Be(-1.2);
        first.Snr.Should().Be(41.8);
    }

    [Fact]
    public void ParseDocsis_ParsesUpstreamChannels()
    {
        var stats = VodafoneStationProvider.ParseDocsis(DocsisPage, Context(), "ARRIS TG3442DE");

        stats.UpstreamChannels.Should().HaveCount(2);

        var first = stats.UpstreamChannels[0];
        first.ChannelId.Should().Be(1);
        first.LockStatus.Should().Be("Locked");
        first.ChannelType.Should().Be("SC-QAM");
        first.Frequency.Should().Be(51_000_000);
        first.Power.Should().Be(43.3);
        first.SymbolRate.Should().Be(5_120_000);
    }

    // OFDM/OFDMA channels report a band rather than a carrier, so the channel takes its center.
    [Fact]
    public void ParseDocsis_UsesBandCenterForOfdmChannels()
    {
        var stats = VodafoneStationProvider.ParseDocsis(DocsisPage, Context(), "ARRIS TG3442DE");

        var ofdm = stats.DownstreamChannels[2];
        ofdm.Frequency.Should().Be(225_000_000);
        ofdm.Modulation.Should().Be("OFDM");

        var ofdma = stats.UpstreamChannels[1];
        ofdma.Frequency.Should().Be(37_000_000);
        ofdma.ChannelType.Should().Be("OFDMA");
    }

    // The TG reports "ACTIVE" where every aggregate on CableModemStats counts "Locked". Without
    // the mapping the channels still parse but each aggregate silently reads zero or null.
    [Fact]
    public void ParseDocsis_MapsActiveToLockedSoAggregatesPopulate()
    {
        var stats = VodafoneStationProvider.ParseDocsis(DocsisPage, Context(), "ARRIS TG3442DE");

        stats.LockedDsChannels.Should().Be(3);
        stats.LockedUsChannels.Should().Be(2);
        stats.DownstreamPowerAvgDbmv.Should().BeApproximately(0.667, 0.001);
        stats.DownstreamSnrAvgDb.Should().BeApproximately(41.667, 0.001);
        stats.UpstreamPowerAvgDbmv.Should().BeApproximately(43.65, 0.001);
    }

    [Theory]
    [InlineData("ACTIVE", "Locked")]
    [InlineData("Locked", "Locked")]
    [InlineData("locked", "Locked")]
    [InlineData("NOT_LOCKED", "NOT_LOCKED")]
    [InlineData("", "")]
    public void NormalizeLockStatus_MapsFirmwareValues(string raw, string expected)
    {
        VodafoneStationProvider.NormalizeLockStatus(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("602000000", 602_000_000)]
    [InlineData("151000000~299000000", 225_000_000)]
    [InlineData("602", 602_000_000)]
    [InlineData("", 0)]
    [InlineData("not-a-number", 0)]
    public void ParseFrequencyHz_HandlesFirmwareFormats(string raw, long expected)
    {
        VodafoneStationProvider.ParseFrequencyHz(raw).Should().Be(expected);
    }

    // The TG pairs both units in one field; only the dBmV half is meaningful to us.
    [Theory]
    [InlineData("-1.2 dBmV/1158.8 dBuV", -1.2)]
    [InlineData("43.3 dBmV", 43.3)]
    [InlineData("0.0 dBmV/1160.0 dBuV", 0.0)]
    public void ParsePowerDbmv_TakesTheDbmvHalf(string raw, double expected)
    {
        VodafoneStationProvider.ParsePowerDbmv(raw).Should().Be(expected);
    }

    [Fact]
    public void ParsePowerDbmv_ReturnsNullWhenAbsent()
    {
        VodafoneStationProvider.ParsePowerDbmv("").Should().BeNull();
        VodafoneStationProvider.ParsePowerDbmv("n/a").Should().BeNull();
    }

    [Theory]
    [InlineData("var myIv = 'a1b2c3d4e5f60718';", "myIv", "a1b2c3d4e5f60718")]
    [InlineData("let mySalt = \"0011223344556677\";", "mySalt", "0011223344556677")]
    [InlineData("window.currentSessionId = 'abc123';", "currentSessionId", "abc123")]
    [InlineData("currentSessionId = 'bare9876';", "currentSessionId", "bare9876")]
    public void ExtractJsVar_ReadsDeclarationForms(string script, string name, string expected)
    {
        VodafoneStationProvider.ExtractJsVar(script, name).Should().Be(expected);
    }

    [Fact]
    public void ExtractJsVar_ReturnsNullWhenMissing()
    {
        VodafoneStationProvider.ExtractJsVar("var somethingElse = 'x';", "myIv").Should().BeNull();
    }

    // The firmware's pages are full of js_-prefixed variables, so a bare assignment must not be
    // read off a longer identifier that merely ends with the name being looked up.
    [Fact]
    public void ExtractJsVar_IgnoresLongerIdentifiersEndingInTheName()
    {
        var page = "var js_mySalt = 'deadbeefdeadbeef';\nmySalt = 'cafebabecafebabe';\n";

        VodafoneStationProvider.ExtractJsVar(page, "mySalt").Should().Be("cafebabecafebabe");
    }

    // The modem expects ciphertext followed by the 16-byte authentication tag as one hex string.
    // The reverse order still looks like a valid payload and is rejected only by the device.
    [Fact]
    public void AesCcmEncryptHex_AppendsTagAndRoundTrips()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var nonce = Convert.FromHexString("a1b2c3d4e5f60718");
        const string payload = """{"Password":"secret","Nonce":"session"}""";

        var encrypted = VodafoneStationProvider.AesCcmEncryptHex(
            key, nonce, Encoding.UTF8.GetBytes(payload), "loginPassword");

        encrypted.Should().HaveLength((Encoding.UTF8.GetByteCount(payload) + 16) * 2);
        encrypted.Should().MatchRegex("^[0-9a-f]+$");

        VodafoneStationProvider.AesCcmDecryptText(key, nonce, encrypted, "loginPassword")
            .Should().Be(payload);
    }

    // The credentials and the returned CSRF nonce are sealed under different associated data, so
    // mixing the two up has to fail closed.
    [Fact]
    public void AesCcmDecryptText_ReturnsNullOnAssociatedDataMismatch()
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var nonce = Convert.FromHexString("a1b2c3d4e5f60718");

        var encrypted = VodafoneStationProvider.AesCcmEncryptHex(
            key, nonce, Encoding.UTF8.GetBytes("token"), "nonce");

        VodafoneStationProvider.AesCcmDecryptText(key, nonce, encrypted, "loginPassword").Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("00112233")]
    public void AesCcmDecryptText_ReturnsNullOnMalformedInput(string encrypted)
    {
        var key = Convert.FromHexString("000102030405060708090a0b0c0d0e0f");
        var nonce = Convert.FromHexString("a1b2c3d4e5f60718");

        VodafoneStationProvider.AesCcmDecryptText(key, nonce, encrypted, "loginPassword").Should().BeNull();
    }

    [Fact]
    public void ParseDocsis_ReturnsEmptyChannelsWhenArraysAbsent()
    {
        var stats = VodafoneStationProvider.ParseDocsis("<html><body>Login</body></html>", Context(), "ARRIS TG3442DE");

        stats.DownstreamChannels.Should().BeEmpty();
        stats.UpstreamChannels.Should().BeEmpty();
        stats.DeviceModel.Should().Be("ARRIS TG3442DE");
    }

    [Fact]
    public void ParseDocsis_SkipsMalformedChannelArray()
    {
        var page = "var json_dsData = [ {\"ChannelID\": ; ];";

        var stats = VodafoneStationProvider.ParseDocsis(page, Context(), "ARRIS TG3442DE");

        stats.DownstreamChannels.Should().BeEmpty();
    }

    // Numeric fields arrive quoted on some builds and bare on others.
    [Fact]
    public void ParseDocsis_AcceptsUnquotedNumericFields()
    {
        var page = """
            var json_dsData = [
              {"ChannelID":9,"LockStatus":"ACTIVE","ChannelType":"SC-QAM","Modulation":"256QAM","Frequency":602000000,"PowerLevel":"-1.2 dBmV/1158.8 dBuV","SNRLevel":41.8}
            ];
            """;

        var stats = VodafoneStationProvider.ParseDocsis(page, Context(), "ARRIS TG3442DE");

        var channel = stats.DownstreamChannels.Should().ContainSingle().Subject;
        channel.ChannelId.Should().Be(9);
        channel.Frequency.Should().Be(602_000_000);
        channel.Snr.Should().Be(41.8);
    }

    [Fact]
    public void ParseDocsis_CarriesDeviceIdentityFromContext()
    {
        var context = Context() with { ConfiguredHost = "198.51.100.5" };

        var stats = VodafoneStationProvider.ParseDocsis(DocsisPage, context, "ARRIS TG3442DE");

        stats.DeviceHost.Should().Be("198.51.100.5");
        stats.DeviceName.Should().Be("Cable Modem");
    }
}
