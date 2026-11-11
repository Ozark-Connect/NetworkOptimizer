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

    /// <summary>One poll every five minutes on one boot, the ONT's clocks running with the wall clock.</summary>
    private static List<MonitoringInfluxClient.PonSeriesPoint> Link(int n) =>
        Enumerable.Range(0, n).Select(i => new MonitoringInfluxClient.PonSeriesPoint
        {
            Time = T0.AddMinutes(5 * i),
            PonLinkStatus = "operation",
            SfpUptimeS = 1000 + 300 * i,
            O5Seconds = 998 + 300.0 * i,
            NotO5Seconds = 2,
            O5Exits = 0,
            LosEvents = 0,
            LosSeconds = 0,
            LofEvents = 0,
            OltDeactivations = 0,
            DsSuperframe = 8000L * 300 * i,
            AllocTotal = 600_000L * i,
            UsBursts = 300_000L * i,
            UsDbru = 299_000L * i,
            GemTxBytes = 30_000_000L * i,
        }).ToList();

    /// <summary>Puts a drop into the interval ending at point <paramref name="at"/>, and carries it forward.</summary>
    private static void Drop(List<MonitoringInfluxClient.PonSeriesPoint> points, int at, double seconds, long los = 0, long deact = 0)
    {
        for (var i = at; i < points.Count; i++)
        {
            points[i].O5Seconds -= seconds;
            points[i].NotO5Seconds += seconds;
            points[i].O5Exits += 1;
            points[i].LosEvents += los;
            points[i].LosSeconds += los > 0 ? seconds : 0;
            points[i].OltDeactivations += deact;
        }
    }

    [Fact]
    public void SummarizeAvailability_CleanWindow_IsFullyAvailable()
    {
        var s = PonChartSeries.SummarizeAvailability(Link(13))!;

        s.AvailabilityPercent.Should().Be(100);
        s.Drops.Should().Be(0);
        s.DownSeconds.Should().Be(0);
        s.Outages.Should().BeEmpty();
        s.Reboots.Should().BeEmpty();
    }

    [Fact]
    public void SummarizeAvailability_DropBetweenPolls_CountsWithItsCause()
    {
        // Both polls saw O5; the ONT's own clock says it was out for 36 s in between.
        var points = Link(13);
        Drop(points, 4, 36, los: 1);
        Drop(points, 9, 12, deact: 1);

        var s = PonChartSeries.SummarizeAvailability(points)!;

        s.Drops.Should().Be(2);
        s.DownSeconds.Should().Be(48);
        s.AvailabilityPercent.Should().BeApproximately(100 * (3600 - 48) / 3600.0, 0.001);
        s.LosEvents.Should().Be(1);
        s.LosSeconds.Should().Be(36);
        s.OltDeactivations.Should().Be(1);
        s.Outages.Select(o => (o.Time, o.DownSeconds, o.Cause)).Should().Equal(
            (T0.AddMinutes(20).ToString("o"), 36, "light"),
            (T0.AddMinutes(45).ToString("o"), 12, "olt"));
    }

    [Fact]
    public void SummarizeAvailability_DropWithoutAlarmOrMessage_IsUnknown()
    {
        var points = Link(6);
        Drop(points, 3, 5);

        PonChartSeries.SummarizeAvailability(points)!.Outages.Should().ContainSingle().Which.Cause.Should().Be("unknown");
    }

    [Fact]
    public void SummarizeAvailability_OntReboot_IsAnEventNotDowntime()
    {
        var points = Link(8);
        // The ONT restarted before point 5: uptime and every counter began again.
        for (var i = 5; i < points.Count; i++)
        {
            points[i].SfpUptimeS = 60 + 300 * (i - 5);
            points[i].O5Seconds = 58 + 300.0 * (i - 5);
            points[i].LastResetReason = "wdt_userland";
        }

        var s = PonChartSeries.SummarizeAvailability(points)!;

        s.Reboots.Should().ContainSingle().Which.Should().Be(
            new PonChartSeries.PonReboot(T0.AddMinutes(25).ToString("o"), "wdt_userland"));
        s.Outages.Should().BeEmpty();
        s.Drops.Should().Be(0);
        s.AvailabilityPercent.Should().Be(100, "the interval across the reboot is left out");
    }

    [Fact]
    public void SummarizeAvailability_NoStateClock_IsNull()
    {
        PonChartSeries.SummarizeAvailability(null).Should().BeNull();
        PonChartSeries.SummarizeAvailability([Point(0, 1000), Point(10, 2000)]).Should().BeNull("a source without the state clock");
    }

    [Fact]
    public void Build_LinkSeries_AreRatesAndSecondsPerInterval()
    {
        var points = Link(4);
        Drop(points, 2, 36, los: 1);

        var json = System.Text.Json.JsonSerializer.Serialize(PonChartSeries.Build(points)!);
        var rows = System.Text.Json.JsonDocument.Parse(json).RootElement;

        rows[0].GetProperty("grantsPerS").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "the first point has no interval");
        rows[1].GetProperty("grantsPerS").GetDouble().Should().Be(2000);
        rows[1].GetProperty("burstsPerS").GetDouble().Should().Be(1000);
        rows[1].GetProperty("bytesPerBurst").GetDouble().Should().Be(100);
        rows[1].GetProperty("downS").GetDouble().Should().Be(0);
        rows[2].GetProperty("downS").GetDouble().Should().Be(36);
        rows[2].GetProperty("losS").GetDouble().Should().Be(36);
        rows[3].GetProperty("downS").GetDouble().Should().Be(0);
    }

    [Fact]
    public void Build_AcrossAReboot_LeavesTheIntervalOut()
    {
        var points = Link(3);
        points[2].SfpUptimeS = 30;
        points[2].NotO5Seconds = 2;
        points[2].AllocTotal = 1000;

        var rows = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(PonChartSeries.Build(points)!)).RootElement;

        rows[2].GetProperty("downS").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        rows[2].GetProperty("grantsPerS").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "the counter went back");
    }

    [Fact]
    public void OwnShare_IsOwnDownstreamOverThePonsLoad()
    {
        // 300 s: the PON carried 27.65 Mbit/s (see the measured interval above, scaled), this ONT 2.765.
        var prev = Point(0, 0);
        prev.GemRxBytes = 0;
        var cur = Point(7200L * 2_400_000, 2_400_000);
        cur.GemRxBytes = (long)(2.765e6 / 8 * 300);

        PonChartSeries.OwnShare(cur, prev).Should().BeApproximately(10, 0.05);
    }

    [Fact]
    public void OwnShare_NotComputable_IsNull()
    {
        var prev = Point(0, 0);
        var cur = Point(7200L * 2_400_000, 2_400_000);

        PonChartSeries.OwnShare(cur, prev).Should().BeNull("no byte counter");
        PonChartSeries.OwnShare(cur, null).Should().BeNull();
    }

    [Fact]
    public void HourlyLoad_OneValuePerHour_SkipsGaps()
    {
        MonitoringInfluxClient.PonSeriesPoint At(double hours, long frames) => new()
        {
            Time = T0.AddHours(hours), DsFecEnabled = 1, DsSuperframe = frames % (1L << 30), GemRxIdleFrames = 7200L * frames,
        };
        const long hourFrames = 8000L * 3600;
        var points = new List<MonitoringInfluxClient.PonSeriesPoint>
        {
            At(0, 0), At(1, hourFrames), At(2, 2 * hourFrames),
            // Five hours without data: not one long hour.
            At(7, 7 * hourFrames), At(8, 8 * hourFrames),
        };

        var rows = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(PonChartSeries.HourlyLoad(points))).RootElement;

        rows.GetArrayLength().Should().Be(3);
        rows[0].GetProperty("pct").GetDouble().Should().BeApproximately(1.186, 0.001);
        rows[0].GetProperty("time").GetString().Should().Be(T0.AddMinutes(30).ToString("o"), "the middle of the hour it stands for");
        rows[2].GetProperty("time").GetString().Should().Be(T0.AddHours(7.5).ToString("o"));
    }

    /// <summary>A healthy line with everything the connection check reads.</summary>
    private static List<MonitoringInfluxClient.PonSeriesPoint> Healthy(int n = 4)
    {
        var points = Link(n);
        for (var i = 0; i < n; i++)
        {
            var p = points[i];
            p.PathLossDb = 19.9;
            p.OltLaunchPowerDbm = 3.4;
            p.OdnClass = 2;
            p.OmciUp = 1;
            p.OmciServices = 3;
            p.GemRxBytes = 1_000_000L * i;
            p.OmciRxFrames = 30L * i;
            p.MibDataSync = 160;
            p.PloamRequestKey = 1;
            p.PloamKeySwitch = 3;
            p.PloamRangingTime = 3;
            p.TrafficPirBytesPerSecond = 167772125;
            p.TrafficCirBytesPerSecond = 167772125;
            p.FecCorrectedWords = 0;
            p.FecCorrectedBits = 0;
            p.FecErrors = 0;
            p.BipErrors = 0;
            p.BiasMa = 14.5;
            p.BiasWarnHighMa = 90;
            p.OntTxPowerDbm = 1.8;
            p.OntTemperatureC = 52;
        }
        return points;
    }

    private static string[] Statuses(List<MonitoringInfluxClient.PonSeriesPoint> points) =>
        PonChartSeries.ConnectionCheck(points)!.Select(s => $"{s.Key}:{s.Status}").ToArray();

    [Fact]
    public void ConnectionCheck_HealthyLine_IsAllOk()
    {
        Statuses(Healthy()).Should().Equal("light:ok", "registered:ok", "management:ok", "service:ok", "traffic:ok");
    }

    [Fact]
    public void ConnectionCheck_EachStep_FailsOnItsOwnEvidence()
    {
        var dark = Healthy();
        dark[^1].PathLossDb = 29.5;
        Statuses(dark)[0].Should().Be("light:fail");

        var starved = Healthy();
        starved[^1].AllocTotal = starved[^2].AllocTotal;
        PonChartSeries.ConnectionCheck(starved)![1].Should().Be(
            new PonChartSeries.CheckStep("registered", "fail", "in O5, but the OLT grants this ONT nothing"));

        var ranging = Healthy();
        ranging[^1].PonLinkStatus = "ranging";
        Statuses(ranging)[1].Should().Be("registered:fail");

        var noOmci = Healthy();
        noOmci[^1].OmciUp = 0;
        Statuses(noOmci)[2].Should().Be("management:fail");

        var noService = Healthy();
        noService[^1].OmciServices = 0;
        Statuses(noService)[3].Should().Be("service:fail");

        var silent = Healthy();
        silent[^1].GemRxBytes = silent[^2].GemRxBytes;
        silent[^1].GemTxBytes = silent[^2].GemTxBytes;
        Statuses(silent)[4].Should().Be("traffic:fail");
    }

    [Fact]
    public void ConnectionCheck_MissingSources_AreUnknownNotOk()
    {
        // An OLT without PON-ID and a source without OMCI state or byte counters.
        var points = Link(3);
        foreach (var p in points) p.GemTxBytes = null;

        Statuses(points).Should().Equal("light:unknown", "registered:ok", "management:unknown", "service:unknown", "traffic:unknown");
        PonChartSeries.ConnectionCheck([Point(0, 1000)]).Should().BeNull("an ONT that reports no state gets no check");
    }

    [Fact]
    public void SummarizeFec_CleanLine_IsOkWithAZeroRatio()
    {
        var f = PonChartSeries.SummarizeFec(Healthy())!;

        f.Level.Should().Be("ok");
        f.CorrectedBits.Should().Be(0);
        f.PreFecBitErrorRatio.Should().Be(0);
    }

    [Fact]
    public void SummarizeFec_Corrections_GiveTheRatioBeforeFec()
    {
        var points = Healthy(3);
        points[2].FecCorrectedWords = 40;
        points[2].FecCorrectedBits = 120;

        var f = PonChartSeries.SummarizeFec(points)!;

        f.Level.Should().Be("notice");
        f.CorrectedCodewords.Should().Be(40);
        // 120 bits in two intervals of 2.4 million frames of 38880 bytes.
        f.PreFecBitErrorRatio.Should().BeApproximately(120 / (2 * 2_400_000 * 38880.0 * 8), 1e-18);
    }

    [Fact]
    public void SummarizeFec_UncorrectableCodewords_AreAWarning()
    {
        var points = Healthy(3);
        points[2].FecErrors = 2;

        var f = PonChartSeries.SummarizeFec(points)!;

        f.UncorrectableCodewords.Should().Be(2);
        f.Level.Should().Be("warning");
        PonChartSeries.SummarizeFec([Point(0, 1000), Point(10, 2000)]).Should().BeNull("no FEC counters");
    }

    [Fact]
    public void SummarizeLaser_MoreCurrentForTheSameLight_IsHinted()
    {
        var points = Healthy(12);
        for (var i = 8; i < 12; i++) points[i].BiasMa = 16.5;

        var l = PonChartSeries.SummarizeLaser(points)!;

        l.BiasMa.Should().Be(16.5);
        l.WarnHighMa.Should().Be(90);
        l.BiasChangePercent.Should().BeApproximately(13.8, 0.05);
        l.TxPowerChangeDb.Should().Be(0);
        l.Hints.Should().Equal("bias_rise");
    }

    [Fact]
    public void SummarizeLaser_WarmerModuleOrHigherPower_IsNotAgeing()
    {
        var warmer = Healthy(12);
        for (var i = 8; i < 12; i++) { warmer[i].BiasMa = 16.5; warmer[i].OntTemperatureC = 60; }
        PonChartSeries.SummarizeLaser(warmer)!.Hints.Should().BeEmpty();

        var louder = Healthy(12);
        for (var i = 8; i < 12; i++) { louder[i].BiasMa = 16.5; louder[i].OntTxPowerDbm = 2.6; }
        PonChartSeries.SummarizeLaser(louder)!.Hints.Should().BeEmpty();

        var blind = Healthy(12);
        for (var i = 0; i < 12; i++) blind[i].OntTemperatureC = null;
        for (var i = 8; i < 12; i++) blind[i].BiasMa = 16.5;
        PonChartSeries.SummarizeLaser(blind)!.Hints.Should().BeEmpty("without the temperature the rise cannot be told from warming");
    }

    [Fact]
    public void SummarizeLaser_ModuleFlagsAndLimit_AreHinted()
    {
        var points = Healthy(3);
        points[2].BiasMa = 75;
        points[2].TransceiverAlarms = "warning:rx_power_low";

        var l = PonChartSeries.SummarizeLaser(points)!;

        l.ModuleAlarms.Should().Equal("warning:rx_power_low");
        l.Hints.Should().Equal("near_limit", "module_alarm");
        PonChartSeries.SummarizeLaser(Link(3)).Should().BeNull("no bias current reported");
    }

    [Fact]
    public void SummarizeProvider_QuietWindow_HasRatesAndNoEvents()
    {
        var s = PonChartSeries.SummarizeProvider(Healthy())!;

        s.OmciPerMinute.Should().Be(6);
        s.Services.Should().Be(3);
        s.PirMbps.Should().BeApproximately(1342.2, 0.05);
        s.CirMbps.Should().Be(s.PirMbps);
        s.Events.Should().BeEmpty();
    }

    [Fact]
    public void SummarizeProvider_WritesKeyRenewalRerangingAndRateChange_AreEvents()
    {
        var points = Healthy(6);
        for (var i = 2; i < 6; i++) points[i].MibDataSync = 163;
        for (var i = 3; i < 6; i++) { points[i].PloamRequestKey = 2; points[i].PloamKeySwitch = 4; }
        for (var i = 4; i < 6; i++) points[i].PloamRangingTime = 4;
        points[5].TrafficPirBytesPerSecond = 62_500_000;

        var s = PonChartSeries.SummarizeProvider(points)!;

        s.Events.Select(e => (e.Time, e.What)).Should().Equal(
            (T0.AddMinutes(10).ToString("o"), "config_written"),
            (T0.AddMinutes(15).ToString("o"), "key_renewed"),
            (T0.AddMinutes(20).ToString("o"), "reranged"),
            (T0.AddMinutes(25).ToString("o"), "rate_changed"));
        s.Events[^1].Should().Match<PonChartSeries.PonIdChange>(e => e.From == "1342.2 Mbit/s" && e.To == "500 Mbit/s");
    }

    [Fact]
    public void SummarizeProvider_RateAppearingWithTheField_IsNotAChange()
    {
        // Seen live: the first poll after the field began to be recorded has a rate, the one before none.
        var points = Healthy(4);
        points[0].TrafficPirBytesPerSecond = null;
        points[0].OmciUp = null;
        // And a poll where the OMCI daemon was silent reports no rate without the OLT having changed it.
        points[2].TrafficPirBytesPerSecond = null;
        points[2].OmciUp = 0;

        PonChartSeries.SummarizeProvider(points)!.Events.Should().BeEmpty();
    }

    [Fact]
    public void SummarizeProvider_AfterADropOrARestart_ReprovisioningIsNotNews()
    {
        // The link dropped and came back: a new activation ranges and keys again.
        var dropped = Healthy(5);
        Drop(dropped, 3, 20, los: 1);
        for (var i = 3; i < 5; i++) { dropped[i].PloamRangingTime = 4; dropped[i].PloamKeySwitch = 4; dropped[i].PloamRequestKey = 2; }
        PonChartSeries.SummarizeProvider(dropped)!.Events.Should().BeEmpty();

        // The ONT restarted: the OLT provisions it from nothing.
        var restarted = Healthy(5);
        for (var i = 3; i < 5; i++) { restarted[i].SfpUptimeS = 40 + 300 * (i - 3); restarted[i].MibDataSync = 12; restarted[i].OmciServices = 1; }
        PonChartSeries.SummarizeProvider(restarted)!.Events.Where(e => e.Time == T0.AddMinutes(15).ToString("o")).Should().BeEmpty();
    }

    [Fact]
    public void SummarizePathLoss_WanderingLoss_IsUnsteady()
    {
        // Back and forth by 1.4 dB with no lasting step: a connector, not a new level.
        var wobble = Enumerable.Range(0, 18).SelectMany(i => Steady(1, rx: i % 3 == 0 ? -17.9 : -16.5));

        var s = PonChartSeries.SummarizePathLoss(Path(wobble))!;

        s.SpreadDb.Should().BeApproximately(1.4, 1e-9);
        s.Jumps.Should().Be(11);
        s.Hints.Should().Equal("unsteady");
        PonChartSeries.SummarizePathLoss(Path(Steady(20)))!.Jumps.Should().Be(0);
        // A single step to a new level is one jump, not unsteadiness.
        PonChartSeries.SummarizePathLoss(Path(Steady(12).Concat(Steady(6, rx: -17.5))))!.Hints.Should().BeEmpty();
    }

    [Fact]
    public void Insights_NothingReported_IsNull()
    {
        PonChartSeries.Insights(null).Should().BeNull();
        PonChartSeries.Insights([Point(0, 1000), Point(10, 2000)]).Should().BeNull();
        PonChartSeries.Insights(Healthy()).Should().NotBeNull();
    }

    private static List<MonitoringInfluxClient.PonSeriesPoint> Ranged(params (long? Eqd, double? Loss)[] samples) =>
        samples.Select((x, i) => new MonitoringInfluxClient.PonSeriesPoint
        {
            Time = T0.AddMinutes(5 * i),
            SfpUptimeS = 1000 + 300 * i,
            EqdBits = x.Eqd,
            PathLossDb = x.Loss,
        }).ToList();

    [Fact]
    public void SummarizeRanging_NothingReported_IsNull()
    {
        PonChartSeries.SummarizeRanging(Ranged((null, 19.8), (null, 19.8))).Should().BeNull();
        PonChartSeries.SummarizeRanging(null).Should().BeNull();
    }

    [Fact]
    public void SummarizeRanging_SteadyDelay_GivesTheValueAndNoStep()
    {
        var s = PonChartSeries.SummarizeRanging(Ranged((283549, 19.8), (283551, 19.8), (null, 19.9), (283547, 19.8)))!;

        s.EqdBits.Should().Be(283547);
        s.EqdMicros.Should().Be(227.9);
        s.Steps.Should().BeEmpty("a new ranging lands a few bits off on the same fibre");
        s.PonIdStale.Should().BeFalse();
    }

    [Fact]
    public void SummarizeRanging_Step_NamesTheLengthAndTheLossAcrossIt()
    {
        // 610 bit times less delay: the OLT sees the ONT about 50 m further away.
        var s = PonChartSeries.SummarizeRanging(Ranged((283549, 19.8), (283549, 19.8), (282939, 21.3), (282939, 21.3)))!;

        var step = s.Steps.Should().ContainSingle().Subject;
        step.DeltaBits.Should().Be(-610);
        step.DeltaMetres.Should().BeApproximately(50.1, 0.2);
        step.LossChangeDb.Should().Be(1.5);
        step.Time.Should().Be(T0.AddMinutes(10).ToString("o"));
    }

    [Fact]
    public void SummarizeRanging_StepAcrossAMissedRead_IsStillOneStep()
    {
        var s = PonChartSeries.SummarizeRanging(Ranged((283549, 19.8), (null, null), (283749, null)))!;

        var step = s.Steps.Should().ContainSingle().Subject;
        step.DeltaBits.Should().Be(200);
        step.DeltaMetres.Should().BeLessThan(0, "more delay is a shorter fibre");
        step.LossChangeDb.Should().BeNull();
    }

    [Fact]
    public void SummarizeRanging_PonIdAge_IsStaleOnlyAfterTwoMinutes()
    {
        var fresh = Ranged((283549, 19.8), (283549, 19.8));
        fresh[1].PonIdAgeSeconds = 4.2;
        PonChartSeries.SummarizeRanging(fresh)!.PonIdStale.Should().BeFalse();

        var old = Ranged((null, 19.8), (null, 19.8));
        old[1].PonIdAgeSeconds = 5400;
        var s = PonChartSeries.SummarizeRanging(old)!;
        s.PonIdStale.Should().BeTrue();
        s.PonIdAgeSeconds.Should().Be(5400);
        s.EqdBits.Should().BeNull();
    }

    [Fact]
    public void CpuPercent_IsTheBusyShareOfTheInterval_AndNullAcrossARestart()
    {
        var a = new MonitoringInfluxClient.PonSeriesPoint { Time = T0, SfpUptimeS = 1000, CpuBusySeconds = 100, CpuTotalSeconds = 1000 };
        var b = new MonitoringInfluxClient.PonSeriesPoint { Time = T0.AddMinutes(5), SfpUptimeS = 1300, CpuBusySeconds = 115, CpuTotalSeconds = 1300 };
        var rebooted = new MonitoringInfluxClient.PonSeriesPoint { Time = T0.AddMinutes(10), SfpUptimeS = 40, CpuBusySeconds = 9, CpuTotalSeconds = 40 };

        PonChartSeries.CpuPercent(b, a).Should().Be(5.0);
        PonChartSeries.CpuPercent(rebooted, b).Should().BeNull();
        PonChartSeries.CpuPercent(a, null).Should().BeNull();
        PonChartSeries.CpuPercent(new MonitoringInfluxClient.PonSeriesPoint { Time = T0.AddMinutes(5), SfpUptimeS = 1300 }, a).Should().BeNull();
    }

}
