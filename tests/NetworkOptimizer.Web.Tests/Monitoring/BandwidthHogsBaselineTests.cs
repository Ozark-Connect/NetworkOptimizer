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

    [Fact]
    public void A_constant_local_flow_the_console_never_saw_is_the_baseline()
    {
        // NVR: camera feeds wobble 27-33 Mbps, console WAN figure a few Kbps.
        var measured = new[] { Ago(14, 30e6), Ago(10, 27e6), Ago(5, 33e6), Ago(0, 29e6) };
        var console = new[] { Ago(14, 3e3), Ago(7, 5e3), Ago(0, 2e3) };
        BandwidthHogsService.BaselineLocalBps(measured, console, Now, MinSpan).Should().BeApproximately(27e6 - 5e3, 1);
    }

    [Fact]
    public void A_bursty_client_has_no_baseline()
    {
        var measured = new[] { Ago(14, 0), Ago(10, 900e6), Ago(5, 0), Ago(0, 200e6) };
        var console = new[] { Ago(14, 0), Ago(0, 0) };
        BandwidthHogsService.BaselineLocalBps(measured, console, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void A_steady_wan_flow_the_console_explains_has_no_baseline()
    {
        // Rig downloading at 30 for the whole horizon; the console's figure caught up to it.
        var measured = new[] { Ago(14, 30e6), Ago(7, 30e6), Ago(0, 30e6) };
        var console = new[] { Ago(14, 28e6), Ago(7, 30e6), Ago(0, 29e6) };
        BandwidthHogsService.BaselineLocalBps(measured, console, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void A_burst_above_the_baseline_is_a_wan_candidate()
    {
        // The NVR starts a firmware download: the floor over the horizon is still the feed, so
        // only the feed comes off and the burst above it competes for WAN.
        var measured = new[] { Ago(14, 30e6), Ago(7, 30e6), Ago(0, 230e6) };
        var console = new[] { Ago(14, 3e3), Ago(0, 3e3) };
        var baseline = BandwidthHogsService.BaselineLocalBps(measured, console, Now, MinSpan);
        baseline.Should().BeApproximately(30e6 - 3e3, 1);
        (230e6 - baseline).Should().BeGreaterThan(199e6);
    }

    [Fact]
    public void Too_little_history_claims_no_baseline()
    {
        var measured = new[] { Ago(2, 30e6), Ago(0, 30e6) };
        var console = new[] { Ago(2, 0), Ago(0, 0) };
        BandwidthHogsService.BaselineLocalBps(measured, console, Now, MinSpan).Should().Be(0);
    }

    [Fact]
    public void Missing_console_history_claims_no_baseline()
    {
        var measured = new[] { Ago(14, 30e6), Ago(0, 30e6) };
        BandwidthHogsService.BaselineLocalBps(measured, System.Array.Empty<(DateTime, double)>(), Now, MinSpan).Should().Be(0);
    }
}
