using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class ScoreCurveTests
{
    [Fact]
    public void Returns_first_anchor_score_at_or_below_first_value()
    {
        ScoreCurve.Interpolate(1.0, (2, 100), (3, 70)).Should().Be(100);
        ScoreCurve.Interpolate(2.0, (2, 100), (3, 70)).Should().Be(100);
    }

    [Fact]
    public void Returns_last_anchor_score_beyond_last_value()
    {
        ScoreCurve.Interpolate(99, (2, 100), (3, 70)).Should().Be(70);
    }

    [Fact]
    public void Interpolates_linearly_between_anchors()
    {
        ScoreCurve.Interpolate(2.5, (2, 100), (3, 70)).Should().Be(85);
        ScoreCurve.Interpolate(7.5, (5, 80), (10, 40)).Should().Be(60);
    }

    [Fact]
    public void Handles_duplicate_anchor_values()
    {
        ScoreCurve.Interpolate(2.4, (2, 100), (2, 88), (3, 70)).Should().BeApproximately(80.8, 0.01);
    }

    [Fact]
    public void Clamps_scores_to_valid_range()
    {
        ScoreCurve.Interpolate(5, (0, 150), (10, -20)).Should().BeInRange(0, 100);
    }

    [Fact]
    public void Falloff_returns_threshold_score_at_threshold()
    {
        ScoreCurve.ExponentialFalloff(0.05, 0.05, 70).Should().Be(70);
        ScoreCurve.ExponentialFalloff(0.01, 0.05, 70).Should().Be(70);
    }

    [Fact]
    public void Falloff_drops_drastically_past_threshold()
    {
        var atDouble = ScoreCurve.ExponentialFalloff(0.10, 0.05, 70);
        atDouble.Should().BeApproximately(70 * Math.Exp(-3), 0.1);
        atDouble.Should().BeLessThan(5);
    }

    [Fact]
    public void Falloff_is_monotonically_decreasing()
    {
        var previous = double.MaxValue;
        for (var loss = 0.05; loss <= 0.5; loss += 0.01)
        {
            var score = ScoreCurve.ExponentialFalloff(loss, 0.05, 70);
            score.Should().BeLessThanOrEqualTo(previous);
            previous = score;
        }
    }
}
