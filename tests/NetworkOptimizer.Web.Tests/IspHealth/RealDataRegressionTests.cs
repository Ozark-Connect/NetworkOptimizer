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
    public void Detects_real_downward_transit_shift_on_all_correlated_targets()
    {
        var events = StepChangeDetector.Detect(Load("real-shift-down.csv"), new IspHealthOptions());

        var shiftEvents = events.Where(e => ShiftingTargets.Contains(e.AsnName)).ToList();
        shiftEvents.Should().HaveCount(4);
        shiftEvents.Should().OnlyContain(e => e.Direction == PathShiftDirection.Down);
        shiftEvents.Should().OnlyContain(e => e.Time >= new DateTime(2026, 6, 12, 15, 30, 0, DateTimeKind.Utc)
            && e.Time <= new DateTime(2026, 6, 12, 17, 0, 0, DateTimeKind.Utc));
        shiftEvents.Should().OnlyContain(e => e.CorrelatedTargetCount == 4);
        shiftEvents.Should().OnlyContain(e => e.DeltaMs < -2 && e.DeltaMs > -4);

        events.Where(e => !ShiftingTargets.Contains(e.AsnName)).Should().BeEmpty("stable transits must not produce shift events");
    }

    [Fact]
    public void Detects_real_dip_and_return_as_two_event_groups()
    {
        var events = StepChangeDetector.Detect(Load("real-shift-dip-return.csv"), new IspHealthOptions());

        var downs = events.Where(e => e.Direction == PathShiftDirection.Down).ToList();
        var ups = events.Where(e => e.Direction == PathShiftDirection.Up).ToList();

        downs.Should().HaveCount(4);
        downs.Should().OnlyContain(e => ShiftingTargets.Contains(e.AsnName));
        downs.Should().OnlyContain(e => e.Time.Day == 10 && e.Time.Hour >= 21);

        ups.Should().HaveCount(4);
        ups.Should().OnlyContain(e => ShiftingTargets.Contains(e.AsnName));
        ups.Should().OnlyContain(e => e.Time.Day == 11 && e.Time.Hour >= 6 && e.Time.Hour <= 7);
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
    public void Detects_real_evening_incident_as_two_shared_flapping_episodes()
    {
        // Anonymized capture of a documented transit congestion incident: a congested
        // transit plus targets whose return paths crossed it flapped in two episodes,
        // while two other transit providers stayed clean throughout
        var events = CongestionDetector.Detect(Load("real-incident-evening-congestion.csv"), new IspHealthOptions());

        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.IsShared);
        events.Should().OnlyContain(e => e.AsnNames.Contains("transit-lumen-far"));
        events.Should().OnlyContain(e => e.AsnNames.Contains("wan-google-dns"));
        events.SelectMany(e => e.AsnNames).Should().NotContain(new[] { "transit-cox-a", "transit-cox-b", "transit-ws-a", "transit-ws-b" });

        events[0].Start.Should().BeCloseTo(new DateTime(2026, 5, 20, 0, 30, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30));
        events[1].Start.Should().BeCloseTo(new DateTime(2026, 5, 20, 1, 45, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30));
        events[1].End.Should().BeCloseTo(new DateTime(2026, 5, 20, 2, 45, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(30));
    }

    [Fact]
    public void Shared_congestion_does_not_produce_step_events()
    {
        var events = StepChangeDetector.Detect(Load("real-shared-congestion.csv"), new IspHealthOptions());

        events.Should().BeEmpty("congestion humps revert and must not read as path shifts");
    }
}
