using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Endpoints;
using Xunit;

namespace NetworkOptimizer.Web.Tests;

public class PonChartSeriesTests
{
    private static MonitoringInfluxClient.PonSeriesPoint Point(long? idle, long? superframe, long? dsFec = 1) => new()
    {
        Time = DateTime.UtcNow,
        GemRxIdleFrames = idle,
        DsSuperframe = superframe,
        DsFecEnabled = dsFec,
    };

    [Fact]
    public void DownstreamUtilization_MeasuredInterval_ReadsAboutOnePercent()
    {
        // 60 s on a live GPON line with FEC: 480000 frames of 36432 payload bytes, and
        // 7200 idle GEM frames of 5 bytes in each of them.
        var prev = Point(1_000_000_000_000, 100_000);
        var cur = Point(1_000_000_000_000 + 7200L * 480_000, 580_000);

        var load = PonChartSeries.DownstreamUtilization(cur, prev);

        load.Should().NotBeNull();
        load!.Value.Percent.Should().BeApproximately(1.186, 0.001);
        load.Value.Mbps.Should().BeApproximately(27.65, 0.01);
    }

    [Fact]
    public void DownstreamUtilization_FecOff_UsesTheWholeFrame()
    {
        // All idle without FEC is 38880 / 5 = 7776 idle frames per frame.
        var load = PonChartSeries.DownstreamUtilization(Point(7776L * 1000, 1000, dsFec: 0), Point(0, 0, dsFec: 0));

        load!.Value.Percent.Should().Be(0);
        load.Value.Mbps.Should().Be(0);
    }

    [Fact]
    public void DownstreamUtilization_SuperframeWrap_IsOneInterval()
    {
        // The 30-bit counter wraps about every 37 hours.
        var prev = Point(0, (1L << 30) - 1000);
        var cur = Point(7200L * 2000, 1000);

        PonChartSeries.DownstreamUtilization(cur, prev)!.Value.Percent.Should().BeApproximately(1.186, 0.001);
    }

    [Fact]
    public void DownstreamUtilization_NotComputable_IsNull()
    {
        var good = Point(7200L * 1000, 2000);

        PonChartSeries.DownstreamUtilization(good, null).Should().BeNull("the first point has no interval");
        PonChartSeries.DownstreamUtilization(good, Point(null, 1000)).Should().BeNull("no idle counter");
        PonChartSeries.DownstreamUtilization(Point(100, 2000), Point(7200L * 1000, 1000)).Should().BeNull("a reboot reset the counter");
        PonChartSeries.DownstreamUtilization(good, Point(7200L * 1000, 2000)).Should().BeNull("no frames passed");
        // More idle than fits the frames: the superframe counter wrapped more than once.
        PonChartSeries.DownstreamUtilization(Point(7200L * 5000, 2000), Point(0, 1000)).Should().BeNull();
    }

    [Fact]
    public void DownstreamUtilization_SampledSuperframe_IsPreferredOverTheLiveOne()
    {
        // The live counter was read one sample (8000 frames) late at the second poll. The counter
        // sampled with the totals says what the idle count really spans.
        var prev = Point(0, 100_000);
        prev.DsSuperframeSampled = 100_000;
        var cur = Point(7200L * 480_000, 588_000);
        cur.DsSuperframeSampled = 580_000;

        PonChartSeries.DownstreamUtilization(cur, prev)!.Value.Percent.Should().BeApproximately(1.186, 0.001);
    }

    [Fact]
    public void DownstreamUtilization_NoSampledSuperframe_FallsBackToTheLiveOne()
    {
        // Older firmware: only the live counter.
        var prev = Point(0, 100_000);
        var cur = Point(7200L * 480_000, 580_000);

        PonChartSeries.DownstreamUtilization(cur, prev)!.Value.Percent.Should().BeApproximately(1.186, 0.001);
    }

    [Fact]
    public void DownstreamUtilization_SampledSuperframeOnOneSideOnly_UsesTheLivePair()
    {
        // The poll after a firmware update: the new point has both counters, the old one only the
        // live one. Mixing them would mix two reading instants.
        var prev = Point(0, 100_000);
        var cur = Point(7200L * 480_000, 580_000);
        cur.DsSuperframeSampled = 572_000;

        PonChartSeries.FramesBetween(cur, prev).Should().Be(480_000);
        PonChartSeries.DownstreamUtilization(cur, prev)!.Value.Percent.Should().BeApproximately(1.186, 0.001);
    }

    [Fact]
    public void FramesBetween_SampledSuperframeWrap_IsOneInterval()
    {
        var prev = Point(0, null);
        prev.DsSuperframeSampled = (1L << 30) - 1000;
        var cur = Point(0, null);
        cur.DsSuperframeSampled = 1000;

        PonChartSeries.FramesBetween(cur, prev).Should().Be(2000);
    }

    [Fact]
    public void FramesBetween_NoPairOrStandingStill_IsNull()
    {
        PonChartSeries.FramesBetween(Point(0, null), Point(0, 1000)).Should().BeNull("no counter on one side");
        PonChartSeries.FramesBetween(Point(0, 1000), Point(0, 1000)).Should().BeNull("no downstream frames passed");
    }

    [Fact]
    public void OwnDownstreamMbps_TimedBySuperframes()
    {
        // 300 s of frames and 37.5 MB on the ONT's own GEM ports: 1 Mbit/s.
        var prev = Point(0, 100_000);
        prev.GemRxBytes = 1_000_000;
        var cur = Point(0, 100_000 + 300 * 8000);
        cur.GemRxBytes = 1_000_000 + 37_500_000;

        PonChartSeries.OwnDownstreamMbps(cur, prev).Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void OwnDownstreamMbps_NoSuperframe_IsTimedByTheClock()
    {
        var t = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var prev = new MonitoringInfluxClient.PonSeriesPoint { Time = t, GemRxBytes = 0 };
        var cur = new MonitoringInfluxClient.PonSeriesPoint { Time = t.AddSeconds(300), GemRxBytes = 37_500_000 };

        PonChartSeries.OwnDownstreamMbps(cur, prev).Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void OwnDownstreamMbps_NotComputable_IsNull()
    {
        var prev = Point(0, 1000);
        prev.GemRxBytes = 5000;
        var restarted = Point(0, 2000);
        restarted.GemRxBytes = 100;

        PonChartSeries.OwnDownstreamMbps(restarted, null).Should().BeNull("the first point has no interval");
        PonChartSeries.OwnDownstreamMbps(restarted, prev).Should().BeNull("the GEM flow was created anew");
        PonChartSeries.OwnDownstreamMbps(Point(0, 2000), prev).Should().BeNull("no byte counter");
    }

    [Fact]
    public void Build_CarriesPathLossAndPonIdFields()
    {
        var p = Point(0, 1000);
        p.PathLossDb = 19.9;
        p.OltLaunchPowerDbm = 3.4;
        p.OdnClass = 2;
        p.ReachExtender = 0;

        var json = System.Text.Json.JsonSerializer.Serialize(PonChartSeries.Build([p])!.Single());

        json.Should().Contain("\"pathLoss\":19.9").And.Contain("\"oltLaunch\":3.4")
            .And.Contain("\"odnClass\":2").And.Contain("\"reachExt\":0");
    }

    [Fact]
    public void Build_NoPonIdFields_LeavesThemNull()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(PonChartSeries.Build([Point(0, 1000)])!.Single());

        json.Should().Contain("\"pathLoss\":null").And.Contain("\"oltLaunch\":null").And.Contain("\"dsOwnMbps\":null");
    }
}
