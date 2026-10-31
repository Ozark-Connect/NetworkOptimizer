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
    private static DateTime Feed(TargetDegradation t, DateTime from, TimeSpan span, double rtt, int lostPerBurst = 0, bool exclude = false)
    {
        var at = from;
        for (; at < from + span; at += TimeSpan.FromSeconds(15))
            t.Record(at, rtt, 5, 5 - lostPerBurst, () => exclude);
        return at;
    }

    private static TargetDegradation WithBaseline(out DateTime now, double rtt = 42)
    {
        var t = new TargetDegradation("aws-us-east-1", Options);
        now = Feed(t, T0, TimeSpan.FromHours(2), rtt);
        return t;
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
    public void FewerThanTwentyProbes_LossIsNotRead()
    {
        var t = WithBaseline(out var now);
        // A burst every 100 s puts 3 bursts (15 probes) in a window, short of the 20-probe floor, so
        // even 40% loss is not read.
        for (var i = 0; i < 20; i++)
            t.Record(now.AddSeconds(i * 100), 42, 5, 3, () => false);
        t.Degraded.Should().BeFalse();
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
            .Select(i => (T0.AddMinutes(5 * i), (double?)33.0, (double?)0.0))
            .ToList();
        // One past episode span carrying 85 ms is left out.
        history[100] = (history[100].Item1, 85.0, 10.0);
        t.Seed(history, new[] { (T0.AddMinutes(500), T0.AddMinutes(505)) });

        t.HasBaseline.Should().BeTrue();
        t.BaselineRttMs.Should().Be(33);
        t.BaselineLossPct.Should().Be(0);
        t.Degraded.Should().BeFalse();
    }

    [Fact]
    public void ReplayedBacklog_OlderThanTheOpenWindow_IsDropped()
    {
        var t = WithBaseline(out var now);
        t.Record(now.AddHours(-1), 500, 5, 0, () => false).Should().BeNull();
        t.LastSampleAt.Should().BeBefore(now);
    }
}
