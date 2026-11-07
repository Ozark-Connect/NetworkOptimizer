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
            t.BaselineJitterMs, t.PeakJitterMs), Options);

    [Fact]
    public void FlatStep_NoLossSameJitter_ReadsAsARouteChange()
    {
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.4);
        Feed(t, now, TimeSpan.FromMinutes(30), 71, jitter: 0.45);

        t.Degraded.Should().BeTrue();
        t.LastWindow!.Signal.Should().Be(DegradationSignal.Rtt);
        t.BaselineJitterMs.Should().Be(0.4);
        t.PeakJitterMs.Should().Be(0.45);
        IsStep(t).Should().BeTrue();
    }

    [Fact]
    public void WideningJitter_ReadsAsCongestion()
    {
        var t = WithBaseline(out var now, rtt: 58, jitter: 0.4);
        now = Feed(t, now, TimeSpan.FromMinutes(20), 71, jitter: 0.45);
        // One later window queues: its jitter, not the episode's average, decides.
        Feed(t, now, TimeSpan.FromMinutes(10), 75, jitter: 3.0);

        t.Degraded.Should().BeTrue();
        t.PeakJitterMs.Should().Be(3.0);
        IsStep(t).Should().BeFalse();
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
    public void ResumeDegraded_KeepsThePeakJitter()
    {
        var t = WithBaseline(out var now, jitter: 0.4);
        t.ResumeDegraded(now, 0, 60, now, peakJitterMs: 2.5);
        t.PeakJitterMs.Should().Be(2.5);
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
