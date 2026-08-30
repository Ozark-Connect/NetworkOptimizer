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
    private static WanShareReconciler.Load L(double rate, double dpi = 0, double? cap = null, bool idle = false) => new(rate, dpi, cap, idle);

    [Fact]
    public void Rates_that_add_up_to_the_wan_are_all_wan_and_not_an_estimate()
    {
        // Within the threshold, but 105 against a WAN of 100 is counter skew: the rows scale to the WAN.
        var split = WanShareReconciler.Allocate(100, new[] { L(60), L(45) });
        split.Estimated.Should().BeFalse();
        split.WanBps[0].Should().BeApproximately(60 * 100.0 / 105, 1e-9);
        split.WanBps[1].Should().BeApproximately(45 * 100.0 / 105, 1e-9);
        split.WanBps.Sum().Should().BeApproximately(100, 1e-9);
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

    // ---- The console's per-client rate as a WAN-idle litmus ----

    [Fact]
    public void A_client_the_console_shows_idle_is_local_even_when_the_rates_add_up()
    {
        // Phone saturating the WAN at 900; the NVR takes 30 of camera feed, steady, and the
        // console sees it idle. 930 is within 15% of 900, and the NVR still gets nothing.
        var split = WanShareReconciler.Allocate(900, new[] { L(900, dpi: 900), L(30, dpi: 100, idle: true) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(900, 0);
    }

    [Fact]
    public void An_idle_client_is_out_of_the_sum_so_the_rest_can_still_add_up()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(100, dpi: 900), L(30, dpi: 100, idle: true) });
        split.Estimated.Should().BeFalse();
        split.WanBps.Should().Equal(100, 0);
    }

    [Fact]
    public void An_idle_client_takes_no_share_of_an_estimate_either()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(300, dpi: 100), L(300, dpi: 900, idle: true) });
        split.Estimated.Should().BeTrue();
        split.WanBps.Should().Equal(100, 0);
    }

    [Fact]
    public void Counter_skew_inside_the_slack_scales_the_rows_to_the_wan()
    {
        // The rig streams at 20 and the WAN reads 25 this tick; the NVR is idle. The rig takes the
        // WAN and the rows never sum past it.
        var split = WanShareReconciler.Allocate(25, new[] { L(20, dpi: 900), L(30, dpi: 100, idle: true) });
        split.WanBps.Should().Equal(20, 0);
    }

    [Fact]
    public void Without_the_litmus_the_split_is_unchanged()
    {
        var split = WanShareReconciler.Allocate(100, new[] { L(100, dpi: 300), L(100, dpi: 100) });
        split.WanBps[0].Should().BeApproximately(75, 1e-9);
        split.WanBps[1].Should().BeApproximately(25, 1e-9);
    }
}
