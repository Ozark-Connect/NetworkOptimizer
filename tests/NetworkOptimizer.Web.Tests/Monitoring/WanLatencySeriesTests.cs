using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The WAN live chart's latency line: the ISP access floor and the Transit mean, half each, so one
/// ISP hop's slow replies do not lift it and ISP still weighs what it always did.
/// </summary>
public class WanLatencySeriesTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static MonitoringInfluxClient.TargetLatencyMean Row(int minute, string id, string type, double? rtt, double? loss = 0) =>
        new(T0.AddMinutes(minute), id, type, rtt, loss);

    [Fact]
    public void Combine_IsHalfAccessFloorAndHalfTransitMean()
    {
        var rows = new[]
        {
            Row(0, "isp-a", "accessisp", 2), Row(0, "isp-b", "accessisp", 9),  // slow control plane on b
            Row(0, "tr-a", "transit", 10), Row(0, "tr-b", "transit", 14),
        };

        var point = WanLatencySeries.Combine(rows, T0).Single();

        point.RttAvgMs.Should().Be((2 + 12) / 2.0, "the floor (2) and the transit mean (12), not the mean of all four");
    }

    [Fact]
    public void Combine_ATargetMissingAWindow_CarriesItsLastReading()
    {
        var rows = new[]
        {
            Row(0, "tr-a", "transit", 10), Row(0, "tr-b", "transit", 20),
            Row(1, "tr-a", "transit", 10),                                     // tr-b misses minute 1
        };

        var points = WanLatencySeries.Combine(rows, T0);

        points.Select(p => p.RttAvgMs).Should().Equal(15, 15);
    }

    [Fact]
    public void Combine_DropsTheWarmup()
    {
        var rows = new[] { Row(0, "tr-a", "transit", 10), Row(1, "tr-a", "transit", 11) };

        WanLatencySeries.Combine(rows, T0.AddMinutes(1)).Should().ContainSingle()
            .Which.RttAvgMs.Should().Be(11);
    }

    [Fact]
    public void Combine_IspHopsThatNeverAnswer_StillReportTheirLoss()
    {
        var rows = new[]
        {
            Row(0, "isp-a", "accessisp", null, 100), Row(0, "isp-b", "accessisp", null, 100),
            Row(0, "tr-a", "transit", 20, 0),
        };

        var point = WanLatencySeries.Combine(rows, T0).Single();

        point.LossPercent.Should().Be(50, "the ISP half's 100% loss is half the line, not dropped");
        point.RttAvgMs.Should().Be(20);
    }

    [Fact]
    public void LiveStats_LowestMedianTarget_PicksByMedianNotByLatest()
    {
        var stats = new MonitoringLiveStats(Microsoft.Extensions.Logging.Abstractions.NullLogger<MonitoringLiveStats>.Instance, null!);
        var now = DateTime.UtcNow;
        foreach (var (rtt, i) in new[] { 2.0, 2.0, 2.0, 9.0 }.Select((v, i) => (v, i)))
            stats.RecordTargetProbe("a", rtt, 0, true, now.AddMinutes(-10 + i));
        foreach (var (rtt, i) in new[] { 3.0, 3.0, 3.0, 1.0 }.Select((v, i) => (v, i)))
            stats.RecordTargetProbe("b", rtt, 0, true, now.AddMinutes(-10 + i));

        stats.LowestMedianTarget(["a", "b"]).Should().Be("a", "a's median is 2; b's latest reading is lower but its median is 3");
    }

    [Fact]
    public void LiveStats_IspAccessFloorLive_IsTheFloorWithTheLowestMedianHopsLoss()
    {
        var stats = new MonitoringLiveStats(Microsoft.Extensions.Logging.Abstractions.NullLogger<MonitoringLiveStats>.Instance, null!);
        var now = DateTime.UtcNow;
        stats.RecordTargetProbe("a", 2.0, 0, true, now.AddMinutes(-5));
        stats.RecordTargetProbe("b", 3.0, 0, true, now.AddMinutes(-5));
        stats.RecordTargetProbe("a", 2.2, 0, true, now);
        stats.RecordTargetProbe("b", 1.5, 7, true, now);

        stats.GetIspAccessFloorLive(["a", "b"]).Should().Be(((double?)1.5, (double?)0));
    }

    [Fact]
    public void Sparkline_IspAccessFloor_MatchesIspHealthsRule()
    {
        var perTarget = new Dictionary<(string TargetId, string Field), List<(DateTime Time, double Value)>>
        {
            [("a", "rtt_avg_ms")] = [(T0, 2.0), (T0.AddMinutes(1), 2.0)],
            [("a", "loss_percent")] = [(T0, 0.0), (T0.AddMinutes(1), 0.0)],
            [("b", "rtt_avg_ms")] = [(T0, 3.0), (T0.AddMinutes(1), 1.5)],
            [("b", "loss_percent")] = [(T0, 9.0), (T0.AddMinutes(1), 9.0)],
        };

        var (floor, loss) = SignalSparklineService.IspAccessFloor(perTarget, ["a", "b"]);

        floor.Select(s => s.RttAvgMs).Should().Equal(2.0, 1.5);
        loss.Select(l => l.LossPercent).Should().Equal(0.0, 0.0);
    }

    [Fact]
    public void Sparkline_IspLoss_WhereTheLowestMedianHopHasNoReading_IsTheHopsMean()
    {
        // b is the lowest-median hop but only started reporting at minute 2 (a hop added mid-hour).
        var perTarget = new Dictionary<(string TargetId, string Field), List<(DateTime Time, double Value)>>
        {
            [("a", "rtt_avg_ms")] = [(T0, 5.0), (T0.AddMinutes(1), 5.0), (T0.AddMinutes(2), 5.0), (T0.AddMinutes(3), 5.0)],
            [("a", "loss_percent")] = [(T0, 4.0), (T0.AddMinutes(1), 4.0), (T0.AddMinutes(2), 4.0), (T0.AddMinutes(3), 4.0)],
            [("b", "rtt_avg_ms")] = [(T0.AddMinutes(2), 1.0), (T0.AddMinutes(3), 1.0)],
            [("b", "loss_percent")] = [(T0.AddMinutes(2), 0.0), (T0.AddMinutes(3), 0.0)],
        };

        var (_, loss) = SignalSparklineService.IspAccessFloor(perTarget, ["a", "b"]);

        loss.Select(l => l.LossPercent).Should().Equal(4.0, 4.0, 0.0, 0.0);
    }
}
