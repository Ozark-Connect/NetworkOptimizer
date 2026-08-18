using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.CableModemProviders;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class TechnicolorCgaProviderTests
{
    private static CmPollContext Context() => new()
    {
        Id = 1,
        Name = "Cable Modem",
        Host = "192.0.2.10",
        Port = 80,
    };

    // Shape of /api/v1/sta_docsis_status: SC-QAM and OFDM channels arrive in separate arrays
    // under "data", each with its own field naming.
    private const string DocsisPayload = """
        {
          "error": "ok",
          "data": {
            "downstream": [
              {"channelid":"1","locked":"Locked","ChannelType":"SC-QAM","FFT":"256QAM","CentralFrequency":"602000000","power":"1.7","SNR":"37.0"},
              {"channelid":"2","locked":"Locked","ChannelType":"SC-QAM","FFT":"256QAM","CentralFrequency":"610000000","power":"1.4","SNR":"37.0"}
            ],
            "ofdm_downstream": [
              {"channelid_ofdm":"162","locked":"Locked","FFT_ofdm":"","CentralFrequency_ofdm":"225000000","power_ofdm":"5.8","SNR_ofdm":"41.0"}
            ],
            "upstream": [
              {"channelidup":"1","locked":"Locked","ChannelType":"SC-QAM","FFT":"32QAM","CentralFrequency":"51000000","power":"43.3","SymbolRate":"5120000"}
            ],
            "ofdma_upstream": [
              {"channelidup":"9","locked":"Locked","ChannelType":"","FFT":"","CentralFrequency":"37000000","power":"44.0"}
            ]
          }
        }
        """;

    private static JsonElement Payload(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void ParseDocsis_ParsesScQamDownstream()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(DocsisPayload), Context(), "Technicolor CGA");

        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(1);
        first.LockStatus.Should().Be("Locked");
        first.Modulation.Should().Be("256QAM");
        first.Frequency.Should().Be(602_000_000);
        first.Power.Should().Be(1.7);
        first.Snr.Should().Be(37.0);
    }

    // OFDM channels use suffixed field names and are appended to the same downstream list.
    [Fact]
    public void ParseDocsis_ParsesOfdmDownstreamWithSuffixedFields()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(DocsisPayload), Context(), "Technicolor CGA");

        stats.DownstreamChannels.Should().HaveCount(3);

        var ofdm = stats.DownstreamChannels[2];
        ofdm.ChannelId.Should().Be(162);
        ofdm.Modulation.Should().Be("OFDM");
        ofdm.Frequency.Should().Be(225_000_000);
        ofdm.Power.Should().Be(5.8);
        ofdm.Snr.Should().Be(41.0);
    }

    [Fact]
    public void ParseDocsis_ParsesUpstreamChannels()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(DocsisPayload), Context(), "Technicolor CGA");

        stats.UpstreamChannels.Should().HaveCount(2);

        var scqam = stats.UpstreamChannels[0];
        scqam.ChannelId.Should().Be(1);
        scqam.ChannelType.Should().Be("SC-QAM");
        scqam.Frequency.Should().Be(51_000_000);
        scqam.Power.Should().Be(43.3);
        scqam.SymbolRate.Should().Be(5_120_000);

        // OFDMA channels carry no channel type of their own.
        stats.UpstreamChannels[1].ChannelType.Should().Be("OFDMA");
    }

    // Every aggregate on CableModemStats counts "Locked", so the lock vocabulary has to be
    // normalized or the channels parse while all the aggregates read zero or null.
    [Fact]
    public void ParseDocsis_PopulatesAggregates()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(DocsisPayload), Context(), "Technicolor CGA");

        stats.LockedDsChannels.Should().Be(3);
        stats.LockedUsChannels.Should().Be(2);
        stats.DownstreamPowerAvgDbmv.Should().BeApproximately(2.967, 0.001);
        stats.DownstreamSnrAvgDb.Should().BeApproximately(38.333, 0.001);
        stats.UpstreamPowerAvgDbmv.Should().BeApproximately(43.65, 0.001);
    }

    [Theory]
    [InlineData("Locked", "Locked")]
    [InlineData("locked", "Locked")]
    [InlineData("ACTIVE", "Locked")]
    [InlineData("YES", "Locked")]
    [InlineData("1", "Locked")]
    [InlineData("true", "Locked")]
    [InlineData("Not Locked", "Not Locked")]
    [InlineData("", "")]
    public void NormalizeLockStatus_MapsFirmwareValues(string raw, string expected)
    {
        TechnicolorCgaProvider.NormalizeLockStatus(raw).Should().Be(expected);
    }

    [Theory]
    [InlineData("602000000", 602_000_000)]
    [InlineData("602", 602_000_000)]
    [InlineData("", 0)]
    [InlineData("n/a", 0)]
    public void ParseFrequencyHz_HandlesFirmwareFormats(string raw, long expected)
    {
        TechnicolorCgaProvider.ParseFrequencyHz(raw).Should().Be(expected);
    }

    // Some builds report MER as a negative MSE.
    [Theory]
    [InlineData("37.0", 37.0)]
    [InlineData("-37.0", 37.0)]
    public void ParseSnr_NormalizesSign(string raw, double expected)
    {
        TechnicolorCgaProvider.ParseSnr(raw).Should().Be(expected);
    }

    [Fact]
    public void ParseSnr_ReturnsNullWhenAbsent()
    {
        TechnicolorCgaProvider.ParseSnr("").Should().BeNull();
    }

    // Older builds return the channel arrays at the root rather than under "data".
    [Fact]
    public void ParseDocsis_AcceptsUnwrappedPayload()
    {
        var payload = """
            {"downstream":[{"channelid":"1","locked":"Locked","FFT":"256QAM","CentralFrequency":"602000000","power":"1.7","SNR":"37.0"}]}
            """;

        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(payload), Context(), "Technicolor CGA");

        stats.DownstreamChannels.Should().ContainSingle();
        stats.DownstreamChannels[0].Frequency.Should().Be(602_000_000);
    }

    // Numeric fields arrive quoted on some builds and bare on others.
    [Fact]
    public void ParseDocsis_AcceptsUnquotedNumericFields()
    {
        var payload = """
            {"data":{"downstream":[{"channelid":1,"locked":"Locked","FFT":"256QAM","CentralFrequency":602000000,"power":1.7,"SNR":37.0}]}}
            """;

        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(payload), Context(), "Technicolor CGA");

        var channel = stats.DownstreamChannels.Should().ContainSingle().Subject;
        channel.ChannelId.Should().Be(1);
        channel.Frequency.Should().Be(602_000_000);
        channel.Power.Should().Be(1.7);
        channel.Snr.Should().Be(37.0);
    }

    [Fact]
    public void ParseDocsis_ReturnsEmptyChannelsWhenArraysAbsent()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload("""{"error":"ok","data":{}}"""), Context(), "Technicolor CGA");

        stats.DownstreamChannels.Should().BeEmpty();
        stats.UpstreamChannels.Should().BeEmpty();
    }

    [Fact]
    public void ParseDocsis_CarriesDeviceIdentityFromContext()
    {
        var context = Context() with { ConfiguredHost = "198.51.100.5" };

        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(DocsisPayload), context, "Technicolor CGA437A");

        stats.DeviceHost.Should().Be("198.51.100.5");
        stats.DeviceName.Should().Be("Cable Modem");
        stats.DeviceModel.Should().Be("Technicolor CGA437A");
    }

    // --- CGA4233 VOO firmware shape: /api/v1/modem/exUSTbl,exDSTbl,USTbl,DSTbl,ErrTbl ---

    // Reproduces the actual response from a CGA4233VOO captured in a HAR trace. Uses
    // DSTbl/exDSTbl/USTbl/exUSTbl array names and ChannelID/Frequency/PowerLevel/SNRLevel
    // field names with unit suffixes.
    private const string Cga4233Payload = """
        {
          "error": "ok",
          "data": {
            "exUSTbl": [],
            "exDSTbl": [
              {"__id":"1","ChannelID":"162","CentralFrequency":"190.487503 MHz","PowerLevel":"6.6 dBmV","SNRLevel":"41.3 dB","FFT":"","LockStatus":"Locked","ChannelType":"OFDM"}
            ],
            "USTbl": [
              {"__id":"1","ChannelID":"1","Frequency":"50.7 MHz","PowerLevel":"42.3 dBmV","ChannelType":"SC-QAM","SymbolRate":"5120000","Modulation":"32-qam","LockStatus":"Locked"},
              {"__id":"2","ChannelID":"2","Frequency":"57.2 MHz","PowerLevel":"42.3 dBmV","ChannelType":"SC-QAM","SymbolRate":"5120000","Modulation":"32-qam","LockStatus":"Locked"}
            ],
            "DSTbl": [
              {"__id":"1","ChannelID":"4","Frequency":"474 MHz","PowerLevel":"2.1 dBmV","SNRLevel":"37.1 dB","Modulation":"256-QAM","Correcteds":"507162","Uncorrectables":"13099","LockStatus":"Locked","ChannelType":"SC-QAM"},
              {"__id":"2","ChannelID":"1","Frequency":"450 MHz","PowerLevel":"2.5 dBmV","SNRLevel":"36.9 dB","Modulation":"256-QAM","Correcteds":"547780","Uncorrectables":"5943","LockStatus":"Locked","ChannelType":"SC-QAM"}
            ],
            "ErrTbl": []
          }
        }
        """;

    [Fact]
    public void ParseDocsis_Cga4233_ParsesDownstreamFromDSTbl()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(Cga4233Payload), Context(), "Technicolor CGA4233VOO");

        stats.DownstreamChannels.Should().HaveCount(3);

        var first = stats.DownstreamChannels[0];
        first.ChannelId.Should().Be(4);
        first.LockStatus.Should().Be("Locked");
        first.Modulation.Should().Be("256-QAM");
        first.Frequency.Should().Be(474_000_000);
        first.Power.Should().Be(2.1);
        first.Snr.Should().Be(37.1);
        first.Correctables.Should().Be(507162);
        first.Uncorrectables.Should().Be(13099);
    }

    [Fact]
    public void ParseDocsis_Cga4233_ParsesOfdmFromExDSTbl()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(Cga4233Payload), Context(), "Technicolor CGA4233VOO");

        var ofdm = stats.DownstreamChannels[2];
        ofdm.ChannelId.Should().Be(162);
        ofdm.Modulation.Should().Be("OFDM");
        ofdm.Frequency.Should().Be(190_487_503);
        ofdm.Power.Should().Be(6.6);
        ofdm.Snr.Should().Be(41.3);
    }

    [Fact]
    public void ParseDocsis_Cga4233_ParsesUpstreamFromUSTbl()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(Cga4233Payload), Context(), "Technicolor CGA4233VOO");

        stats.UpstreamChannels.Should().HaveCount(2);

        var first = stats.UpstreamChannels[0];
        first.ChannelId.Should().Be(1);
        first.ChannelType.Should().Be("SC-QAM");
        first.Frequency.Should().Be(50_700_000);
        first.Power.Should().Be(42.3);
        first.SymbolRate.Should().Be(5_120_000);
    }

    [Fact]
    public void ParseDocsis_Cga4233_PopulatesAggregates()
    {
        var stats = TechnicolorCgaProvider.ParseDocsis(Payload(Cga4233Payload), Context(), "Technicolor CGA4233VOO");

        stats.LockedDsChannels.Should().Be(3);
        stats.LockedUsChannels.Should().Be(2);
        stats.DownstreamPowerAvgDbmv.Should().BeApproximately(3.733, 0.001);
        stats.UpstreamPowerAvgDbmv.Should().Be(42.3);
    }

    [Theory]
    [InlineData("474 MHz", 474_000_000)]
    [InlineData("50.7 MHz", 50_700_000)]
    [InlineData("190.487503 MHz", 190_487_503)]
    public void ParseFrequencyHz_HandlesUnitSuffix(string raw, long expected)
    {
        TechnicolorCgaProvider.ParseFrequencyHz(raw).Should().Be(expected);
    }
}
