using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Tests for DDM read-artifact rejection. SFP sticks occasionally return a garbage DDM read where
/// RX dives while the reported temperature jumps tens of degrees in one sample; those must not drag
/// the RX statistics the Physical Link factor scores on.
/// </summary>
public class OpticalSampleStatsTests
{
    private static readonly DateTime T0 = new(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Rejects_ddm_artifacts_where_temperature_glitches_with_rx()
    {
        // 20 steady-state reads (~-22.7 dBm / ~44 C) plus the two real field artifacts:
        // -25.53 dBm @ 10.85 C and -24.69 dBm @ 23.85 C - temperature physically can't drop 20-33 C
        // in one poll, so the whole read is rejected.
        var samples = new List<(DateTime, double?, double?)>();
        for (var i = 0; i < 20; i++)
            samples.Add((T0.AddMinutes(i), -22.6 - (i % 3) * 0.1, 43.85));
        samples.Add((T0.AddMinutes(21), -25.53, 10.85));   // artifact
        samples.Add((T0.AddMinutes(22), -24.69, 23.85));   // artifact

        var stats = OpticalSampleStats.Compute(samples);

        stats.RejectedArtifacts.Should().Be(2);
        stats.MedianDbm.Should().BeInRange(-23.0, -22.4);
        // The coldest CLEAN sample, not the -25.53 artifact, so the worst-cap can't be tripped by garbage.
        stats.WorstDbm.Should().BeGreaterThan(-23.5);
    }

    [Fact]
    public void Keeps_all_samples_when_temperature_is_stable()
    {
        var samples = Enumerable.Range(0, 10)
            .Select(i => (T0.AddMinutes(i), (double?)(-22.5 - i * 0.05), (double?)44.0))
            .ToList();

        var stats = OpticalSampleStats.Compute(samples);

        stats.RejectedArtifacts.Should().Be(0);
        stats.CleanCount.Should().Be(10);
        stats.WorstDbm.Should().BeApproximately(-22.95, 0.01);
    }

    [Fact]
    public void Baseline_is_the_earliest_fifth_for_trend_detection()
    {
        // Earliest reads ~-19, drifting to ~-23: baseline should reflect the early ~-19, not the median.
        var samples = Enumerable.Range(0, 20)
            .Select(i => (T0.AddMinutes(i), (double?)(-19.0 - i * 0.2), (double?)44.0))
            .ToList();

        var stats = OpticalSampleStats.Compute(samples);

        stats.BaselineDbm.Should().BeInRange(-19.6, -19.0);
    }
}
