using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Unit tests for the pure time-of-day usage weighting: peak-normalized weight with a floor, and
/// the local-hours-spanned mapping. Service-side fingerprint building (Influx/timezone) is not
/// covered here - this is the policy layer.
/// </summary>
public class UsageWeightingTests
{
    // A profile that is busy in the evening (hour 20 = peak) and quiet pre-dawn (hour 4).
    private static double[] Profile()
    {
        var p = new double[24];
        p[4] = 0.10;   // quiet
        p[12] = 0.50;  // moderate
        p[20] = 1.00;  // peak
        return p;
    }

    [Fact]
    public void Peak_hour_weighs_full()
    {
        UsageWeighting.Weight(Profile(), new[] { 20 }, floor: 0.5).Should().Be(1.0);
    }

    [Fact]
    public void Quiet_hour_weighs_near_the_floor()
    {
        // 0.10 active / 1.00 peak = 0.1 -> floor + 0.5*0.1 = 0.55
        UsageWeighting.Weight(Profile(), new[] { 4 }, floor: 0.5).Should().BeApproximately(0.55, 0.001);
    }

    [Fact]
    public void Weight_never_drops_below_the_floor()
    {
        var p = new double[24];
        p[20] = 1.0; // every other hour is 0 active
        UsageWeighting.Weight(p, new[] { 3 }, floor: 0.5).Should().Be(0.5);
    }

    [Fact]
    public void Missing_or_degenerate_profile_does_not_soften()
    {
        UsageWeighting.Weight(null, new[] { 12 }, 0.5).Should().Be(1.0);
        UsageWeighting.Weight(new double[24], new[] { 12 }, 0.5).Should().Be(1.0); // all-zero peak
        UsageWeighting.Weight(Profile(), System.Array.Empty<int>(), 0.5).Should().Be(1.0);
    }

    [Fact]
    public void Multi_hour_event_averages_across_the_hours_it_spans()
    {
        // hours 12 (0.50) and 20 (1.00) -> avg 0.75 / peak 1.0 -> 0.5 + 0.5*0.75 = 0.875
        UsageWeighting.Weight(Profile(), new[] { 12, 20 }, 0.5).Should().BeApproximately(0.875, 0.001);
    }

    [Fact]
    public void Local_hours_spanned_is_a_single_hour_for_a_short_event()
    {
        var tz = TimeZoneInfo.Utc;
        var start = new DateTime(2026, 6, 1, 20, 0, 30, DateTimeKind.Utc);
        var hours = UsageWeighting.LocalHoursSpanned(start, start.AddSeconds(28), tz);
        hours.Should().Equal(20);
    }

    [Fact]
    public void Local_hours_spanned_covers_every_hour_a_long_event_touches()
    {
        var tz = TimeZoneInfo.Utc;
        var start = new DateTime(2026, 6, 1, 23, 50, 0, DateTimeKind.Utc);
        var hours = UsageWeighting.LocalHoursSpanned(start, start.AddMinutes(80), tz); // 23:50 -> 01:10
        hours.Should().BeEquivalentTo(new[] { 23, 0, 1 });
    }
}
