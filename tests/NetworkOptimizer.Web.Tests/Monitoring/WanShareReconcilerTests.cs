using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The WAN split behind Bandwidth Hogs. Clients whose rates add up to the WAN rate are all WAN;
/// past the threshold the WAN rate is water-filled by recent DPI bytes, capped by what each
/// client and its uplink chain actually carried.
/// </summary>
public class WanShareReconcilerTests
{
    private static WanShareReconciler.Load L(double rate, double dpi = 0, double? cap = null, double? console = null) => new(rate, dpi, cap, console);

    [Fact]
    public void Rates_that_add_up_to_the_wan_are_all_wan_and_not_an_estimate()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(60), L(45) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(60, 45);
    }

    [Fact]
    public void Rates_under_the_wan_are_all_wan_too()
    {
        var split = WanShareReconciler.Allocate(200, new[] { L(60), L(45) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(60, 45);
    }

    [Fact]
    public void Past_the_threshold_the_wan_is_shared_by_dpi_weight()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(100, dpi: 300), L(100, dpi: 100) });
        split.Estimated.Should().BeTrue();
        split.WanBps[0].Should().BeApproximately(75, 1e-9);
        split.WanBps[1].Should().BeApproximately(25, 1e-9);
    }

    [Fact]
    public void A_client_with_no_dpi_bytes_gets_nothing_when_others_have_some()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(100, dpi: 300), L(100, dpi: 0) });
        split.WanBps[0].Should().BeApproximately(100, 1e-9);
        split.WanBps[1].Should().Be(0);
    }

    [Fact]
    public void Without_any_dpi_the_measured_rates_weight_the_split()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(150), L(50) });
        split.Estimated.Should().BeTrue();
        split.WanBps[0].Should().BeApproximately(75, 1e-9);
        split.WanBps[1].Should().BeApproximately(25, 1e-9);
    }

    [Fact]
    public void A_share_over_the_measured_rate_is_capped_and_the_rest_reshared()
    {
        // Weights say 90/10, but the first client only moved 30.
        var split = WanShareReconciler.Allocate(100, new[] { L(30, dpi: 900), L(200, dpi: 100) });
        split.WanBps[0].Should().Be(30);
        split.WanBps[1].Should().BeApproximately(70, 1e-9);
    }

    [Fact]
    public void The_uplink_chain_caps_a_client_below_its_own_rate()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(80, cap: 50), L(10) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(50, 10);
    }

    [Fact]
    public void Nothing_is_wan_when_the_wan_is_idle()
    {
        var split = WanShareReconciler.Allocate(0, new[] { L(80), L(10) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(0, 0);
    }

    [Fact]
    public void Total_never_exceeds_the_wan_rate()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(90, dpi: 1), L(90, dpi: 1), L(90, dpi: 1) });
        split.WanBps.Sum().Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void Empty_input_is_empty_output()
    {
        WanShareReconciler.Allocate(100, Array.Empty<WanShareReconciler.Load>()).WanBps.Should().BeEmpty();
    }

    // ---- The console's per-client WAN rate as a tie-break ----

    [Fact]
    public void The_console_rate_is_a_floor_the_dpi_weights_cannot_take_away()
    {
        // DPI history says 90/10, but the console sees the second client moving 80 on the WAN now.
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 900, console: 0), L(300, dpi: 100, console: 80) });
        split.Estimated.Should().BeTrue();
        split.WanBps[1].Should().BeGreaterThanOrEqualTo(80);
        split.WanBps.Sum().Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void A_client_the_console_sees_idle_on_the_wan_yields_to_one_it_sees_busy()
    {
        // Phone streaming from the NAS (console 0) against a client the console shows at 80:
        // the WAN goes to the busy one, up to its soft cap, before the idle one gets any.
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 900, console: 0), L(300, dpi: 100, console: 80) });
        split.WanBps[1].Should().BeApproximately(100, 1e-9);
        split.WanBps[0].Should().Be(0);
    }

    [Fact]
    public void What_the_soft_caps_cannot_place_spills_under_the_hard_caps()
    {
        // A burst the console has not seen yet: every console rate is 0, so the soft caps hold
        // nothing, and the WAN is still handed out by DPI weight under the measured rates.
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 900, console: 0), L(300, dpi: 100, console: 0) });
        split.WanBps[0].Should().BeApproximately(90, 1e-9);
        split.WanBps[1].Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void The_console_floor_is_clipped_to_the_uplink_chain()
    {
        // The console says 400, the client's own uplink carried 50: the counter is now, the console is a minute ago.
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 100, cap: 50, console: 400), L(300, dpi: 100) });
        split.WanBps[0].Should().Be(50);
        split.WanBps.Sum().Should().BeApproximately(100, 1e-9);
    }

    [Fact]
    public void Floors_that_claim_more_than_the_wan_are_scaled_down_together()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 100, console: 80), L(300, dpi: 100, console: 80) });
        split.WanBps.Should().Equal(50, 50);
    }

    [Fact]
    public void Counter_skew_does_not_pool_on_a_local_heavy_client_the_console_sees_idle()
    {
        // The rig streams at 20 and the console agrees; the WAN reads 25 this tick (read a few
        // seconds apart). The NVR moves 30 locally, console 0. The extra 5 is skew: the NVR may
        // take only its DPI share of it, and the rest stays unattributed.
        var split = WanShareReconciler.Allocate(25, new[] { L(20, dpi: 900, console: 20), L(30, dpi: 100, console: 0) });
        split.WanBps[0].Should().Be(20);
        split.WanBps[1].Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void A_burst_the_console_has_not_seen_still_goes_to_the_client_with_the_history()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(900, dpi: 900, console: 0), L(30, dpi: 100, console: 0) });
        split.WanBps[0].Should().BeApproximately(90, 1e-9);
        split.WanBps[1].Should().BeApproximately(10, 1e-9);
    }

    [Fact]
    public void Without_a_console_rate_the_split_is_unchanged()
    {
        var with = WanShareReconciler.Allocate(100, new[] { L(100, dpi: 300), L(100, dpi: 100) });
        with.WanBps[0].Should().BeApproximately(75, 1e-9);
        with.WanBps[1].Should().BeApproximately(25, 1e-9);
    }

    [Fact]
    public void When_the_rates_add_up_the_console_rate_is_not_consulted()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(60, console: 0), L(45, console: 0) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(60, 45);
    }
}
