using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class HopLatencyBlendTests
{
    // A steady hop: alternates +-0.2 ms around its baseline, ending on the baseline.
    private static List<double> Steady(double baseline) =>
        Enumerable.Range(0, 30).Select(i => baseline + (i % 2 == 0 ? 0.2 : -0.2)).Append(baseline).ToList();

    private static List<double> Then(List<double> history, params double[] recent) => history.Concat(recent).ToList();

    [Fact]
    public void SteadyHops_ReadAsTheMeanOfTheirBaselines()
    {
        var value = HopLatencyBlend.Live([Steady(12), Steady(30)]);

        value.Should().BeApproximately(21, 0.05);
    }

    [Fact]
    public void ALoneSpikeOnOneHop_DoesNotMoveTheFigure()
    {
        // The noisy hop's current reply came back 20 ms slow; its previous two did not.
        var value = HopLatencyBlend.Live([Steady(12), Steady(18), Then(Steady(25), 25, 45)]);

        value.Should().BeApproximately((12 + 18 + 25) / 3.0, 0.1);
    }

    [Fact]
    public void ASustainedElevationOnAMinorityOfHops_StillShows()
    {
        // One of three hops is 9 ms slower for its last three readings: real, and it must not be hidden.
        var value = HopLatencyBlend.Live([Steady(12), Steady(18), Then(Steady(25), 34, 34, 34)]);

        // The full share is +3 ms. The elevated readings raise that hop's own jitter, trimming its weight a little.
        value.Should().BeInRange((12 + 18 + 25) / 3.0 + 2.3, (12 + 18 + 25) / 3.0 + 3);
    }

    [Fact]
    public void EveryHopElevated_RaisesTheFigureByTheElevation()
    {
        var value = HopLatencyBlend.Live([Then(Steady(12), 17, 17, 17), Then(Steady(18), 23, 23, 23), Then(Steady(25), 30, 30, 30)]);

        value.Should().BeApproximately((12 + 18 + 25) / 3.0 + 5, 0.2);
    }

    [Fact]
    public void APersistentlyJitteryHop_CountsLess()
    {
        // The jittery hop swings +-8 ms, so its weight is low; its sustained +9 ms moves the figure less than a steady hop's would.
        var jittery = Enumerable.Range(0, 30).Select(i => 25 + (i % 3 - 1) * 8.0).ToList();
        var jitteryUp = HopLatencyBlend.Live([Steady(12), Steady(18), Then(jittery, 34, 34, 34)]);
        var steadyUp = HopLatencyBlend.Live([Steady(12), Steady(18), Then(Steady(25), 34, 34, 34)]);

        jitteryUp.Should().BeLessThan(steadyUp!.Value);
        jitteryUp.Should().BeGreaterThan((12 + 18 + 25) / 3.0);
    }

    [Fact]
    public void NoHopSettled_ReadsTheMeanOfTheCurrentValues()
    {
        HopLatencyBlend.Live([[12.0], [14.0], [16.0]]).Should().Be(14);
    }

    [Fact]
    public void HopsSettlingOneByOne_DoNotStepTheFigure()
    {
        var none = HopLatencyBlend.Live([[7.0], [15.0], [24.0]]);
        var one = HopLatencyBlend.Live([Steady(7), [15.0], [24.0]]);
        var all = HopLatencyBlend.Live([Steady(7), Steady(15), Steady(24)]);

        none.Should().BeApproximately(15.33, 0.05);
        one.Should().BeApproximately(15.33, 0.05);
        all.Should().BeApproximately(15.33, 0.05);
    }

    [Fact]
    public void NoHop_ReadsNull()
    {
        HopLatencyBlend.Live([]).Should().BeNull();
    }

    [Fact]
    public void Series_BlendsEachTimestamp_FromEachHopsOwnWindowProfile()
    {
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        IReadOnlyList<(DateTime, double)> Hop(List<double> values) =>
            values.Select((v, i) => (t0.AddMinutes(i), v)).ToList();
        var elevated = Steady(25);
        elevated[^1] = 34; // a whole window 9 ms up on one of three hops

        var series = HopLatencyBlend.Series([Hop(Steady(12)), Hop(Steady(18)), Hop(elevated)]);

        series.Should().HaveCount(31);
        series[0].Value.Should().BeApproximately((12 + 18 + 25) / 3.0, 0.3);
        series[^1].Value.Should().BeInRange((12 + 18 + 25) / 3.0 + 2.3, (12 + 18 + 25) / 3.0 + 3);
    }
}
