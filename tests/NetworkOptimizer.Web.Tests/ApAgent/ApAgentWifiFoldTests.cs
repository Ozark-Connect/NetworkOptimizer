using FluentAssertions;
using NetworkOptimizer.Web.Services.ApAgent;
using Xunit;

namespace NetworkOptimizer.Web.Tests.ApAgent;

/// <summary>
/// The field mapping and the window fold. Both are pure, and both are where a Wi-Fi 7 site would
/// silently gain two clients for every one it has.
/// </summary>
public class ApAgentWifiFoldTests
{
    private const string ApMac = "aa:bb:cc:dd:ee:ff";
    private const string StationMac = "00:11:22:33:44:55";
    private const string MldMac = "00:11:22:33:44:60";

    private static readonly DateTime Now = new(2026, 8, 24, 12, 0, 0, DateTimeKind.Utc);

    private static ApAgentClientLink Link(
        bool active = true,
        string band = "5",
        int nss = 2,
        int ccq = 900,
        long txBytes = 1_000_000,
        long rxBytes = 500_000,
        long txRetries = 100,
        long txAttempts = 5_000,
        long txDropped = 3,
        int latencyAvgUs = 4_500,
        int latencyMaxUs = 9_000,
        int tcpLatAvgMs = 12,
        int tcpStalls = 2)
        => new()
        {
            Active = active,
            Band = band,
            Nss = nss,
            Ccq = ccq,
            TxBytes = txBytes,
            RxBytes = rxBytes,
            TxRetries = txRetries,
            TxAttempts = txAttempts,
            TxDropped = txDropped,
            TxLatency = new ApAgentTxLatency { Avg = latencyAvgUs, Max = latencyMaxUs },
            TxTcpStats = new ApAgentTcpStats { LatAvg = tcpLatAvgMs, Stalls = tcpStalls },
        };

    private static ApAgentClient Client(
        string key = StationMac,
        bool isMlo = false,
        string? mldMac = null,
        string band = "5",
        int signal = -60,
        params ApAgentClientLink[] links)
        => new()
        {
            Key = key,
            Mac = key,
            MldMac = mldMac,
            IsMlo = isMlo,
            Band = band,
            Channel = 44,
            Bandwidth = 160,
            Signal = signal,
            Noise = -96,
            Snr = 36,
            TxRateKbps = 1_200_000,
            RxRateKbps = 900_000,
            Satisfaction = 98,
            Capabilities = new ApAgentClientCapabilities { Nss = 4 },
            Links = links.Length > 0 ? links.ToList() : new List<ApAgentClientLink> { Link() },
        };

    [Theory]
    [InlineData("2.4", "2.4ghz")]
    [InlineData("5", "5ghz")]
    [InlineData("6", "6ghz")]
    [InlineData("ng", "2.4ghz")]
    [InlineData("6e", "6ghz")]
    public void Band_maps_onto_the_existing_tag_values(string token, string expected)
        => ApAgentWifiFieldMapper.MapBand(token).Should().Be(expected);

    [Fact]
    public void Unknown_band_drops_the_client_rather_than_tagging_a_guess()
    {
        var client = Client(band: "60");
        client.Links[0].Band = "60";

        ApAgentWifiFieldMapper.ToSample(client, ApMac).Should().BeNull();
    }

    [Fact]
    public void Counters_come_from_the_active_link_and_latency_converts_to_milliseconds()
    {
        var sample = ApAgentWifiFieldMapper.ToSample(Client(), ApMac);

        sample.Should().NotBeNull();
        sample!.TxRetries.Should().Be(100);
        sample.TxAttempts.Should().Be(5_000);
        sample.TxDropped.Should().Be(3);
        sample.Ccq.Should().Be(900);
        sample.TcpStalls.Should().Be(2);
        sample.TcpLatAvgMs.Should().Be(12);
        // wifi_tx_latency_mov is microseconds on the AP.
        sample.LatencyAvgMs.Should().Be(4.5);
        sample.LatencyMaxMs.Should().Be(9.0);
    }

    [Fact]
    public void Nss_prefers_the_operating_value_and_falls_back_to_the_capability()
    {
        ApAgentWifiFieldMapper.ToSample(Client(), ApMac)!.Nss.Should().Be(2);

        var idle = Client(links: Link(nss: 0));
        ApAgentWifiFieldMapper.ToSample(idle, ApMac)!.Nss.Should().Be(4);
    }

    [Fact]
    public void Mlo_client_is_one_sample_keyed_on_the_mld_mac()
    {
        var mlo = Client(
            key: MldMac,
            isMlo: true,
            mldMac: MldMac,
            band: "6",
            links:
            [
                Link(active: false, band: "2.4", ccq: 100, txRetries: 1),
                Link(active: false, band: "5", ccq: 200, txRetries: 2),
                Link(active: true, band: "6", ccq: 950, txRetries: 42),
            ]);

        var sample = ApAgentWifiFieldMapper.ToSample(mlo, ApMac);

        sample.Should().NotBeNull();
        sample!.ClientMac.Should().Be(MldMac);
        sample.IsMlo.Should().BeTrue();
        sample.Band.Should().Be("6ghz");
        sample.Ccq.Should().Be(950);
        sample.TxRetries.Should().Be(42);
    }

    [Fact]
    public void Mlo_client_writes_one_point_not_one_per_link()
    {
        var accumulator = new ApAgentWifiAccumulator();
        var mlo = Client(
            key: MldMac, isMlo: true, mldMac: MldMac, band: "6",
            links:
            [
                Link(active: false, band: "2.4"),
                Link(active: false, band: "5"),
                Link(active: true, band: "6"),
            ]);

        accumulator.Add(ApAgentWifiFieldMapper.ToSample(mlo, ApMac)!, Now);
        accumulator.Add(ApAgentWifiFieldMapper.ToSample(Client(), ApMac)!, Now);

        var folded = accumulator.Flush(Now);

        folded.Should().HaveCount(2);
        folded.Select(f => f.Sample.ClientMac).Should().BeEquivalentTo([MldMac, StationMac]);
    }

    [Fact]
    public void Samples_fold_to_one_point_with_averages_and_the_worst_latency()
    {
        var accumulator = new ApAgentWifiAccumulator();

        accumulator.Add(Sample(signal: -60, latencyAvgMs: 4, latencyMaxMs: 8, ccq: 900, txRetries: 100), Now);
        accumulator.Add(Sample(signal: -70, latencyAvgMs: 6, latencyMaxMs: 20, ccq: 800, txRetries: 140), Now.AddSeconds(10));
        accumulator.Add(Sample(signal: -80, latencyAvgMs: 8, latencyMaxMs: 12, ccq: 700, txRetries: 150), Now.AddSeconds(20));

        var folded = accumulator.Flush(Now.AddSeconds(30));

        folded.Should().HaveCount(1);
        var result = folded[0];
        result.SampleCount.Should().Be(3);
        result.Sample.SignalDbm.Should().Be(-70);
        result.Sample.LatencyAvgMs.Should().Be(6);
        result.Sample.LatencyMaxMs.Should().Be(20);
        result.Sample.Ccq.Should().Be(800);
        // Cumulative counters take the newest value; averaging a running total means nothing.
        result.Sample.TxRetries.Should().Be(150);
    }

    [Fact]
    public void A_missing_reading_does_not_drag_the_average_to_zero()
    {
        var accumulator = new ApAgentWifiAccumulator();

        accumulator.Add(Sample(signal: -50), Now);
        accumulator.Add(Sample(signal: null), Now.AddSeconds(10));

        accumulator.Flush(Now.AddSeconds(30))[0].Sample.SignalDbm.Should().Be(-50);
    }

    [Fact]
    public void Throughput_comes_from_the_byte_delta_across_write_windows()
    {
        var accumulator = new ApAgentWifiAccumulator();

        accumulator.Add(Sample(txBytes: 1_000_000, rxBytes: 500_000), Now);
        var first = accumulator.Flush(Now);

        // Nothing to diff against on the first window, so no rate is invented.
        first[0].TxThroughputBps.Should().BeNull();

        accumulator.Add(Sample(txBytes: 4_000_000, rxBytes: 1_000_000), Now.AddSeconds(30));
        var second = accumulator.Flush(Now.AddSeconds(30));

        second[0].TxThroughputBps.Should().Be(3_000_000 * 8.0 / 30);
        second[0].RxThroughputBps.Should().Be(500_000 * 8.0 / 30);
    }

    [Fact]
    public void A_counter_that_went_backwards_reports_no_rate_rather_than_a_negative_one()
    {
        var accumulator = new ApAgentWifiAccumulator();

        accumulator.Add(Sample(txBytes: 9_000_000, rxBytes: 9_000_000), Now);
        accumulator.Flush(Now);

        accumulator.Add(Sample(txBytes: 10_000, rxBytes: 10_000), Now.AddSeconds(30));
        var second = accumulator.Flush(Now.AddSeconds(30));

        second[0].TxThroughputBps.Should().BeNull();
        second[0].RxThroughputBps.Should().BeNull();
    }

    [Fact]
    public void One_access_point_cannot_grow_the_fold_without_bound()
    {
        var accumulator = new ApAgentWifiAccumulator();

        for (var i = 0; i < ApAgentWifiAccumulator.MaxTrackedClients + 50; i++)
            accumulator.Add(Sample(clientMac: $"00:11:22:33:{i / 256:x2}:{i % 256:x2}"), Now);

        accumulator.PendingClients.Should().Be(ApAgentWifiAccumulator.MaxTrackedClients);
    }

    private static ApAgentWifiSample Sample(
        string clientMac = StationMac,
        double? signal = -60,
        double? latencyAvgMs = 4,
        double? latencyMaxMs = 8,
        int? ccq = 900,
        long? txRetries = 100,
        long txBytes = 1_000_000,
        long rxBytes = 500_000)
        => new(
            ClientMac: clientMac,
            ApMac: ApMac,
            Band: "5ghz",
            Channel: 44,
            ChannelWidth: 160,
            SignalDbm: signal,
            NoiseDbm: -96,
            Rssi: 36,
            TxRateKbps: 1_200_000,
            RxRateKbps: 900_000,
            Satisfaction: 98,
            TxBytes: txBytes,
            RxBytes: rxBytes,
            IsMlo: false,
            TxRetries: txRetries,
            TxAttempts: 5_000,
            TxDropped: 3,
            LatencyAvgMs: latencyAvgMs,
            LatencyMaxMs: latencyMaxMs,
            TcpStalls: 2,
            TcpLatAvgMs: 12,
            Ccq: ccq,
            Nss: 2);
}
