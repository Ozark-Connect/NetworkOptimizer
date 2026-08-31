using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

/// <summary>
/// The interleave contract: one WAN source per bucket, conntrack where covered, DPI where not,
/// never summed inside a bucket; and the totals boundary walks the newest contiguous covered run.
/// </summary>
public class ConntrackInterleaveTests
{
    private static readonly DateTime T0 = new(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CoveredBucketTakesMeasuredValueOverDpi()
    {
        var dpi = new List<UsageBucket> { new(T0, 500, 100) };
        var measured = new List<MonitoringInfluxClient.ClientWanPoint> { new(T0.AddMinutes(10), 900, 300) };
        var coverage = new Dictionary<DateTime, long> { [T0] = 3600 };

        var result = ClientDashboardService.InterleaveWanBuckets(dpi, measured, coverage, TimeSpan.FromHours(1), T0.AddHours(1));

        var bucket = result.Should().ContainSingle().Subject;
        bucket.DownloadBytes.Should().Be(900);
        bucket.UploadBytes.Should().Be(300);
    }

    [Fact]
    public void CoveredBucketWithNothingMeasuredIsMeasuredIdleNotDpi()
    {
        var dpi = new List<UsageBucket> { new(T0, 500, 100) };
        var coverage = new Dictionary<DateTime, long> { [T0] = 3600 };

        var result = ClientDashboardService.InterleaveWanBuckets(
            dpi, Array.Empty<MonitoringInfluxClient.ClientWanPoint>(), coverage, TimeSpan.FromHours(1), T0.AddHours(1));

        var bucket = result.Should().ContainSingle().Subject;
        bucket.DownloadBytes.Should().Be(0);
        bucket.UploadBytes.Should().Be(0);
    }

    [Fact]
    public void UncoveredBucketKeepsDpiForever()
    {
        // Pre-agent history: no coverage entry for the hour.
        var dpi = new List<UsageBucket> { new(T0, 500, 100), new(T0.AddHours(1), 700, 200) };
        var measured = new List<MonitoringInfluxClient.ClientWanPoint> { new(T0.AddHours(1).AddMinutes(5), 40, 10) };
        var coverage = new Dictionary<DateTime, long> { [T0.AddHours(1)] = 3600 };

        var result = ClientDashboardService.InterleaveWanBuckets(dpi, measured, coverage, TimeSpan.FromHours(1), T0.AddHours(2));

        result.Should().HaveCount(2);
        result[0].DownloadBytes.Should().Be(500); // uncovered hour: DPI
        result[1].DownloadBytes.Should().Be(40);  // covered hour: measured
    }

    [Fact]
    public void PartialCoverageUnderTheBarKeepsDpi()
    {
        var dpi = new List<UsageBucket> { new(T0, 500, 100) };
        var measured = new List<MonitoringInfluxClient.ClientWanPoint> { new(T0.AddMinutes(50), 40, 10) };
        var coverage = new Dictionary<DateTime, long> { [T0] = 600 }; // 10 min of a full hour

        var result = ClientDashboardService.InterleaveWanBuckets(dpi, measured, coverage, TimeSpan.FromHours(1), T0.AddHours(1));

        result.Should().ContainSingle().Subject.DownloadBytes.Should().Be(500);
    }

    [Fact]
    public void CurrentPartialBucketJudgedAgainstElapsedTime()
    {
        // 12 minutes into the hour with 10 minutes of coverage: covered, despite 600s << 3600s.
        var coverage = new Dictionary<DateTime, long> { [T0] = 600 };
        var measured = new List<MonitoringInfluxClient.ClientWanPoint> { new(T0.AddMinutes(5), 40, 10) };

        var result = ClientDashboardService.InterleaveWanBuckets(
            new List<UsageBucket> { new(T0, 500, 100) }, measured, coverage, TimeSpan.FromHours(1), T0.AddMinutes(12));

        result.Should().ContainSingle().Subject.DownloadBytes.Should().Be(40);
    }

    [Fact]
    public void SubHourBucketInAPartiallyElapsedCoveredHourIsCovered()
    {
        // 12 minutes into the hour with ~full coverage of those minutes: the recent 5-minute
        // buckets are covered. Scaling the hour's coverage by bucket/hour instead read them as
        // uncovered for the hour's first ~40 minutes and handed the chart's tail to DPI's lag.
        var coverage = new Dictionary<DateTime, long> { [T0] = 700 };
        var measured = new List<MonitoringInfluxClient.ClientWanPoint> { new(T0.AddMinutes(6), 40, 10) };
        var dpi = new List<UsageBucket> { new(T0.AddMinutes(5), 500, 100) };

        var result = ClientDashboardService.InterleaveWanBuckets(
            dpi, measured, coverage, TimeSpan.FromMinutes(5), T0.AddMinutes(12));

        result.Should().ContainSingle().Subject.DownloadBytes.Should().Be(40);
    }

    [Fact]
    public void SubHourBucketInAMostlyUncoveredElapsedHourKeepsDpi()
    {
        var coverage = new Dictionary<DateTime, long> { [T0] = 300 }; // 5 min covered of 12 elapsed
        var dpi = new List<UsageBucket> { new(T0.AddMinutes(5), 500, 100) };

        var result = ClientDashboardService.InterleaveWanBuckets(
            dpi, Array.Empty<MonitoringInfluxClient.ClientWanPoint>(), coverage, TimeSpan.FromMinutes(5), T0.AddMinutes(12));

        result.Should().ContainSingle().Subject.DownloadBytes.Should().Be(500);
    }

    [Fact]
    public void CoverageBoundaryWalksTheNewestContiguousRun()
    {
        var from = T0;
        var to = T0.AddHours(6);
        var coverage = new Dictionary<DateTime, long>
        {
            [T0.AddHours(1)] = 3600, // isolated old covered hour - a feed gap follows it
            [T0.AddHours(4)] = 3600,
            [T0.AddHours(5)] = 3600,
            [T0.AddHours(6)] = 3600,
        };

        BandwidthHogsService.CoverageBoundary(coverage, from, to).Should().Be(T0.AddHours(4));
    }

    [Fact]
    public void NoCoverageNowMeansAllDpi()
    {
        var coverage = new Dictionary<DateTime, long> { [T0] = 3600 }; // covered long ago, dark since
        BandwidthHogsService.CoverageBoundary(coverage, T0, T0.AddHours(6)).Should().Be(T0.AddHours(6));
    }

    [Fact]
    public void FullCoverageReachesTheWindowStart()
    {
        var coverage = new Dictionary<DateTime, long>();
        for (var h = T0; h <= T0.AddHours(6); h = h.AddHours(1)) coverage[h] = 3600;
        BandwidthHogsService.CoverageBoundary(coverage, T0.AddMinutes(30), T0.AddHours(6)).Should().Be(T0.AddMinutes(30));
    }
}
