using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The headroom release: a WAN deficit the capped rates cannot explain - a burst shorter than
/// the corroborating sources' lag - is spent against the clients' uncapped headroom, marked
/// estimated. No deficit, no release, so a capped-idle device stays suppressed.
/// </summary>
public class WanShareReconcilerHeadroomTests
{
    [Fact]
    public void A_short_burst_the_cap_missed_claims_the_deficit()
    {
        // One client bursting 950 Mbps, console-capped to 0, everyone else idle.
        var loads = new[]
        {
            new WanShareReconciler.Load(0, 0, null, 950e6),
            new WanShareReconciler.Load(0, 0, null, 0),
        };
        var split = WanShareReconciler.Allocate(940e6, loads);
        split.WanBps[0].Should().BeApproximately(940e6, 1e6);
        split.WanBps[1].Should().Be(0);
        split.Estimated.Should().BeTrue("headroom spending is an estimate");
    }

    [Fact]
    public void No_deficit_means_no_release()
    {
        // The capped rates already explain the WAN; headroom stays unspent and nothing is estimated.
        var loads = new[]
        {
            new WanShareReconciler.Load(100e6, 0, null, 0),
            new WanShareReconciler.Load(0, 0, null, 30e6),
        };
        var split = WanShareReconciler.Allocate(100e6, loads);
        split.WanBps[1].Should().Be(0);
        split.Estimated.Should().BeFalse();
    }

    [Fact]
    public void The_deficit_splits_by_headroom_share()
    {
        var loads = new[]
        {
            new WanShareReconciler.Load(0, 0, null, 900e6),
            new WanShareReconciler.Load(0, 0, null, 100e6),
        };
        var split = WanShareReconciler.Allocate(500e6, loads);
        split.WanBps[0].Should().BeApproximately(450e6, 1e6);
        split.WanBps[1].Should().BeApproximately(50e6, 1e6);
    }

    [Fact]
    public void Counter_slack_under_the_floor_is_not_a_deficit()
    {
        var loads = new[] { new WanShareReconciler.Load(10e6, 0, null, 40e6) };
        var split = WanShareReconciler.Allocate(10.5e6, loads);
        split.WanBps[0].Should().BeApproximately(10e6, 1e3);
        split.Estimated.Should().BeFalse();
    }
}
