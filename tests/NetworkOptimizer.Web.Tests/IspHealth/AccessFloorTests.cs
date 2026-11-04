using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class AccessFloorTests
{
    private static readonly DateTime T0 = TestSeries.Start;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private static List<LatencySample> Hop(double rtt, double jitter = 0.2, double loss = 0) =>
        TestSeries.Flat(T0, Hour, rtt, jitter, loss);

    [Fact]
    public void Build_TakesLowestRttAtEachTimestamp()
    {
        var a = Hop(2.0);
        var b = Hop(1.8);

        var floor = AccessFloor.Build([a, b], a);

        floor.Should().HaveCount(60);
        floor.Should().OnlyContain(s => s.RttAvgMs == 1.8);
    }

    [Fact]
    public void Build_SpikeOnOneHop_IsIgnored()
    {
        var spiky = Hop(1.8).WithSegment(T0.AddMinutes(10), T0.AddMinutes(15), 9.0, 3.0);
        var steady = Hop(1.9);

        var floor = AccessFloor.Build([spiky, steady], spiky);

        floor.Max(s => s.RttAvgMs!.Value).Should().Be(1.9);
        floor.Max(s => s.JitterMs!.Value).Should().Be(0.2);
    }

    [Fact]
    public void Build_SpikeOnEveryHop_IsKept()
    {
        var from = T0.AddMinutes(10);
        var to = T0.AddMinutes(15);
        var a = Hop(1.8).WithSegment(from, to, 9.0, 3.0);
        var b = Hop(1.9).WithSegment(from, to, 8.0, 2.5);

        var floor = AccessFloor.Build([a, b], a);

        floor.Where(s => s.Time >= from && s.Time < to).Should().OnlyContain(s => s.RttAvgMs == 8.0 && s.JitterMs == 2.5);
    }

    [Fact]
    public void Build_RttMaxComesFromTheLowestHop()
    {
        var a = Hop(1.8, jitter: 0.5);
        var b = Hop(2.5, jitter: 0.1);

        var floor = AccessFloor.Build([a, b], a);

        floor.Should().OnlyContain(s => s.RttAvgMs == 1.8 && s.RttMaxMs == a[0].RttMaxMs && s.JitterMs == 0.1);
    }

    [Fact]
    public void Build_TooFewHopsAtATimestamp_UsesFallbackSample()
    {
        var gap = T0.AddMinutes(20);
        var fallback = Hop(1.8);
        var other = Hop(1.5).Where(s => s.Time != gap).ToList();
        var third = Hop(1.6).Where(s => s.Time != gap).ToList();

        var floor = AccessFloor.Build([fallback, other, third], fallback);

        floor.Single(s => s.Time == gap).RttAvgMs.Should().Be(1.8);
        floor.Where(s => s.Time != gap).Should().OnlyContain(s => s.RttAvgMs == 1.5);
    }

    [Fact]
    public void Build_TooFewHopsAndNoFallbackSample_EmitsNothing()
    {
        var gap = T0.AddMinutes(20);
        var fallback = Hop(1.8).Where(s => s.Time != gap).ToList();
        var other = Hop(1.5);

        var floor = AccessFloor.Build([fallback, other], fallback);

        floor.Should().NotContain(s => s.Time == gap);
        floor.Should().HaveCount(59);
    }

    [Fact]
    public void Build_SingleHop_ReturnsFallbackUnchanged()
    {
        var only = Hop(1.8).WithSegment(T0.AddMinutes(10), T0.AddMinutes(15), 9.0, 3.0);

        AccessFloor.Build([only], only).Should().Equal(only);
    }

    [Fact]
    public void Build_LossComesFromFallback()
    {
        var fallback = Hop(1.8, loss: 2.0);
        var other = Hop(1.5, loss: 0);

        var floor = AccessFloor.Build([fallback, other], fallback);

        floor.Should().OnlyContain(s => s.LossPercent == 2.0);
    }

    [Fact]
    public void Build_NoHops_ReturnsEmpty()
    {
        AccessFloor.Build([], []).Should().BeEmpty();
    }
}
