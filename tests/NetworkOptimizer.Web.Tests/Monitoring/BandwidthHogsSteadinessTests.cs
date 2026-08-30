using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The steadiness test behind the console WAN-idle litmus: the console's rate lags, so a client
/// may only be called idle once its own rate has held for longer than that lag.
/// </summary>
public class BandwidthHogsSteadinessTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lag = TimeSpan.FromSeconds(60);

    private static (DateTime, double) Ago(int seconds, double bps) => (Now.AddSeconds(-seconds), bps);

    [Fact]
    public void A_rate_held_for_the_whole_lag_is_steady()
    {
        BandwidthHogsService.HeldSteady(new[] { Ago(90, 30), Ago(60, 31), Ago(30, 29), Ago(0, 30) }, Now, 30, Lag, 0.25).Should().BeTrue();
    }

    [Fact]
    public void Too_little_history_is_not_steady()
    {
        // A flow the card first saw 20 s ago cannot be one the console has had time to notice.
        BandwidthHogsService.HeldSteady(new[] { Ago(20, 30), Ago(0, 30) }, Now, 30, Lag, 0.25).Should().BeFalse();
    }

    [Fact]
    public void A_flow_that_just_rose_is_not_steady()
    {
        // The rig sat idle and started a 30 Mbps download 20 s ago: not idle, whatever the console says.
        BandwidthHogsService.HeldSteady(new[] { Ago(90, 0), Ago(60, 0), Ago(30, 0), Ago(20, 30), Ago(0, 30) }, Now, 30, Lag, 0.25).Should().BeFalse();
    }

    [Fact]
    public void A_flow_that_fell_is_still_steady()
    {
        // Falling means the console saw more than it sees now; its idle verdict still holds.
        BandwidthHogsService.HeldSteady(new[] { Ago(90, 60), Ago(60, 60), Ago(30, 30), Ago(0, 30) }, Now, 30, Lag, 0.25).Should().BeTrue();
    }

    [Fact]
    public void A_rise_within_tolerance_is_steady()
    {
        BandwidthHogsService.HeldSteady(new[] { Ago(90, 25), Ago(60, 26), Ago(0, 30) }, Now, 30, Lag, 0.25).Should().BeTrue();
    }
}
