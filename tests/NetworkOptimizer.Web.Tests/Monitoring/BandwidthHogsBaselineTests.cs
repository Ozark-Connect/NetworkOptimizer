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
        // NVR: camera feeds wobble 27-33 Mbps, console WAN figure a few Kbps. The baseline is the
        // p90 of the band, so the wobble sits under it instead of reading as growth.
        var measured = new[] { Ago(14, 30e6), Ago(10, 27e6), Ago(5, 33e6), Ago(0, 29e6) };
        var ceiling = Ceiling(Both(14, 3e3), Both(7, 5e3), Both(0, 2e3));
        BandwidthHogsService.BaselineLocalBps(measured, ceiling, Now, MinSpan).Should().BeApproximately(30e6 - 5e3, 1);
    }

    [Fact]
    public void The_wobble_band_sits_under_the_baseline()
    {
        // The observed leak: a 13-37 Mbps feed against a min-based floor left 10-22 Mbps of
        // standing candidacy. Against p90, an in-band reading leaves nothing.
        var measured = new[] { Ago(14, 13e6), Ago(12, 30e6), Ago(10, 35e6), Ago(8, 24e6), Ago(6, 37e6), Ago(4, 28e6), Ago(2, 33e6), Ago(0, 26e6) };
        var baseline = BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan);
        (30e6 - baseline).Should().BeLessThan(0, "an in-band rate must not exceed the baseline");
    }

    [Fact]
    public void One_burst_sample_does_not_drag_the_baseline_up()
    {
        // Nine idle-band samples and one burst: p90 (lower interpolation) stays in the band, so
        // the burst is judged against the band, not against itself.
        var measured = new[] { Ago(14, 30e6), Ago(12, 29e6), Ago(11, 31e6), Ago(9, 30e6), Ago(8, 28e6), Ago(6, 30e6), Ago(5, 29e6), Ago(3, 31e6), Ago(2, 30e6), Ago(0, 230e6) };
        BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan).Should().BeLessThan(32e6);
    }

    [Fact]
    public void An_occasional_burst_leaves_no_baseline()
    {
        // Mostly idle with one burst in the window: p90 sits at the idle level, so the burst
        // reads as growth.
        var measured = new[]
        {
            Ago(14, 0), Ago(13, 0), Ago(11, 0), Ago(10, 0), Ago(8, 0),
            Ago(7, 0), Ago(5, 0), Ago(4, 0), Ago(2, 0), Ago(0, 200e6),
        };
        BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void Frequent_bursts_the_console_never_saw_read_as_local()
    {
        // A device hitting 200 Mbps often enough to own the p90, with the console's 15-minute max
        // near zero, is doing local bursts (repeated LAN speed tests): baselined at that level.
        // The same pattern on the WAN is protected by the ceiling, which captures the bursts.
        var measured = new[] { Ago(14, 200e6), Ago(12, 0), Ago(10, 200e6), Ago(8, 200e6), Ago(6, 0), Ago(4, 200e6), Ago(2, 200e6), Ago(0, 200e6) };
        BandwidthHogsService.BaselineLocalBps(measured, 0, Now, MinSpan).Should().BeApproximately(200e6, 1);
        BandwidthHogsService.BaselineLocalBps(measured, 200e6, Now, MinSpan).Should().Be(0);
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
        // The NVR starts a firmware download: the baseline over the horizon is still the feed, so
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
    public void An_unarmed_row_is_capped_at_what_the_console_corroborates()
    {
        var window = TimeSpan.FromMinutes(15);
        // Cold NVR: no DPI, console ~0 -> capped to ~0 from the first split.
        BandwidthHogsService.UnarmedWanCapBps(0, window, 3e3).Should().BeLessThan(10e3);
        // Cold client with recent DPI history: capped at twice that average rate.
        BandwidthHogsService.UnarmedWanCapBps(dpiRecentBytes: 90e6, window, null).Should().BeApproximately(2 * 90e6 * 8 / 900, 1);
        // A live console rate corroborates on its own once it lands.
        BandwidthHogsService.UnarmedWanCapBps(0, window, 400e6).Should().Be(800e6);
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

/// <summary>
/// Port ownership over a window: succession is not sharing. A month-long window sees past
/// tenants and passers-by on a port; only real concurrent occupancy makes a hub row.
/// </summary>
public class BandwidthHogsDominantOccupantTests
{
    private static NetworkOptimizer.Storage.Services.MonitoringInfluxClient.WiredPortOccupant O(string mac, int samples) =>
        new("aa:bb:cc:dd:ee:00", 1, mac, samples, null, null);

    [Fact]
    public void The_sole_occupant_owns_the_port()
    {
        BandwidthHogsService.DominantOccupant(new[] { O("aa:bb:cc:dd:ee:01", 5) })!.ClientMac.Should().Be("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void A_past_tenant_does_not_make_the_port_shared()
    {
        // 29 days of the rig, a day of whatever sat there before.
        var occupants = new[] { O("aa:bb:cc:dd:ee:01", 2700), O("aa:bb:cc:dd:ee:02", 90), O("aa:bb:cc:dd:ee:03", 40) };
        BandwidthHogsService.DominantOccupant(occupants)!.ClientMac.Should().Be("aa:bb:cc:dd:ee:01");
    }

    [Fact]
    public void Real_concurrent_sharing_has_no_dominant_occupant()
    {
        // A hypervisor's interfaces: every MAC present the whole window.
        var occupants = new[] { O("aa:bb:cc:dd:ee:01", 1000), O("aa:bb:cc:dd:ee:02", 980), O("aa:bb:cc:dd:ee:03", 940) };
        BandwidthHogsService.DominantOccupant(occupants).Should().BeNull();
    }

    [Fact]
    public void Empty_input_owns_nothing()
    {
        BandwidthHogsService.DominantOccupant(Array.Empty<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.WiredPortOccupant>()).Should().BeNull();
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
