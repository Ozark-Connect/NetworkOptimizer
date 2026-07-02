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
    public void FitCouplingSlope_recovers_a_known_slope()
    {
        // RX rides temperature at exactly 0.10 dB/C across a 20 C spread; the fit should recover it.
        var samples = new List<(DateTime, double?, double?)>();
        var t0 = T0;
        foreach (var temp in new[] { 40.0, 44.0, 48.0, 52.0, 56.0, 60.0 })
            for (var k = 0; k < 3; k++)
            {
                samples.Add((t0, (double?)(-22.0 + 0.10 * (temp - 50.0)), (double?)temp));
                t0 = t0.AddMinutes(1);
            }

        OpticalSampleStats.FitCouplingSlope(samples).Should().BeApproximately(0.10, 0.005);
    }

    [Fact]
    public void FitCouplingSlope_is_null_when_temperature_is_pinned()
    {
        // No temperature spread to fit against - the optic is thermally pinned, so no slope.
        var samples = Enumerable.Range(0, 30)
            .Select(i => (T0.AddMinutes(i), (double?)(-22.6 - (i % 4) * 0.05), (double?)44.0))
            .ToList();

        OpticalSampleStats.FitCouplingSlope(samples).Should().BeNull();
    }

    [Fact]
    public void Detrend_normalizes_a_temperature_driven_dip()
    {
        // A single cooler poll reads RX low purely from the ~0.11 dB/C coupling (-23.4 dBm at 37 C vs
        // -22.6 at 44 C). Detrended to the reference temperature it lands back at baseline, so it must
        // NOT survive as the worst sample - a temperature swing is not an optical dip.
        var samples = new List<(DateTime, double?, double?)>();
        for (var i = 0; i < 11; i++) samples.Add((T0.AddMinutes(i), (double?)-22.6, (double?)44.0));
        samples.Add((T0.AddMinutes(11), -23.4, 37.0));   // temp-driven dip on the coupling line

        var stats = OpticalSampleStats.Compute(samples, couplingSlopeDbmPerC: 0.11);

        stats.WorstDbm.Should().BeGreaterThan(-22.8);      // -23.4 normalized away, not the worst
        stats.MedianDbm.Should().BeApproximately(-22.6, 0.05);
    }

    [Fact]
    public void Detrend_keeps_a_real_optical_dip_at_flat_temperature()
    {
        // RX dives with temperature unchanged - detrending does nothing (temp == reference), so the
        // real optical dip survives as the worst sample and drives scoring.
        var samples = new List<(DateTime, double?, double?)>();
        for (var i = 0; i < 11; i++) samples.Add((T0.AddMinutes(i), (double?)-22.6, (double?)44.0));
        samples.Add((T0.AddMinutes(11), -25.0, 44.0));   // real dip, temperature flat

        var stats = OpticalSampleStats.Compute(samples, couplingSlopeDbmPerC: 0.11);

        stats.WorstDbm.Should().BeApproximately(-25.0, 0.01);
    }

    [Fact]
    public void ComputeTx_spike_rejects_a_temperature_artifact_read()
    {
        // Steady 3.0 dBm TX plus one 3.9 dBm read on a garbage temperature (34 C below median): the
        // spike must be the clean 3.0, not the glitch, so a lone bad read can't trip the high-TX rule.
        var samples = new List<(DateTime, double?, double?)>();
        for (var i = 0; i < 12; i++) samples.Add((T0.AddMinutes(i), (double?)3.0, (double?)44.0));
        samples.Add((T0.AddMinutes(12), 3.9, 10.0));   // artifact: temp jump rides the same bad read

        var (median, spike) = OpticalSampleStats.ComputeTx(samples);

        median.Should().BeApproximately(3.0, 0.01);
        spike.Should().BeApproximately(3.0, 0.01);      // 3.9 glitch discarded
    }

    [Fact]
    public void ComputeTx_spike_keeps_a_real_high_tx_at_normal_temperature()
    {
        // A genuinely hot transmit sample at a normal temperature is not an artifact - it must survive
        // as the spike so the transmit-power-high rule can grade it, while the median stays typical.
        var samples = new List<(DateTime, double?, double?)>();
        for (var i = 0; i < 12; i++) samples.Add((T0.AddMinutes(i), (double?)3.0, (double?)44.0));
        samples.Add((T0.AddMinutes(12), 4.5, 44.0));   // real hot read, temperature normal

        var (median, spike) = OpticalSampleStats.ComputeTx(samples);

        median.Should().BeApproximately(3.0, 0.05);
        spike.Should().BeApproximately(4.5, 0.01);
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
