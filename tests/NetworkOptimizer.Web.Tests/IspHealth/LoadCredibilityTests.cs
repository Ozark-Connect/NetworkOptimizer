using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Not every loaded moment is equally good evidence about behavior under load. A brief burst is
/// where load CLASSIFICATION goes wrong most often and is too short for buffers to fill; a
/// sustained saturation near plan speed is the best evidence available, better than a speed test,
/// which is itself short and synthetic.
/// </summary>
public class LoadCredibilityTests
{
    private const int WindowSeconds = 7;

    private static DateTime W(int index) => new DateTime(2026, 8, 5, 0, 0, 0, DateTimeKind.Utc)
        .AddSeconds(index * WindowSeconds);

    [Fact]
    public void Consecutive_windows_are_one_episode_and_carry_its_full_length()
    {
        // Three back-to-back windows are one 21-second episode, not three 7-second ones.
        var seconds = SeriesStats.LoadEpisodeSeconds(new[] { W(0), W(1), W(2) }, WindowSeconds);

        seconds.Values.Should().AllBeEquivalentTo(21.0);
    }

    [Fact]
    public void A_gap_starts_a_new_episode()
    {
        // W(0..1) then a hole then W(5): two episodes, measured separately.
        var seconds = SeriesStats.LoadEpisodeSeconds(new[] { W(0), W(1), W(5) }, WindowSeconds);

        seconds[W(0)].Should().Be(14);
        seconds[W(1)].Should().Be(14);
        seconds[W(5)].Should().Be(7);
    }

    [Fact]
    public void Order_and_duplicates_do_not_change_an_episode()
    {
        var seconds = SeriesStats.LoadEpisodeSeconds(new[] { W(2), W(0), W(1), W(1) }, WindowSeconds);

        seconds.Should().HaveCount(3);
        seconds.Values.Should().AllBeEquivalentTo(21.0);
    }

    [Fact]
    public void A_short_burst_counts_for_less_than_a_sustained_saturation()
    {
        const double fullAt = 60, floor = 0.15;

        var burst = SeriesStats.Credibility(7, fullAt, floor);
        var sustained = SeriesStats.Credibility(120, fullAt, floor);

        burst.Should().BeLessThan(sustained);
        sustained.Should().Be(1);
        // Weak evidence, never absent evidence.
        burst.Should().BeGreaterThanOrEqualTo(floor);
    }

    [Fact]
    public void Utilization_is_judged_across_the_band_where_it_can_discriminate()
    {
        // Everything here is already classified loaded at 50% of plan, so a ramp from zero would
        // score every episode near the top. The band starts above that threshold instead.
        const double start = 0.60, full = 0.90, floor = 0.15;

        SeriesStats.CredibilityBetween(0.55, start, full, floor).Should().Be(floor);
        SeriesStats.CredibilityBetween(0.75, start, full, floor).Should().BeApproximately(0.5, 0.001);
        SeriesStats.CredibilityBetween(0.90, start, full, floor).Should().Be(1);
        SeriesStats.CredibilityBetween(1.20, start, full, floor).Should().Be(1);

        // The naive ramp for comparison: 55% and 75% are nearly indistinguishable, which is the
        // failure this band exists to avoid.
        SeriesStats.Credibility(0.55, full, floor).Should().BeApproximately(0.61, 0.01);
        SeriesStats.Credibility(0.75, full, floor).Should().BeApproximately(0.83, 0.01);
    }

    [Fact]
    public void A_weighted_mean_is_used_for_loss_because_a_median_of_mostly_zeros_is_zero()
    {
        // Loss is a rate: most samples are zero even on a line dropping traffic under load, so a
        // median reports zero however bad the rest are. The mean carries them.
        var samples = new[] { (0.0, 1.0), (0.0, 1.0), (0.0, 1.0), (8.0, 1.0), (8.0, 1.0) };

        SeriesStats.WeightedMedian(samples).Should().Be(0);
        SeriesStats.WeightedMean(samples).Should().BeApproximately(3.2, 0.001);
    }

    [Fact]
    public void Credible_load_outweighs_doubtful_load_in_the_reported_loss()
    {
        // Same two readings, one from a long saturation and one from a two-second blip: the
        // sustained one decides the number.
        var trusted = new[] { (6.0, 1.0), (0.0, 0.15) };
        var doubted = new[] { (6.0, 0.15), (0.0, 1.0) };

        SeriesStats.WeightedMean(trusted).Should().BeApproximately(5.22, 0.01);
        SeriesStats.WeightedMean(doubted).Should().BeApproximately(0.78, 0.01);
    }

    [Fact]
    public void Nothing_credible_is_null_rather_than_zero()
    {
        SeriesStats.WeightedMean(new[] { (5.0, 0.0) }).Should().BeNull();
        SeriesStats.WeightedMean(Array.Empty<(double, double)>()).Should().BeNull();
    }
}
