using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Unit tests for the Satellite (Starlink) arm of the Physical Link scorer:
/// obstruction/drop-rate/outage blending, the SNR and dish-alert caps, and the
/// LAN-side Ethernet advisory.
/// </summary>
public class PhysicalLinkScorerSatelliteTests
{
    private const double Weight = 0.15;

    private static PhysicalLinkResult ScoreSat(
        double? obstruction = null, double? dropAvg = null, double? outageSeconds = null,
        long? outageCount = null, bool? snrLow = null, string[]? alerts = null,
        int? ethSpeed = null, double windowDays = 2.0) =>
        PhysicalLinkScorer.Score(new PhysicalLinkInput
        {
            Medium = PhysicalMedium.Satellite,
            SourceName = "Test Starlink",
            ObstructionFraction = obstruction,
            DishDropRateAvg = dropAvg,
            OutageSecondsTotal = outageSeconds,
            OutageCountTotal = outageCount,
            SnrPersistentlyLow = snrLow,
            DishAlerts = alerts,
            EthSpeedMbps = ethSpeed,
            WindowDays = windowDays
        }, expectedUploadMbps: null, Weight);

    [Fact]
    public void Satellite_clear_sky_scores_high_with_no_issues()
    {
        // TJ's real dish: 0.06% obstructed, near-zero drop, no outages in window.
        var result = ScoreSat(obstruction: 0.0006, dropAvg: 0.001, outageSeconds: 0);
        result.Factor.Score.Should().BeGreaterThan(90);
        result.Issues.Should().BeEmpty();
        result.Factor.Weight.Should().Be(Weight);
    }

    [Fact]
    public void Satellite_heavy_obstruction_penalized_and_raises_warning()
    {
        var result = ScoreSat(obstruction: 0.05, dropAvg: 0.001, outageSeconds: 0);
        result.Factor.Score.Should().BeLessThan(75);
        result.Issues.Should().Contain(i => i.Title.Contains("obstruction"));
    }

    [Fact]
    public void Satellite_high_drop_rate_penalized_and_raises_warning()
    {
        var result = ScoreSat(obstruction: 0.0005, dropAvg: 0.08, outageSeconds: 0);
        result.Factor.Score.Should().BeLessThan(75);
        result.Issues.Should().Contain(i => i.Title.Contains("packet loss"));
    }

    [Fact]
    public void Satellite_outage_burden_drags_score_and_notes_anomaly()
    {
        // ~600 s/day of dead air over a 2-day window.
        var result = ScoreSat(obstruction: 0.0005, dropAvg: 0.001, outageSeconds: 1200, outageCount: 20);
        result.Factor.Score.Should().BeLessThan(90);
        result.Issues.Should().Contain(i => i.Title.Contains("outages"));
        result.Factor.Description.Should().Contain("anomaly");
    }

    [Fact]
    public void Satellite_persistently_low_snr_caps_score()
    {
        var result = ScoreSat(obstruction: 0.0005, dropAvg: 0.001, outageSeconds: 0, snrLow: true);
        result.Factor.Score.Should().BeLessThanOrEqualTo(40);
        result.Issues.Should().Contain(i => i.Title.Contains("SNR"));
    }

    [Fact]
    public void Satellite_thermal_shutdown_caps_to_critical()
    {
        var result = ScoreSat(obstruction: 0.0005, dropAvg: 0.001, outageSeconds: 0,
            alerts: new[] { "thermal_shutdown" });
        result.Factor.Score.Should().BeLessThanOrEqualTo(10);
        result.Issues.Should().Contain(i => i.Severity == IspIssueSeverity.Critical);
    }

    [Fact]
    public void Satellite_slow_ethernet_is_advisory_only()
    {
        var withSlowEth = ScoreSat(obstruction: 0.0005, dropAvg: 0.001, outageSeconds: 0, ethSpeed: 100);
        var withFastEth = ScoreSat(obstruction: 0.0005, dropAvg: 0.001, outageSeconds: 0, ethSpeed: 1000);
        withSlowEth.Factor.Score.Should().Be(withFastEth.Factor.Score);
        withSlowEth.Issues.Should().ContainSingle(i => i.Severity == IspIssueSeverity.Info);
        withFastEth.Issues.Should().BeEmpty();
    }

    [Fact]
    public void Satellite_no_data_yields_null_factor()
    {
        var result = ScoreSat();
        result.Factor.Score.Should().BeNull();
    }
}
