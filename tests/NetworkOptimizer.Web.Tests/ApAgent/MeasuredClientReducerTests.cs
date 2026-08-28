using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi.Models;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

public class MeasuredClientReducerTests
{
    private const string CoveredAp = "aa:bb:cc:dd:ee:01";
    private const string BareAp = "aa:bb:cc:dd:ee:02";
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    /// <summary>An agent-written row: the counters exist only on the AP Agent path.</summary>
    private static MonitoringInfluxClient.WifiClientSamplePoint AgentRow(
        string clientMac,
        string apMac,
        DateTime at,
        string band = "5ghz",
        double signal = -54,
        int rssi = 38) => new()
        {
            Time = at,
            ApMac = apMac,
            Band = band,
            ClientMac = clientMac,
            SignalDbm = signal,
            NoiseDbm = -92,
            Rssi = rssi,
            TxRateKbps = 2_161_800,
            RxRateKbps = 1_080_900,
            Channel = 44,
            ChannelWidth = 80,
            Satisfaction = 97,
            Nss = 2,
            Ccq = 950,
            TxRetries = 42,
            TxAttempts = 5000,
            LatencyAvgMs = 3.2,
        };

    /// <summary>A console-written row: the same measurement, none of the agent-only fields.</summary>
    private static MonitoringInfluxClient.WifiClientSamplePoint ConsoleRow(
        string clientMac,
        string apMac,
        DateTime at) => new()
        {
            Time = at,
            ApMac = apMac,
            Band = "2.4ghz",
            ClientMac = clientMac,
            SignalDbm = -70,
            TxRateKbps = 100_000,
            RxRateKbps = 90_000,
            Channel = 6,
            ChannelWidth = 20,
        };

    private static IReadOnlySet<string> Covered(params string[] macs) =>
        macs.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void TellsAnAgentRowFromAConsoleRow()
    {
        MeasuredClientReducer.IsAgentMeasured(AgentRow("00:11:22:33:44:55", CoveredAp, Now)).Should().BeTrue();
        MeasuredClientReducer.IsAgentMeasured(ConsoleRow("00:11:22:33:44:55", CoveredAp, Now)).Should().BeFalse();
    }

    [Theory]
    [InlineData("2.4ghz", RadioBand.Band2_4GHz)]
    [InlineData("5ghz", RadioBand.Band5GHz)]
    [InlineData("6ghz", RadioBand.Band6GHz)]
    [InlineData("ng", RadioBand.Unknown)]
    [InlineData(null, RadioBand.Unknown)]
    public void MapsTheBandTag(string? tag, RadioBand expected)
        => MeasuredClientReducer.BandFromTag(tag).Should().Be(expected);

    [Fact]
    public void KeepsOnlyTheAccessPointsThatAreCovered()
    {
        var rows = new[]
        {
            AgentRow("00:11:22:33:44:55", CoveredAp, Now),
            AgentRow("00:11:22:33:44:66", BareAp, Now),
        };

        var result = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge);

        result.Should().ContainKey(CoveredAp);
        result.Should().NotContainKey(BareAp);
        result[CoveredAp].Should().HaveCount(1);
    }

    [Fact]
    public void DropsConsoleWrittenRowsOnACoveredAccessPoint()
    {
        var rows = new[] { ConsoleRow("00:11:22:33:44:55", CoveredAp, Now) };

        var result = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DropsAReadingOlderThanTheAgeGate()
    {
        var rows = new[] { AgentRow("00:11:22:33:44:55", CoveredAp, Now - TimeSpan.FromMinutes(2)) };

        var result = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge);

        result.Should().BeEmpty();
    }

    [Fact]
    public void KeepsTheNewestReadingPerClient()
    {
        var rows = new[]
        {
            AgentRow("00:11:22:33:44:55", CoveredAp, Now - TimeSpan.FromSeconds(60), signal: -80),
            AgentRow("00:11:22:33:44:55", CoveredAp, Now - TimeSpan.FromSeconds(10), signal: -54),
        };

        var result = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge);

        result[CoveredAp].Should().HaveCount(1);
        result[CoveredAp][0].Signal.Should().Be(-54);
    }

    [Fact]
    public void MapsSignalToDbmAndRssiToSnr()
    {
        var rows = new[] { AgentRow("00:11:22:33:44:55", CoveredAp, Now, signal: -54, rssi: 38) };

        var client = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge)[CoveredAp][0];

        client.Signal.Should().Be(-54);
        client.Rssi.Should().Be(38);
        client.Noise.Should().Be(-92);
    }

    [Fact]
    public void CarriesRatesInKbpsAndTheActiveLinkGeometry()
    {
        var rows = new[] { AgentRow("00:11:22:33:44:55", CoveredAp, Now) };

        var client = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge)[CoveredAp][0];

        client.TxRate.Should().Be(2_161_800);
        client.RxRate.Should().Be(1_080_900);
        client.Band.Should().Be(RadioBand.Band5GHz);
        client.Channel.Should().Be(44);
        client.ChannelWidth.Should().Be(80);
    }

    [Fact]
    public void ProducesOneClientForAnMloRecordKeyedOnItsMldMac()
    {
        // The agent folds an MLO client onto its MLD MAC before anything is written, so the series
        // holds one row per client rather than one per link.
        var rows = new[]
        {
            AgentRow("00:11:22:33:44:55", CoveredAp, Now - TimeSpan.FromSeconds(30), band: "6ghz"),
            AgentRow("00:11:22:33:44:55", CoveredAp, Now, band: "6ghz"),
        };

        var result = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), null, Now, MaxAge);

        result[CoveredAp].Should().HaveCount(1);
        result[CoveredAp][0].Mac.Should().Be("00:11:22:33:44:55");
    }

    [Fact]
    public void ReportsTheBandsAClientHasBeenSeenOn()
    {
        var rows = new[] { AgentRow("00:11:22:33:44:55", CoveredAp, Now) };
        var bands = new Dictionary<string, IReadOnlyCollection<string>>
        {
            ["00:11:22:33:44:55"] = new[] { "2.4ghz", "5ghz" },
        };

        var client = MeasuredClientReducer.Reduce(rows, Covered(CoveredAp), bands, Now, MaxAge)[CoveredAp][0];

        client.ObservedBands.Should().BeEquivalentTo(new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz });
    }

    [Fact]
    public void ReducesHistoryToOnePointPerBucket()
    {
        var bucket = TimeSpan.FromMinutes(5);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            AgentRow("00:11:22:33:44:55", CoveredAp, start, signal: -80),
            AgentRow("00:11:22:33:44:55", CoveredAp, start.AddMinutes(2), signal: -54),
            AgentRow("00:11:22:33:44:55", CoveredAp, start.AddMinutes(7), signal: -60),
        };

        var samples = MeasuredClientReducer.ReduceHistory(rows, bucket);

        samples.Should().HaveCount(2);
        samples[0].Signal.Should().Be(-54);
        samples[1].Signal.Should().Be(-60);
        samples.Select(s => s.Timestamp).Should().BeInAscendingOrder();
    }

    [Fact]
    public void KeepsConsoleWrittenRowsInHistory()
    {
        // Over a range our own series is the record whichever tier wrote it, so history takes no
        // source filter - unlike live state, where the console API is fresher than its own echo.
        var rows = new[] { ConsoleRow("00:11:22:33:44:55", CoveredAp, new DateTime(2026, 1, 1, 0, 1, 0, DateTimeKind.Utc)) };

        var samples = MeasuredClientReducer.ReduceHistory(rows, TimeSpan.FromMinutes(5));

        samples.Should().HaveCount(1);
        samples[0].Signal.Should().Be(-70);
        samples[0].Band.Should().Be(RadioBand.Band2_4GHz);
    }
}
