using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// Co-movement corroboration behind the WAN split: a row whose significant rate steps the WAN
/// total moved with, in step and direction, is WAN traffic whatever the baseline says. The
/// fraction only ever raises a row's WAN candidate, so no evidence means today's behavior.
/// </summary>
public class BandwidthHogsCoMovementTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);

    private static (DateTime At, double Down, double Up) S(int secondsAgo, double down, double up = 0) =>
        (Now.AddSeconds(-secondsAgo), down, up);

    private static IReadOnlyList<(DateTime At, double Down, double Up)> H(
        params (DateTime At, double Down, double Up)[] s) => s;

    [Fact]
    public void A_rise_the_wan_line_stepped_with_is_fully_corroborated()
    {
        // A device speed-testing: its radio steps to 900 Mbps and back while the WAN total steps
        // identically. This is the case the baseline misreads as a local habit.
        var row = H(S(40, 0), S(30, 100e6), S(20, 600e6), S(10, 900e6), S(0, 0));
        var wan = H(S(40, 0), S(30, 100e6), S(20, 600e6), S(10, 900e6), S(0, 0));
        var (down, up) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down.Should().NotBeNull();
        down!.Value.Should().BeApproximately(1.0, 0.01);
        up.Should().BeNull("an idle direction has no significant steps to judge");
    }

    [Fact]
    public void Bursts_over_a_flat_wan_line_earn_nothing()
    {
        // The NVR case: camera feeds step tens of Mbps while the WAN line does not move.
        var row = H(S(40, 5e6), S(30, 30e6), S(20, 8e6), S(10, 33e6), S(0, 6e6));
        var wan = H(S(40, 50e6), S(30, 50e6), S(20, 50e6), S(10, 50e6), S(0, 50e6));
        var (down, _) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down.Should().NotBeNull("flat WAN against real steps is evidence of local, not absence of evidence");
        down!.Value.Should().Be(0);
    }

    [Fact]
    public void Coincident_wiggle_smaller_than_half_the_step_is_chance()
    {
        var row = H(S(40, 20e6), S(30, 40e6), S(20, 20e6), S(10, 40e6), S(0, 20e6));
        var wan = H(S(40, 100e6), S(30, 102e6), S(20, 100e6), S(10, 102e6), S(0, 100e6));
        var (down, _) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down!.Value.Should().Be(0);
    }

    [Fact]
    public void Too_few_significant_steps_is_no_evidence()
    {
        var row = H(S(10, 0), S(0, 900e6));
        var wan = H(S(10, 0), S(0, 900e6));
        var (down, up) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down.Should().BeNull();
        up.Should().BeNull();
    }

    [Fact]
    public void Partial_corroboration_scales_the_fraction()
    {
        // Four 100 Mbps steps; the WAN moved with the first two and sat flat for the rest.
        var row = H(S(40, 0), S(30, 100e6), S(20, 0), S(10, 100e6), S(0, 0));
        var wan = H(S(40, 0), S(30, 100e6), S(20, 0), S(10, 0), S(0, 0));
        var (down, _) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down!.Value.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void Wan_history_swaps_the_port_fields_into_wan_semantics()
    {
        // On a WAN port the stored Down is TX (upload to the ISP) and Up is RX (download).
        var port = H(S(20, 10e6, 200e6), S(10, 12e6, 400e6), S(0, 11e6, 300e6));
        var wan = BandwidthHogsService.WanRateHistory(new[] { port });
        wan.Should().NotBeNull();
        wan![0].Down.Should().Be(200e6);
        wan[0].Up.Should().Be(10e6);
    }

    [Fact]
    public void Two_wan_interfaces_sum_into_one_line()
    {
        var a = H(S(20, 1e6, 100e6), S(10, 2e6, 200e6), S(0, 1e6, 150e6));
        var b = H(S(20, 3e6, 50e6), S(10, 4e6, 60e6), S(0, 3e6, 55e6));
        var wan = BandwidthHogsService.WanRateHistory(new[] { a, b });
        wan.Should().NotBeNull();
        wan![1].Down.Should().Be(260e6);
        wan[1].Up.Should().Be(6e6);
    }

    [Fact]
    public void A_step_landing_across_a_wan_sample_boundary_still_matches()
    {
        // The AP and the gateway sample a few seconds apart, so every step lands between two WAN
        // samples and the aligned delta reads zero; only the shifted comparisons see the moves.
        var row = H(S(70, 0), S(60, 900e6), S(50, 0), S(40, 900e6), S(30, 0), S(20, 900e6), S(10, 0));
        var wan = H(S(75, 0), S(65, 0), S(55, 900e6), S(45, 0), S(35, 900e6), S(25, 0), S(15, 900e6), S(5, 0));
        var (down, _) = BandwidthHogsService.CorroboratedWanFraction(row, wan);
        down.Should().NotBeNull();
        down!.Value.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void Matched_steps_name_the_samples_the_baseline_must_skip()
    {
        var row = H(S(40, 0), S(30, 100e6), S(20, 600e6), S(10, 900e6), S(0, 0));
        var wan = H(S(40, 0), S(30, 100e6), S(20, 600e6), S(10, 900e6), S(0, 0));
        var evidence = BandwidthHogsService.CorroboratedWan(row, wan);
        evidence.FracDown.Should().NotBeNull();
        evidence.MatchedDown.Should().Contain(Now.AddSeconds(-30));
        evidence.MatchedDown.Should().Contain(Now.AddSeconds(-10));
    }

    [Fact]
    public void A_flat_wan_line_matches_no_samples()
    {
        var row = H(S(40, 5e6), S(30, 30e6), S(20, 8e6), S(10, 33e6), S(0, 6e6));
        var wan = H(S(40, 50e6), S(30, 50e6), S(20, 50e6), S(10, 50e6), S(0, 50e6));
        var evidence = BandwidthHogsService.CorroboratedWan(row, wan);
        evidence.MatchedDown.Should().BeEmpty("uncorroborated bursts stay in the baseline's history");
    }
}
