using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Regression tests pinning detector behavior on anonymized real-world latency
/// captures (Fixtures/*.csv): two genuine transit routing shifts and one shared
/// upstream congestion event, plus the negative expectations that came with them.
/// If a tuning change breaks these, it broke detection of known-real events.
/// </summary>
public class RealDataRegressionTests
{
    private static readonly string FixtureDir = Path.Combine(AppContext.BaseDirectory, "IspHealth", "Fixtures");
    private static readonly string[] ShiftingTargets = ["transit-as7029-a", "transit-as7029-b", "transit-as7029-c", "transit-as7029-d"];

    private static List<AsnSeries> Load(string name) =>
        RealDataReplayTests.LoadSeries(Path.Combine(FixtureDir, name));

    [Fact]
    public void Detects_real_downward_transit_shift_as_one_correlated_event()
    {
        var events = StepChangeDetector.Detect(Load("real-shift-down.csv"), new IspHealthOptions());

        // The four correlated paths step down at the same boundary, so they collapse
        // to a single event carrying the four-path count.
        var shiftEvents = events.Where(e => ShiftingTargets.Contains(e.AsnName)).ToList();
        shiftEvents.Should().ContainSingle();
        var shift = shiftEvents[0];
        shift.Direction.Should().Be(PathShiftDirection.Down);
        shift.Time.Should().BeOnOrAfter(new DateTime(2026, 6, 12, 15, 30, 0, DateTimeKind.Utc))
            .And.BeOnOrBefore(new DateTime(2026, 6, 12, 17, 0, 0, DateTimeKind.Utc));
        shift.CorrelatedTargetCount.Should().Be(4);
        shift.DeltaMs.Should().BeInRange(-4, -2);

        events.Where(e => !ShiftingTargets.Contains(e.AsnName)).Should().BeEmpty("stable transits must not produce shift events");
    }

    [Fact]
    public void Detects_real_dip_and_return_as_two_correlated_events()
    {
        var events = StepChangeDetector.Detect(Load("real-shift-dip-return.csv"), new IspHealthOptions());

        var downs = events.Where(e => e.Direction == PathShiftDirection.Down).ToList();
        var ups = events.Where(e => e.Direction == PathShiftDirection.Up).ToList();

        // The dip and the return are each one correlated event across the four paths.
        downs.Should().ContainSingle();
        downs[0].CorrelatedTargetCount.Should().Be(4);
        downs[0].AsnName.Should().BeOneOf(ShiftingTargets);
        downs[0].Time.Day.Should().Be(10);
        downs[0].Time.Hour.Should().BeGreaterThanOrEqualTo(21);

        ups.Should().ContainSingle();
        ups[0].CorrelatedTargetCount.Should().Be(4);
        ups[0].AsnName.Should().BeOneOf(ShiftingTargets);
        ups[0].Time.Day.Should().Be(11);
        ups[0].Time.Hour.Should().BeInRange(6, 7);
    }

    [Fact]
    public void Routing_shifts_are_not_reported_as_congestion()
    {
        var options = new IspHealthOptions();
        CongestionDetector.Detect(Load("real-shift-down.csv"), options).Should().BeEmpty();
        CongestionDetector.Detect(Load("real-shift-dip-return.csv"), options).Should().BeEmpty();
    }

    [Fact]
    public void Detects_real_shared_upstream_congestion_event()
    {
        var events = CongestionDetector.Detect(Load("real-shared-congestion.csv"), new IspHealthOptions());

        events.Should().HaveCount(1);
        var evt = events[0];
        evt.IsShared.Should().BeTrue();
        evt.AsnNames.Should().Contain("transit-as3356");
        evt.AsnNames.Count.Should().BeGreaterThanOrEqualTo(3, "the congested transit plus the return-path DNS targets degraded together");
        evt.AsnNames.Should().NotContain("transit-as7029-b", "the other transit stayed clean");
        evt.AsnNames.Should().NotContain("transit-as22773", "the other transit stayed clean");
        evt.Start.Should().BeOnOrAfter(new DateTime(2026, 5, 25, 0, 30, 0, DateTimeKind.Utc));
        evt.End.Should().BeOnOrBefore(new DateTime(2026, 5, 25, 3, 30, 0, DateTimeKind.Utc));
        evt.Duration.TotalMinutes.Should().BeGreaterThanOrEqualTo(60);
    }

    [Fact]
    public void Detects_real_evening_incident_as_one_shared_flapping_event()
    {
        // Anonymized capture of a documented transit congestion incident: a congested
        // transit plus targets whose return paths crossed it, while two other transit
        // providers stayed clean throughout. The burst criterion bridges the brief
        // mid-incident recovery, so the flapping episode reads as one shared event
        var events = CongestionDetector.Detect(Load("real-incident-evening-congestion.csv"), new IspHealthOptions());

        events.Should().HaveCount(1);
        var evt = events[0];
        evt.IsShared.Should().BeTrue();
        evt.AsnNames.Should().Contain("transit-lumen-far");
        evt.AsnNames.Should().Contain("wan-google-dns");
        evt.AsnNames.Should().NotContain(new[] { "transit-cox-a", "transit-cox-b", "transit-ws-a", "transit-ws-b" });
        evt.Start.Should().BeCloseTo(new DateTime(2026, 5, 20, 0, 30, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30));
        evt.End.Should().BeCloseTo(new DateTime(2026, 5, 20, 2, 45, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30));
        evt.Duration.TotalMinutes.Should().BeGreaterThanOrEqualTo(105);
    }

    [Fact]
    public void Detects_real_bursty_congestion_that_medians_smooth_away()
    {
        // Anonymized capture of intermittent-spike congestion: bucket p90 RTT ran
        // 8-15 ms against a ~6 ms baseline for hours while bucket medians barely
        // moved, so only the burst criterion can see it. An ISP far hop and a CDN
        // path degraded together; the first-hop and other transits stayed clean
        var events = CongestionDetector.Detect(Load("real-bursty-congestion.csv"), new IspHealthOptions());

        events.Should().HaveCount(1);
        var evt = events[0];
        evt.IsShared.Should().BeTrue();
        evt.AsnNames.Should().Contain("isp-hop-far");
        evt.AsnNames.Should().Contain("path-cdn-a");
        evt.AsnNames.Should().NotContain("transit-x");
        evt.Duration.TotalMinutes.Should().BeGreaterThanOrEqualTo(120);
        evt.Start.Should().BeOnOrAfter(new DateTime(2026, 6, 12, 5, 0, 0, DateTimeKind.Utc));
        evt.End.Should().BeOnOrBefore(new DateTime(2026, 6, 12, 11, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void Shared_congestion_does_not_produce_step_events()
    {
        var events = StepChangeDetector.Detect(Load("real-shared-congestion.csv"), new IspHealthOptions());

        events.Should().BeEmpty("congestion humps revert and must not read as path shifts");
    }
}
