using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Loss to the gateway is the common-mode floor of the measurement chain - every probe crosses the
/// host NIC, its cable, the switching fabric and the gateway before reaching anything upstream. These
/// pin the rules that keep the subtraction honest: it only ever removes, it never invents health, and
/// it stops applying once the reading behind it is stale.
/// </summary>
public class GatewayLossFloorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly DateTime Start = TestSeries.Start;

    private static LatencySample At(int minute, double? lossPct) =>
        new(Start.AddMinutes(minute), 1.0, 1.2, 0.1, lossPct);

    [Fact]
    public void No_gateway_series_subtracts_nothing()
    {
        var floor = GatewayLossFloor.Build(new List<LatencySample>(), Options);

        floor.HasLoss.Should().BeFalse();
        floor.Apply(3.5, Start).Should().Be(3.5);
    }

    [Fact]
    public void Upstream_loss_is_reported_net_of_the_gateway()
    {
        // The chain dropped 2% at this instant, so only the excess is the ISP's.
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 2.0) }, Options);

        floor.Apply(5.0, Start).Should().Be(3.0);
    }

    [Fact]
    public void A_target_cleaner_than_the_gateway_never_goes_negative()
    {
        // Reporting less loss than the local chain is a clean path, not a negative one.
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 4.0) }, Options);

        floor.Apply(1.0, Start).Should().Be(0);
    }

    [Fact]
    public void A_clean_gateway_leaves_upstream_loss_untouched()
    {
        // The floor caps what may be blamed upstream; it can never manufacture upstream health.
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 0) }, Options);

        floor.HasLoss.Should().BeFalse();
        floor.Apply(7.5, Start).Should().Be(7.5);
    }

    [Fact]
    public void The_floor_follows_the_gateway_over_time_rather_than_averaging_it()
    {
        // A fabric incident in the middle of the window must not raise the floor across the whole of
        // it, nor a clean stretch dilute the floor while the incident is happening.
        var floor = GatewayLossFloor.Build(new List<LatencySample>
        {
            At(0, 0),
            At(1, 30.0),
            At(2, 0)
        }, Options);

        floor.Apply(35.0, Start).Should().Be(35.0);
        floor.Apply(35.0, Start.AddMinutes(1)).Should().Be(5.0);
        floor.Apply(35.0, Start.AddMinutes(2)).Should().Be(35.0);
    }

    [Fact]
    public void A_reading_is_carried_forward_but_only_while_it_is_fresh()
    {
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 10.0) }, Options);

        // Within the staleness bound the physical condition is assumed to persist.
        floor.Apply(12.0, Start.AddSeconds(Options.GatewayFloorMaxStalenessSeconds - 1))
            .Should().BeApproximately(2.0, 0.001);

        // Past it, an old reading must not keep suppressing loss.
        floor.Apply(12.0, Start.AddSeconds(Options.GatewayFloorMaxStalenessSeconds + 60))
            .Should().Be(12.0);
    }

    [Fact]
    public void A_saturation_burst_is_subtracted_evenly_across_its_span()
    {
        // Modeled on a real capture: saturating the LAN between the gateway and this server took every
        // upstream target to 60-80% loss for about ten seconds, while the gateway itself read 66.7%
        // then 33.3% on its own cadence. Matching only backwards subtracted 0% to 46.7% across that
        // one burst depending on which tick preceded each probe; the whole span crossed the same
        // impaired chain, so the worst nearby reading governs all of it.
        var floor = GatewayLossFloor.Build(new List<LatencySample>
        {
            new(Start, 1, 1, 0.1, 66.7),
            new(Start.AddSeconds(8), 1, 1, 0.1, 33.3)
        }, Options);

        foreach (var offset in new[] { 1, 4, 10 })
            floor.Apply(80, Start.AddSeconds(offset)).Should().BeApproximately(13.3, 0.05,
                $"the burst was continuous, so the probe at +{offset}s crossed the same impairment");
    }

    [Fact]
    public void An_isolated_spike_does_not_absolve_a_span_it_never_overlapped()
    {
        // The match window is deliberately narrow: a gateway blip must not excuse upstream loss
        // minutes away from it.
        var floor = GatewayLossFloor.Build(new List<LatencySample> { new(Start, 1, 1, 0.1, 60.0) }, Options);

        floor.Apply(50, Start.AddSeconds(Options.GatewayFloorMatchSeconds + 5))
            .Should().BeLessThan(50, "still inside the carry-forward window");
        floor.Apply(50, Start.AddSeconds(Options.GatewayFloorMaxStalenessSeconds + 60))
            .Should().Be(50, "far past any reading, nothing is subtracted");
    }

    [Fact]
    public void Nothing_is_subtracted_before_the_first_gateway_reading()
    {
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(10, 5.0) }, Options);

        floor.Apply(6.0, Start).Should().Be(6.0);
    }

    [Fact]
    public void Samples_without_a_loss_reading_are_not_treated_as_clean()
    {
        // An absent measurement is not evidence the chain was fine, so it must not reset the floor.
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 8.0), At(1, null) }, Options);

        floor.Apply(10.0, Start.AddMinutes(1)).Should().Be(2.0);
    }

    [Fact]
    public void Peak_and_mean_describe_the_local_fault_for_reporting()
    {
        var floor = GatewayLossFloor.Build(new List<LatencySample> { At(0, 1.0), At(1, 9.0) }, Options);

        floor.HasLoss.Should().BeTrue();
        floor.PeakLossPct.Should().Be(9.0);
        floor.MeanLossPct.Should().Be(5.0);
    }
}
