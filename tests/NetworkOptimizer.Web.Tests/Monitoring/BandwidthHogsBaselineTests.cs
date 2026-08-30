using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;
using Xunit;

namespace NetworkOptimizer.Web.Tests.Monitoring;

/// <summary>
/// The per-row baseline behind the WAN split: the floor a row's measured rate held over the
/// history, less the most the console's lagging WAN figure explained of it. A constant local
/// flow (an NVR taking camera feeds) gets a baseline; a bursty or WAN-explained one does not.
/// </summary>
public class BandwidthHogsBaselineTests
{
    private static readonly DateTime Now = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MinSpan = TimeSpan.FromMinutes(5);

    private static (DateTime, double) Ago(int minutes, double bps) => (Now.AddMinutes(-minutes), bps);
    private static (DateTime, double, double) Both(int minutes, double bps) => (Now.AddMinutes(-minutes), bps, bps);

    private static double Ceiling(params (DateTime At, double Down, double Up)[] history) =>
        BandwidthHogsService.ConsoleWanCeiling(new[] { (IReadOnlyList<(DateTime, double, double)>)history }, Now, MinSpan)!.Value.Down;

    [Fact]
    public void A_constant_local_flow_the_console_never_saw_is_the_baseline()
    {
        // NVR: camera feeds wobble 27-33 Mbps, console WAN figure a few Kbps.
        var measured = new[] { Ago(14, 30e6), Ago(10, 27e6), Ago(5, 33e6), Ago(0, 29e6) };
        var ceiling = Ceiling(Both(14, 3e3), Both(7, 5e3), Both(0, 2e3));
        BandwidthHogsService.BaselineLocalBps(measured, ceiling, Now, MinSpan).Should().BeApproximately(27e6 - 5e3, 1);
    }

    [Fact]
    public void A_bursty_client_has_no_baseline()
    {
        var measured = new[] { Ago(14, 0), Ago(10, 900e6), Ago(5, 0), Ago(0, 200e6) };
        BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void A_steady_wan_flow_the_console_explains_has_no_baseline()
    {
        // Rig downloading at 30 for the whole horizon; the console's figure caught up to it.
        var measured = new[] { Ago(14, 30e6), Ago(7, 30e6), Ago(0, 30e6) };
        var ceiling = Ceiling(Both(14, 28e6), Both(7, 30e6), Both(0, 29e6));
        BandwidthHogsService.BaselineLocalBps(measured, ceiling, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void A_burst_above_the_baseline_is_a_wan_candidate()
    {
        // The NVR starts a firmware download: the floor over the horizon is still the feed, so
        // only the feed comes off and the burst above it competes for WAN.
        var measured = new[] { Ago(14, 30e6), Ago(7, 30e6), Ago(0, 230e6) };
        var baseline = BandwidthHogsService.BaselineLocalBps(measured, Ceiling(Both(14, 3e3), Both(0, 3e3)), Now, MinSpan);
        baseline.Should().BeApproximately(30e6 - 3e3, 1);
        (230e6 - baseline).Should().BeGreaterThan(199e6);
    }

    [Fact]
    public void Too_little_measured_history_claims_no_baseline()
    {
        var measured = new[] { Ago(2, 30e6), Ago(0, 30e6) };
        BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void The_ceiling_sums_each_members_own_maximum()
    {
        // A hub with two interfaces: one peaked at 10, the other at 4, at different times.
        var a = new (DateTime, double, double)[] { Both(14, 2e6), Both(7, 10e6), Both(0, 1e6) };
        var b = new (DateTime, double, double)[] { Both(14, 4e6), Both(7, 1e6), Both(0, 3e6) };
        var ceiling = BandwidthHogsService.ConsoleWanCeiling(new IReadOnlyList<(DateTime, double, double)>[] { a, b }, Now, MinSpan);
        ceiling!.Value.Down.Should().BeApproximately(14e6, 1);
    }

    [Fact]
    public void A_member_the_console_has_not_covered_leaves_the_ceiling_unclaimed()
    {
        var covered = new (DateTime, double, double)[] { Both(14, 2e6), Both(0, 1e6) };
        var young = new (DateTime, double, double)[] { Both(2, 1e6), Both(0, 1e6) };
        BandwidthHogsService.ConsoleWanCeiling(new IReadOnlyList<(DateTime, double, double)>[] { covered, young }, Now, MinSpan)
            .Should().BeNull();
        BandwidthHogsService.ConsoleWanCeiling(System.Array.Empty<IReadOnlyList<(DateTime, double, double)>>(), Now, MinSpan)
            .Should().BeNull();
    }
}

/// <summary>The never-touches-the-WAN exclusion, as a predicate over what the console recorded.</summary>
public class BandwidthHogsExclusionTests
{
    private static readonly DateTime End = new(2026, 8, 30, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Lookback = TimeSpan.FromHours(24);
    private const long Floor = 1_000_000;

    [Fact]
    public void An_old_client_with_no_wan_history_and_nothing_recent_is_excluded()
    {
        BandwidthHogsService.IsNotAWanUser(End.AddDays(-30), 200_000, 0, End, Lookback, Floor).Should().BeTrue();
    }

    [Fact]
    public void A_young_client_is_never_excluded()
    {
        BandwidthHogsService.IsNotAWanUser(End.AddHours(-2), 0, 0, End, Lookback, Floor).Should().BeFalse();
    }

    [Fact]
    public void An_unknown_client_is_never_excluded()
    {
        BandwidthHogsService.IsNotAWanUser(null, 0, 0, End, Lookback, Floor).Should().BeFalse();
    }

    [Fact]
    public void Wan_history_above_the_floor_keeps_a_client_in()
    {
        BandwidthHogsService.IsNotAWanUser(End.AddDays(-30), 5_000_000, 0, End, Lookback, Floor).Should().BeFalse();
    }

    [Fact]
    public void Recent_wan_bytes_keep_a_client_in_whatever_its_history()
    {
        BandwidthHogsService.IsNotAWanUser(End.AddDays(-30), 0, 1, End, Lookback, Floor).Should().BeFalse();
    }
}
