using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring.PathLocalization;

/// <summary>
/// The trigger (spec 3.2): a target degrades against its OWN baseline after three degraded 5-minute
/// windows in a row and clears after three clean ones. Bursts here are 5 pings every 15 s, the shape
/// the 20-probe floor was written for.
/// </summary>
public class TargetDegradationTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime T0 = new(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Feeds bursts every 15 s for a span; each burst loses <paramref name="lostPerBurst"/> of 5.</summary>
    private static DateTime Feed(TargetDegradation t, DateTime from, TimeSpan span, double rtt, int lostPerBurst = 0, bool exclude = false,
        double? jitter = null)
    {
        var at = from;
        for (; at < from + span; at += TimeSpan.FromSeconds(15))
            t.Record(at, rtt, 5, 5 - lostPerBurst, () => exclude, jitter);
        return at;
    }

    private static TargetDegradation WithBaseline(out DateTime now, double rtt = 42, double? jitter = null)
    {
        var t = new TargetDegradation("aws-us-east-1", Options);
        now = Feed(t, T0, TimeSpan.FromHours(2), rtt, jitter: jitter);
        return t;
    }

    private static bool IsStep(TargetDegradation t) =>
        PathShiftMatch.IsStep(new EpisodeTargetLevels(t.TargetId, t.BaselineRttMs, t.PeakRttMs, t.PeakLossPct,
            t.BaselineJitterMs, t.JitterP95Since(t.DegradedSince!.Value)), Options);

    [Fact]
    public void FlatStep_NoLossSameJitter_ReadsAsARouteChange()
    {
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.4);
        Feed(t, now, TimeSpan.FromMinutes(30), 71, jitter: 0.45);

        t.Degraded.Should().BeTrue();
        t.LastWindow!.Signal.Should().Be(DegradationSignal.Rtt);
        t.BaselineJitterMs.Should().Be(0.4);
        t.JitterP95Since(t.DegradedSince!.Value).Should().BeApproximately(0.45, 1e-9);
        IsStep(t).Should().BeTrue();
    }

    [Fact]
    public void WideningJitter_ReadsAsCongestion()
    {
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.4);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 71, jitter: 0.45);
        // Half an hour of queueing is well over a twentieth of the span.
        Feed(t, now, TimeSpan.FromMinutes(30), 75, jitter: 3.0);

        t.Degraded.Should().BeTrue();
        t.JitterP95Since(t.DegradedSince!.Value).Should().BeApproximately(3.0, 1e-9);
        IsStep(t).Should().BeFalse();
    }

    [Fact]
    public void OneJitterBlipInALongStep_IsStillAStep()
    {
        // Two hours flat with one 5-minute blip: the 95th percentile looks past it, the worst window would not.
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.26);
        now = Feed(t, now, TimeSpan.FromMinutes(60), 70, jitter: 0.3);
        now = Feed(t, now, TimeSpan.FromMinutes(5), 70, jitter: 1.7);
        Feed(t, now, TimeSpan.FromMinutes(60), 70, jitter: 0.3);

        t.Degraded.Should().BeTrue();
        t.JitterP95Since(t.DegradedSince!.Value).Should().BeLessThan(0.5);
        IsStep(t).Should().BeTrue();
    }

    [Fact]
    public void AfterARestart_SeededWindowsGiveThePercentile_OrNullWithoutJitter()
    {
        var opened = T0.AddHours(20);
        var history = Enumerable.Range(0, 288)
            .Select(i => T0.AddMinutes(5 * i))
            .Select(s => (s, (double?)(s >= opened ? 70.0 : 58.0), (double?)0.0, (double?)(s >= opened ? 0.5 : 0.4)))
            .ToList();
        var t = new TargetDegradation("x", Options);
        t.Seed(history, new[] { (opened, T0.AddHours(25)) });
        t.ResumeDegraded(opened, 0, 70, T0.AddHours(24));
        t.JitterP95Since(opened).Should().BeApproximately(0.5, 1e-9);

        var bare = new TargetDegradation("y", Options);
        bare.Seed(history.Select(h => (h.Item1, h.Item2, h.Item3, (double?)null)), new[] { (opened, T0.AddHours(25)) });
        bare.ResumeDegraded(opened, 0, 70, T0.AddHours(24));
        bare.JitterP95Since(opened).Should().BeNull();
    }

    [Fact]
    public void JitterPercentile_FromTheEpisodesOpen_KeepsARunBeforeAClear()
    {
        // Queued for half an hour, cleared, then degraded again flat: the episode's span still holds the queue.
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.3);
        var opened = now;
        now = Feed(t, now, TimeSpan.FromMinutes(30), 75, jitter: 3.0);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 58, jitter: 0.3);
        t.Degraded.Should().BeFalse();
        Feed(t, now, TimeSpan.FromMinutes(30), 70, jitter: 0.3);

        t.Degraded.Should().BeTrue();
        t.JitterP95Since(t.DegradedSince!.Value).Should().BeApproximately(0.3, 1e-9);
        t.JitterP95Since(opened).Should().BeApproximately(3.0, 1e-9);
    }

    [Fact]
    public void QueuedSince_SustainedLossOrElevatedJitter_IsAQueue()
    {
        var lossy = WithBaseline(out var now, jitter: 0.3);
        var from = now;
        Feed(lossy, now, TimeSpan.FromMinutes(20), 42, lostPerBurst: 1, jitter: 0.3);
        lossy.QueuedSince(from).Should().BeTrue();

        var jittery = WithBaseline(out now, jitter: 0.3);
        Feed(jittery, now, TimeSpan.FromMinutes(20), 60, jitter: 2.0);
        jittery.QueuedSince(from).Should().BeTrue();
    }

    [Fact]
    public void QueuedSince_AFlatStepOrOneLossyWindow_IsNotAQueue()
    {
        var flat = WithBaseline(out var now, jitter: 0.3);
        var from = now;
        Feed(flat, now, TimeSpan.FromMinutes(30), 60, jitter: 0.35);
        flat.QueuedSince(from).Should().BeFalse("a route change on another target is not a queue");

        // One lossy window never makes a degraded run, so it is noise, not congestion.
        var blip = WithBaseline(out now, jitter: 0.3);
        now = Feed(blip, now, TimeSpan.FromMinutes(5), 42, lostPerBurst: 1, jitter: 0.3);
        Feed(blip, now, TimeSpan.FromMinutes(30), 42, jitter: 0.3);
        blip.QueuedSince(from).Should().BeFalse();
    }

    [Fact]
    public void LossOnTheRise_ReadsAsCongestion()
    {
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.4);
        Feed(t, now, TimeSpan.FromMinutes(30), 71, lostPerBurst: 1, jitter: 0.45);

        t.Degraded.Should().BeTrue();
        t.PeakLossPct.Should().BeApproximately(20, 0.01);
        IsStep(t).Should().BeFalse();
    }

    [Fact]
    public void NoJitterReported_HasNoJitterBaseline_AndIsNotAStep()
    {
        var t = WithBaseline(out var now, rtt: 58);
        Feed(t, now, TimeSpan.FromMinutes(30), 71);

        t.Degraded.Should().BeTrue();
        t.BaselineJitterMs.Should().BeNull();
        IsStep(t).Should().BeFalse();
    }

    [Fact]
    public void NoBaseline_NeverJudges()
    {
        var t = new TargetDegradation("x", Options);
        var end = Feed(t, T0, TimeSpan.FromMinutes(30), 100, lostPerBurst: 2);

        t.HasBaseline.Should().BeFalse();
        t.Degraded.Should().BeFalse();
        t.IsClean(end).Should().BeFalse("a target with no baseline is unknown, never a clean control");
    }

    [Fact]
    public void LossOverBaseline_ForThreeWindows_Degrades()
    {
        var t = WithBaseline(out var now);
        // 1 of 5 lost per burst = 20% loss, far over max(2%, 0 + 2pp).
        now = Feed(t, now, TimeSpan.FromMinutes(10), 42, lostPerBurst: 1);
        t.Degraded.Should().BeFalse("two windows are not sustained");

        now = Feed(t, now, TimeSpan.FromMinutes(5), 42, lostPerBurst: 1);
        t.Record(now, 42, 5, 4, () => false); // closes the third window
        t.Degraded.Should().BeTrue();
        t.DegradedSince.Should().Be(T0.AddHours(2));
        t.PeakLossPct.Should().BeApproximately(20, 0.01);
    }

    [Fact]
    public void RttRise_UsesTheLargerOfTheFloorAndTheFraction()
    {
        // Baseline 42 ms: the bar is 42 + max(8, 8.4) = 50.4. An observed us-east rise to 55 clears it,
        // a rise to 49 does not.
        var t = WithBaseline(out var now);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 49);
        t.Degraded.Should().BeFalse();

        now = Feed(t, now, TimeSpan.FromMinutes(20), 55);
        t.Degraded.Should().BeTrue();
        t.LastWindow!.Signal.Should().Be(DegradationSignal.Rtt);
    }

    [Fact]
    public void FewerThanTwentyProbes_PoolWithTheWindowsBefore()
    {
        var t = WithBaseline(out var now);
        // A burst every 100 s puts 3 bursts (15 probes) in a window, short of the 20-probe floor on its
        // own, so each window is read together with the one before it.
        for (var i = 0; i < 20; i++)
            t.Record(now.AddSeconds(i * 100), 42, 5, 3, () => false);
        t.Degraded.Should().BeTrue();
    }

    [Fact]
    public void LossSince_PoolsTheWindowsFromAPoint()
    {
        var t = WithBaseline(out var now);
        var from = now;
        now = Feed(t, now, TimeSpan.FromMinutes(10), 42, lostPerBurst: 1);
        t.Record(now, 42, 5, 5, () => false);

        t.LossSince(from)!.Value.LossPct.Should().BeApproximately(20, 0.01);

        // Clean windows after the span leave a bounded figure alone.
        var until = now;
        now = Feed(t, now, TimeSpan.FromMinutes(10), 42);
        t.Record(now, 42, 5, 5, () => false);
        t.LossSince(from, until)!.Value.LossPct.Should().BeApproximately(20, 0.01);
        t.LossSince(from)!.Value.LossPct.Should().BeLessThan(20);
    }

    [Fact]
    public void ThreeCleanWindows_Clear()
    {
        var t = WithBaseline(out var now);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 42, lostPerBurst: 1);
        t.Degraded.Should().BeTrue();

        now = Feed(t, now, TimeSpan.FromMinutes(10), 42);
        t.Degraded.Should().BeTrue("two clean windows are not enough to clear");
        now = Feed(t, now, TimeSpan.FromMinutes(10), 42);
        t.Degraded.Should().BeFalse();
        t.ClearedAt.Should().NotBeNull();
        t.IsClean(now).Should().BeTrue();
    }

    [Fact]
    public void ExcludedWindows_StayOutOfTheBaseline()
    {
        // An evening of 85 ms inside an episode must not drag the baseline up to meet it.
        var t = WithBaseline(out var now, rtt: 32);
        now = Feed(t, now, TimeSpan.FromHours(3), 85, exclude: true);

        t.BaselineRttMs.Should().Be(32);
        t.Degraded.Should().BeTrue();
    }

    [Fact]
    public void Seed_ProvidesABaselineWithoutMovingState()
    {
        var t = new TargetDegradation("x", Options);
        var history = Enumerable.Range(0, 288)
            .Select(i => (T0.AddMinutes(5 * i), (double?)33.0, (double?)0.0, (double?)0.4))
            .ToList();
        // One past episode span carrying 85 ms is left out.
        history[100] = (history[100].Item1, 85.0, 10.0, 6.0);
        t.Seed(history, new[] { (T0.AddMinutes(500), T0.AddMinutes(505)) });

        t.HasBaseline.Should().BeTrue();
        t.BaselineRttMs.Should().Be(33);
        t.BaselineLossPct.Should().Be(0);
        t.BaselineJitterMs.Should().Be(0.4);
        t.Degraded.Should().BeFalse();
    }

    [Fact]
    public void RttDegradation_WithOneLostProbe_ReportsNoPeakLoss()
    {
        // One probe of 100 in a window is 1% loss, under the 2% floor: noise, not packet loss to report.
        var t = WithBaseline(out var now);
        now = Feed(t, now, TimeSpan.FromMinutes(7), 60);
        t.Record(now, 60, 5, 4, () => false);
        now = Feed(t, now.AddSeconds(15), TimeSpan.FromMinutes(15), 60);

        t.Degraded.Should().BeTrue();
        t.PeakLossPct.Should().Be(0);
        t.PeakRttMs.Should().Be(60);
    }

    [Fact]
    public void LossInAnRttDegradation_StillCountsWhenItReadsAsLoss()
    {
        var t = WithBaseline(out var now);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 60);
        t.Degraded.Should().BeTrue();
        t.PeakLossPct.Should().Be(0);

        now = Feed(t, now, TimeSpan.FromMinutes(5), 60, lostPerBurst: 1);
        t.Record(now, 60, 5, 5, () => false);
        t.PeakLossPct.Should().BeApproximately(20, 0.01);
    }

    [Fact]
    public void LastCleanRunAt_MovesOnlyWhenAFullCleanRunCloses()
    {
        var t = WithBaseline(out var now);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 60);
        t.Degraded.Should().BeTrue();
        var before = t.LastCleanRunAt;
        before.Should().NotBeNull();

        now = Feed(t, now, TimeSpan.FromMinutes(10), 42);
        t.LastCleanRunAt.Should().Be(before, "two clean windows are not a clean run");
        Feed(t, now, TimeSpan.FromMinutes(10), 42);
        t.LastCleanRunAt.Should().BeAfter(before!.Value);
        t.LastCleanRunAt.Should().Be(t.ClearedAt);
    }

    [Fact]
    public void ReplayedBacklog_OlderThanTheOpenWindow_IsDropped()
    {
        var t = WithBaseline(out var now);
        t.Record(now.AddHours(-1), 500, 5, 0, () => false).Should().BeNull();
        t.LastSampleAt.Should().BeBefore(now);
    }
}
