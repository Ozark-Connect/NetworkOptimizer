using FluentAssertions;
using NetworkOptimizer.Web.Services.Firmware;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Firmware;

/// <summary>
/// The bars that decide whether a device is worse, better, or the same after an upgrade.
///
/// Both floors are pinned in both directions because either one alone is wrong at an end of the
/// range: a relative floor alone turns 2% CPU becoming 4% into a regression, and an absolute floor
/// alone turns 60% becoming 68% into one. Only a move that clears BOTH is worth telling anyone
/// about, and the same pair decides an improvement so the two are never asymmetric.
/// </summary>
public class LitmusThresholdsTests
{
    private static RolloutResourceStats Stats(double? cpu, double? memory = 40, int samples = 20) =>
        new() { CpuPercent = cpu, MemoryUsedPercent = memory, SampleCount = samples };

    [Fact]
    public void CpuClearingBothFloors_IsARegression()
    {
        var comparison = LitmusThresholds.Compare(Stats(20), Stats(35));

        comparison.Verdict.Should().Be(ResourceComparisonVerdict.Regression);
        comparison.Detail.Should().Contain("CPU");
    }

    [Fact]
    public void CpuClearingBothFloorsDownwards_IsAnImprovement()
    {
        LitmusThresholds.Compare(Stats(35), Stats(20)).Verdict
            .Should().Be(ResourceComparisonVerdict.Improvement);
    }

    [Fact]
    public void ABigRelativeCpuMoveThatIsOnlyAFewPoints_IsNothing()
    {
        // 2% to 6% triples, and is still four points of nothing.
        LitmusThresholds.Compare(Stats(2), Stats(6)).Verdict
            .Should().Be(ResourceComparisonVerdict.Unchanged);
    }

    [Fact]
    public void ABigAbsoluteCpuMoveThatIsASmallShare_IsNothing()
    {
        // 60% to 70% is ten points but only a sixth, under the quarter the relative floor asks for.
        LitmusThresholds.Compare(Stats(60), Stats(70)).Verdict
            .Should().Be(ResourceComparisonVerdict.Unchanged);
    }

    [Fact]
    public void ExactlyOnBothCpuFloors_Counts()
    {
        // +10 points is the absolute floor and +25% of 40 is exactly ten points.
        LitmusThresholds.Compare(Stats(40), Stats(50)).Verdict
            .Should().Be(ResourceComparisonVerdict.Regression);
    }

    [Fact]
    public void MemoryMovesOnItsOwnFloorsWhenCpuIsFlat()
    {
        LitmusThresholds.Compare(Stats(20, memory: 40), Stats(20, memory: 50)).Verdict
            .Should().Be(ResourceComparisonVerdict.Regression);
    }

    [Fact]
    public void ASmallMemoryDriftOnAnIdleDeviceIsNothing()
    {
        // 3% to 7% more than doubles, and is four points - under the absolute floor.
        LitmusThresholds.Compare(Stats(20, memory: 3), Stats(20, memory: 7)).Verdict
            .Should().Be(ResourceComparisonVerdict.Unchanged);
    }

    [Fact]
    public void MemoryDroppingClearOfBothFloors_IsAnImprovement()
    {
        LitmusThresholds.Compare(Stats(20, memory: 60), Stats(20, memory: 45)).Verdict
            .Should().Be(ResourceComparisonVerdict.Improvement);
    }

    [Fact]
    public void CpuWinsWhenBothMetricsMoved()
    {
        var comparison = LitmusThresholds.Compare(Stats(20, memory: 60), Stats(40, memory: 40));

        comparison.Verdict.Should().Be(ResourceComparisonVerdict.Regression);
        comparison.Detail.Should().StartWith("CPU");
    }

    [Fact]
    public void NoSamplesOnEitherSide_IsInconclusive()
    {
        LitmusThresholds.Compare(null, Stats(20)).Verdict.Should().Be(ResourceComparisonVerdict.Inconclusive);
        LitmusThresholds.Compare(Stats(20), null).Verdict.Should().Be(ResourceComparisonVerdict.Inconclusive);
        LitmusThresholds.Compare(Stats(20), Stats(20, samples: 0)).Verdict
            .Should().Be(ResourceComparisonVerdict.Inconclusive);
    }

    [Fact]
    public void AMetricReportedOnOnlyOneSide_DoesNotVote()
    {
        LitmusThresholds.Compare(Stats(null, memory: null), Stats(80, memory: 90)).Verdict
            .Should().Be(ResourceComparisonVerdict.Inconclusive);
    }

    [Fact]
    public void AZeroBaselineIsDecidedByTheAbsoluteFloorAlone()
    {
        // Nothing divides sensibly by zero, so the points moved are all there is to go on.
        LitmusThresholds.Compare(Stats(0), Stats(15)).Verdict.Should().Be(ResourceComparisonVerdict.Regression);
        LitmusThresholds.Compare(Stats(0), Stats(4)).Verdict.Should().Be(ResourceComparisonVerdict.Unchanged);
    }

    [Fact]
    public void IsAppreciableIncrease_OnlyFiresUpwards()
    {
        LitmusThresholds.IsAppreciableIncrease(Stats(20), Stats(40)).Should().BeTrue();
        LitmusThresholds.IsAppreciableIncrease(Stats(40), Stats(20)).Should().BeFalse();
        LitmusThresholds.IsAppreciableIncrease(Stats(20), Stats(22)).Should().BeFalse();
    }

    [Fact]
    public void TheFloorsAreWhatTheApprovedBehaviorSays()
    {
        LitmusThresholds.CpuRelativeFraction.Should().Be(0.25);
        LitmusThresholds.CpuAbsolutePoints.Should().Be(10.0);
        LitmusThresholds.MemoryRelativeFraction.Should().Be(0.10);
        LitmusThresholds.LossFailPercent.Should().Be(5.0);
    }
}
