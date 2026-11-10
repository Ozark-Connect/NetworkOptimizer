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
}
