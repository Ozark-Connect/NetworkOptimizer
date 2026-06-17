using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Tests for internet-outage detection and shaping, calibrated to the real AT&T outage on
/// the Mac site (2026-06-17): the OLT briefly went dark at onset then recovered ~10 min
/// before the upstream, which stayed dark. Detection is internet-tier only (shape- and
/// trace-map-independent); the per-hop shape and break attribution use the ordered hops.
/// </summary>
public class OutageDetectorTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(30);
    private static readonly DateTime OutStart = TestSeries.Start.AddMinutes(10);
    private static readonly DateTime OutEnd = TestSeries.Start.AddMinutes(20);

    private static List<LatencySample> Series(double normalLoss, params (DateTime From, DateTime To, double Loss)[] dark)
    {
        var s = TestSeries.Flat(TestSeries.Start, Window, rttMs: 20, jitterMs: 0.5, lossPct: normalLoss);
        foreach (var d in dark) s = s.WithSegment(d.From, d.To, rttMs: 20, jitterMs: 0.5, lossPct: d.Loss);
        return s;
    }

    private static IReadOnlyList<IReadOnlyList<LatencySample>> Triggers(params List<LatencySample>[] series) => series;

    [Fact]
    public void Detects_upstream_outage_and_attributes_break_beyond_olt()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var olt = Series(0); // never dark - stays reachable throughout
        var transit = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt),
            new OutageDetector.Hop("AT&T Transit", 1, transit),
            new OutageDetector.Hop("Cloudflare", 2, internet1),
            new OutageDetector.Hop("Google", 2, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Scope.Should().Be(OutageScope.Upstream);
        events[0].LastReachableHop.Should().Be("AT&T nokia-olt");
        events[0].Duration.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Olt_blip_at_onset_then_recovery_still_reads_upstream_and_leads_the_waterfall()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        // OLT dark for one minute at onset, then recovers - the validated AT&T shape.
        var olt = Series(0, (OutStart, OutStart.AddMinutes(1), 100));
        var transit = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt),
            new OutageDetector.Hop("AT&T Transit", 1, transit),
            new OutageDetector.Hop("Cloudflare", 2, internet1),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        // A brief onset blip must NOT read as the break - the OLT held for the majority.
        events[0].Scope.Should().Be(OutageScope.Upstream);
        events[0].LastReachableHop.Should().Be("AT&T nokia-olt");

        var oltState = events[0].Tiers.Single(t => t.Name == "AT&T nokia-olt");
        var transitState = events[0].Tiers.Single(t => t.Name == "AT&T Transit");
        oltState.RecoveredAt.Should().NotBeNull();
        transitState.RecoveredAt.Should().NotBeNull();
        oltState.RecoveredAt!.Value.Should().BeBefore(transitState.RecoveredAt!.Value);
    }

    [Fact]
    public void Full_wan_outage_when_even_the_nearest_hop_stays_dark()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var olt = Series(0, (OutStart, OutEnd, 100)); // dark the whole outage

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt),
            new OutageDetector.Hop("Cloudflare", 1, internet1),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Scope.Should().Be(OutageScope.FullWan);
        events[0].LastReachableHop.Should().BeNull();
    }

    [Fact]
    public void Monitoring_gap_with_no_samples_is_not_an_outage()
    {
        // No samples at all during the span (the Monitoring Agent stopped collecting) -
        // a gap, never an outage.
        List<LatencySample> Gapped() => TestSeries.Flat(TestSeries.Start, TimeSpan.FromMinutes(10), 20, 0.5, 0)
            .Concat(TestSeries.Flat(OutEnd, TimeSpan.FromMinutes(10), 20, 0.5, 0))
            .ToList();
        var internet1 = Gapped();
        var internet2 = Gapped();

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Brief_blip_below_min_duration_is_ignored()
    {
        var internet1 = Series(0, (OutStart, OutStart.AddMinutes(1), 100));
        var internet2 = Series(0, (OutStart, OutStart.AddMinutes(1), 100));

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Detects_outage_without_a_hop_map_still_noting_it()
    {
        // No hops passed (no trace map / no monitored intermediate hops): the outage is
        // still flagged with its duration; only the per-hop shape and attribution degrade.
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().ContainSingle();
        events[0].Duration.Should().BeCloseTo(TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
        events[0].Tiers.Should().BeEmpty();
    }

    [Fact]
    public void Single_reporting_target_is_too_thin_to_declare_an_outage()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));

        var events = OutageDetector.Detect(Triggers(internet1), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().BeEmpty();
    }
}
