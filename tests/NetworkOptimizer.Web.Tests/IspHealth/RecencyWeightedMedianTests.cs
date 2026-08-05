using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Loaded latency is read from WAN speed tests, and a plain median over the window treated a test
/// from an hour ago exactly like one from six days ago - so a line fixed this afternoon went on
/// reporting bufferbloat until the good tests outnumbered the bad, which on a daily schedule takes
/// a week. Weighting by recency answers "is it fixed NOW" without giving up the median's refusal to
/// swing on one sample.
/// </summary>
public class RecencyWeightedMedianTests
{
    // The shipped default. Shorter and the newest sample outweighs everything before it on a
    // daily test schedule, which stops being a median at all - the last test in this file is what
    // pins that down.
    private const double HalfLifeHours = 48;

    private static (double Value, double Weight) Sample(double value, double ageHours) =>
        (value, SeriesStats.RecencyWeight(TimeSpan.FromHours(ageHours), HalfLifeHours));

    [Fact]
    public void With_no_decay_it_is_the_plain_median()
    {
        var samples = new[] { (1.0, 1.0), (5.0, 1.0), (30.0, 1.0) };

        SeriesStats.WeightedMedian(samples).Should().Be(5.0);
    }

    [Fact]
    public void RecencyWeight_halves_every_half_life()
    {
        SeriesStats.RecencyWeight(TimeSpan.Zero, HalfLifeHours).Should().Be(1);
        SeriesStats.RecencyWeight(TimeSpan.FromHours(48), HalfLifeHours).Should().BeApproximately(0.5, 0.001);
        SeriesStats.RecencyWeight(TimeSpan.FromHours(96), HalfLifeHours).Should().BeApproximately(0.25, 0.001);
        // Opting out restores equal weighting.
        SeriesStats.RecencyWeight(TimeSpan.FromDays(30), 0).Should().Be(1);
    }

    [Fact]
    public void Three_clean_runs_outweigh_a_week_of_bad_ones()
    {
        // The WAN4 case: ~+23 ms every morning for a week, then the line is fixed and the last
        // three runs come back clean. The plain median still reads ~23 and keeps the finding up.
        var samples = new List<(double, double)>
        {
            Sample(0, 1), Sample(0, 5), Sample(0, 7),
        };
        for (var day = 1; day <= 7; day++) samples.Add(Sample(23, day * 24));

        SeriesStats.Median(samples.Select(s => s.Item1).ToList()).Should().Be(23);
        SeriesStats.WeightedMedian(samples).Should().Be(0);
    }

    [Fact]
    public void One_clean_run_does_not_clear_a_standing_finding()
    {
        // The other half of the bargain: it is still a median, so a single good test among bad
        // ones cannot call the fault fixed.
        var samples = new List<(double, double)> { Sample(0, 1) };
        for (var day = 1; day <= 7; day++) samples.Add(Sample(23, day * 24));

        SeriesStats.WeightedMedian(samples).Should().Be(23);
    }

    [Fact]
    public void One_bad_run_does_not_raise_a_finding_on_its_own()
    {
        var samples = new List<(double, double)> { Sample(40, 1) };
        for (var day = 1; day <= 5; day++) samples.Add(Sample(2, day * 24));

        SeriesStats.WeightedMedian(samples).Should().Be(2);
    }

    [Fact]
    public void Nothing_to_weigh_is_null()
    {
        SeriesStats.WeightedMedian(Array.Empty<(double, double)>()).Should().BeNull();
        SeriesStats.WeightedMedian(new[] { (5.0, 0.0) }).Should().BeNull();
    }
}
