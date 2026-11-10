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

    private static readonly DateTime T0 = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>One poll every five minutes: loss stored as launch less RX, as the writer does.</summary>
    private static List<MonitoringInfluxClient.PonSeriesPoint> Path(
        IEnumerable<(double? Launch, double? Rx)> samples, long? cls = 2, long? ext = 0) =>
        samples.Select((x, i) => new MonitoringInfluxClient.PonSeriesPoint
        {
            Time = T0.AddMinutes(5 * i),
            OltLaunchPowerDbm = x.Launch,
            PathLossDb = x.Launch.HasValue && x.Rx.HasValue ? Math.Round(x.Launch.Value - x.Rx.Value, 2) : null,
            OdnClass = cls,
            ReachExtender = ext,
        }).ToList();

    private static IEnumerable<(double? Launch, double? Rx)> Steady(int n, double launch = 3.4, double rx = -16.5) =>
        Enumerable.Repeat(((double?)launch, (double?)rx), n);

    [Fact]
    public void SummarizePathLoss_SteadyLine_ReadsLossAgainstItsClass()
    {
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(20)))!;

        s.LossDb.Should().BeApproximately(19.9, 1e-9);
        s.LaunchDbm.Should().Be(3.4);
        s.RxDbm.Should().BeApproximately(-16.5, 1e-9);
        s.OdnClass.Should().Be("B+");
        (s.ClassMinDb, s.ClassMaxDb).Should().Be((13, 28));
        s.MarginDb.Should().BeApproximately(8.1, 1e-9);
        s.BelowMinDb.Should().BeNull();
        s.LossChangeDb.Should().Be(0);
        s.Level.Should().Be("ok");
        s.Hints.Should().BeEmpty();
        s.Changes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0L, "A", 5, 20)]
    [InlineData(1L, "B", 10, 25)]
    [InlineData(3L, "C", 15, 30)]
    [InlineData(4L, "C+", 17, 32)]
    public void SummarizePathLoss_UsesTheAnnouncedClass_NotAFixedOne(long cls, string name, double min, double max)
    {
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(3), cls))!;

        s.OdnClass.Should().Be(name);
        s.MarginDb.Should().BeApproximately(max - 19.9, 1e-9);
        (s.ClassMinDb, s.ClassMaxDb).Should().Be((min, max));
    }

    [Fact]
    public void SummarizePathLoss_NearAndOverTheLimit_AreHinted()
    {
        var near = PonChartSeries.SummarizePathLoss(Path(Steady(3, rx: -22.6)))!; // 26.0 dB, 2.0 left
        near.Hints.Should().Equal("near_limit");
        near.Level.Should().Be("notice");

        var over = PonChartSeries.SummarizePathLoss(Path(Steady(3, rx: -25.6)))!; // 29.0 dB
        over.MarginDb.Should().BeApproximately(-1.0, 1e-9);
        over.Hints.Should().Equal("over_limit");
        over.Level.Should().Be("warning");
    }

    [Fact]
    public void SummarizePathLoss_UnderTheClassMinimum_IsHinted()
    {
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(3, rx: -8.4)))!; // 11.8 dB under B+'s 13

        s.BelowMinDb.Should().BeApproximately(1.2, 1e-9);
        s.Hints.Should().Equal("below_min");
    }

    [Fact]
    public void SummarizePathLoss_RxFallsWithLaunchSteady_IsThePath()
    {
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(12).Concat(Steady(3, rx: -19.0))))!;

        s.BaselineLossDb.Should().BeApproximately(19.9, 1e-9);
        s.LossChangeDb.Should().BeApproximately(2.5, 1e-9);
        s.LaunchChangeDb.Should().Be(0);
        s.RxChangeDb.Should().BeApproximately(-2.5, 1e-9);
        s.Hints.Should().Equal("loss_rise");
        s.Changes.Should().BeEmpty("the announcement itself did not change");
    }

    [Fact]
    public void SummarizePathLoss_LaunchFallsAndRxWithIt_IsTheOltSide()
    {
        // Loss unchanged: the OLT sends 2 dB less and the ONT receives 2 dB less.
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(12).Concat(Steady(3, launch: 1.4, rx: -18.5))))!;

        s.LossChangeDb.Should().Be(0);
        s.LaunchChangeDb.Should().BeApproximately(-2.0, 1e-9);
        s.RxChangeDb.Should().BeApproximately(-2.0, 1e-9);
        s.Hints.Should().Equal("launch_drop");
        s.Changes.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new PonChartSeries.PonIdChange(T0.AddMinutes(60).ToString("o"), "launch_power", "+3.4 dBm", "+1.4 dBm"));
    }

    [Fact]
    public void SummarizePathLoss_SmallRiseOrShortHistory_GivesNoHint()
    {
        PonChartSeries.SummarizePathLoss(Path(Steady(12).Concat(Steady(3, rx: -18.0))))!
            .Hints.Should().BeEmpty("1.5 dB is inside what the RX reading wanders");
        var shortHistory = PonChartSeries.SummarizePathLoss(Path(Steady(4).Concat(Steady(3, rx: -19.5))))!;
        shortHistory.LossChangeDb.Should().BeNull("too few points for a usual level");
        shortHistory.Hints.Should().BeEmpty();
    }

    [Fact]
    public void SummarizePathLoss_OneOutlierPoll_IsNotARise()
    {
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(12).Concat(Steady(1, rx: -21.0)).Concat(Steady(2))))!;

        s.LossChangeDb.Should().Be(0);
        s.Hints.Should().BeEmpty();
    }

    [Fact]
    public void SummarizePathLoss_LaunchPowerWanderingByAStep_IsNotAChange()
    {
        // Seen live: the OLT's reading moved from +3.4 to +3.5 dBm within the hour.
        var s = PonChartSeries.SummarizePathLoss(Path(Steady(6).Concat(Steady(6, launch: 3.5)).Concat(Steady(3, launch: 3.3))))!;

        s.Changes.Should().BeEmpty();
        s.Hints.Should().BeEmpty();
    }

    [Fact]
    public void SummarizePathLoss_LaunchPowerDriftingAFullDb_IsRecordedOnce()
    {
        var drift = Enumerable.Range(0, 11).SelectMany(i => Steady(2, launch: 3.4 - 0.1 * i, rx: -16.5 - 0.1 * i));

        var s = PonChartSeries.SummarizePathLoss(Path(drift))!;

        s.Changes.Should().ContainSingle().Which.Should().Match<PonChartSeries.PonIdChange>(
            c => c.What == "launch_power" && c.From == "+3.4 dBm" && c.To == "+2.4 dBm");
    }

    [Fact]
    public void SummarizePathLoss_ClassAndExtenderChange_AreRecorded()
    {
        var points = Path(Steady(4));
        points[2].OdnClass = 4;
        points[3].OdnClass = 4;
        points[3].ReachExtender = 1;
        points[1].OdnClass = null; // a missed read is not a change

        var s = PonChartSeries.SummarizePathLoss(points)!;

        s.ReachExtender.Should().BeTrue();
        s.OdnClass.Should().Be("C+");
        s.Changes.Select(c => (c.What, c.From, c.To)).Should().Equal(
            ("odn_class", "B+", "C+"), ("reach_extender", "no", "yes"));
    }

    [Fact]
    public void SummarizePathLoss_ClassButNoLaunchPower_HasNoLoss()
    {
        // An OLT that sends a PON-ID without its power.
        var s = PonChartSeries.SummarizePathLoss(Path(Enumerable.Repeat(((double?)null, (double?)-16.5), 20)))!;

        s.LossDb.Should().BeNull();
        s.MarginDb.Should().BeNull();
        s.OdnClass.Should().Be("B+");
        s.Hints.Should().BeEmpty();
    }

    [Fact]
    public void SummarizePathLoss_NoPonIdAtAll_IsNull()
    {
        PonChartSeries.SummarizePathLoss(null).Should().BeNull();
        PonChartSeries.SummarizePathLoss([]).Should().BeNull();
        PonChartSeries.SummarizePathLoss([Point(0, 1000), Point(10, 2000)]).Should().BeNull("older firmware or an OLT without PON-ID");
    }
}
