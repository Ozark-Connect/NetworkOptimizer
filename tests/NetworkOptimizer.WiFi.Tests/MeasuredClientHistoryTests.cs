using FluentAssertions;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Providers;
using Xunit;

namespace NetworkOptimizer.WiFi.Tests;

public class MeasuredClientHistoryTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    private static ClientWiFiMetrics ConsolePoint(int bucketIndex, int signal) => new()
    {
        Timestamp = Start + Bucket * bucketIndex,
        ClientMac = "00:11:22:33:44:55",
        Signal = signal,
        Protocol = "ax",
        TxPackets = 1000,
        RxPackets = 900,
        TxRetries = 12,
        Channel = 6,
        Band = RadioBand.Band2_4GHz,
    };

    private static MeasuredClientSample Sample(int bucketIndex, int signal) => new()
    {
        Timestamp = Start + Bucket * bucketIndex,
        ApMac = "aa:bb:cc:dd:ee:01",
        Band = RadioBand.Band5GHz,
        Channel = 44,
        ChannelWidth = 80,
        Signal = signal,
        TxRateKbps = 2_161_800,
        RxRateKbps = 1_080_900,
        Satisfaction = 96,
    };

    [Fact]
    public void ReturnsConsoleHistoryUntouched_WhenTheSeriesHasNothing()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(0, -70), ConsolePoint(1, -71) };

        MeasuredClientOverlay.ApplyHistory(metrics, Array.Empty<MeasuredClientSample>(), Bucket);

        metrics.Should().HaveCount(2);
        metrics[0].Signal.Should().Be(-70);
        metrics[0].Band.Should().Be(RadioBand.Band2_4GHz);
    }

    [Fact]
    public void PrefersTheSeriesWhereItHasTheBucket()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(0, -70) };

        MeasuredClientOverlay.ApplyHistory(metrics, new[] { Sample(0, -54) }, Bucket);

        metrics.Should().HaveCount(1);
        metrics[0].Signal.Should().Be(-54);
        metrics[0].Band.Should().Be(RadioBand.Band5GHz);
        metrics[0].Channel.Should().Be(44);
        metrics[0].TxRateKbps.Should().Be(2_161_800);
    }

    [Fact]
    public void KeepsConsoleOnlyFieldsOnABucketTheSeriesCovers()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(0, -70) };

        MeasuredClientOverlay.ApplyHistory(metrics, new[] { Sample(0, -54) }, Bucket);

        metrics[0].Protocol.Should().Be("ax");
        metrics[0].TxPackets.Should().Be(1000);
        metrics[0].TxRetries.Should().Be(12);
    }

    [Fact]
    public void FillsAGapFromTheConsoleAndNeverWithAZero()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(0, -70), ConsolePoint(1, -71), ConsolePoint(2, -72) };

        MeasuredClientOverlay.ApplyHistory(metrics, new[] { Sample(0, -54), Sample(2, -56) }, Bucket);

        metrics.Should().HaveCount(3);
        metrics[0].Signal.Should().Be(-54);
        metrics[1].Signal.Should().Be(-71);
        metrics[1].Band.Should().Be(RadioBand.Band2_4GHz);
        metrics[2].Signal.Should().Be(-56);
    }

    [Fact]
    public void AddsABucketTheConsoleNeverReported()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(0, -70) };

        MeasuredClientOverlay.ApplyHistory(metrics, new[] { Sample(0, -54), Sample(1, -55) }, Bucket);

        metrics.Should().HaveCount(2);
        metrics[1].Timestamp.Should().Be(Start + Bucket);
        metrics[1].Signal.Should().Be(-55);
        metrics[1].ClientMac.Should().Be("00:11:22:33:44:55");
        metrics[1].Protocol.Should().BeNull();
        metrics[1].TxPackets.Should().BeNull();
    }

    [Fact]
    public void ReturnsPointsInTimeOrder()
    {
        var metrics = new List<ClientWiFiMetrics> { ConsolePoint(2, -72) };

        MeasuredClientOverlay.ApplyHistory(metrics, new[] { Sample(1, -55), Sample(0, -54) }, Bucket);

        metrics.Select(m => m.Timestamp).Should().BeInAscendingOrder();
    }

    [Fact]
    public void CountsMissingBucketsAndTheLongestRun()
    {
        var end = Start + Bucket * 6;
        var measured = new[] { Sample(0, -54), Sample(5, -56) };

        var (missing, longestRun) = MeasuredClientOverlay.MeasureGaps(Start, end, measured, Bucket);

        missing.Should().Be(4);
        longestRun.Should().Be(4);
        longestRun.Should().BeGreaterThanOrEqualTo(MeasuredClientOverlay.ReportableGapBuckets);
    }

    [Fact]
    public void CountsNoGapWhenEveryBucketIsCovered()
    {
        var end = Start + Bucket * 3;
        var measured = new[] { Sample(0, -54), Sample(1, -55), Sample(2, -56) };

        var (missing, longestRun) = MeasuredClientOverlay.MeasureGaps(Start, end, measured, Bucket);

        missing.Should().Be(0);
        longestRun.Should().Be(0);
    }
}
