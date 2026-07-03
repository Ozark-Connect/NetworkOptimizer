using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Scenario tests for the bottleneck localizer: a localized backhaul hop, a dead-end
/// off-path transit, self-inflicted access bufferbloat, an obvious whole-transit
/// congestion, and absolved near-hop control-plane noise.
///
/// Synthetic topology (ip / hopNumber), RFC1918 addresses and placeholder ASNs:
///   10.0.0.1 h1  access egress              ASN 100
///   10.0.0.2 h2  clean middle hop           ASN 100   ancestors: .1
///   10.0.0.3 h3  backhaul hop               ASN 100   ancestors: .1 .2
///   10.0.1.1 h4  transit                    ASN 200   ancestors: .1 .2 .3
///   10.0.2.1 h3  dead-end off-path transit  ASN 300   ancestors: .1 .2   (branches at .2, off the .3 corridor)
///   10.0.3.1 h5  destination via corridor   dest      ancestors: .1 .2 .3 10.0.1.1
///   10.0.4.1 h5  destination off corridor   dest      ancestors: .1 .2   (clean control)
/// </summary>
public class CongestionLocalizerTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly DateTime HumpStart = TestSeries.Start.AddHours(12);
    private static readonly DateTime HumpEnd = HumpStart.AddMinutes(45);

    private const string Bng = "10.0.0.1";
    private const string Border = "10.0.0.2";
    private const string Backhaul = "10.0.0.3";
    private const string Transit = "10.0.1.1";
    private const string DeadEnd = "10.0.2.1";
    private const string DestCorridor = "10.0.3.1";
    private const string DestControl = "10.0.4.1";

    private static readonly Dictionary<string, int> HopNumbers = new(StringComparer.OrdinalIgnoreCase)
    {
        [Bng] = 1,
        [Border] = 2,
        [Backhaul] = 3,
        [Transit] = 4,
        [DeadEnd] = 3,
        [DestCorridor] = 5,
        [DestControl] = 5
    };

    private static List<LatencySample> Flat(double rtt = 5) => TestSeries.Flat(TestSeries.Start, Day, rtt, jitterMs: 0.5);
    private static List<LatencySample> Elevated(double rtt = 5) => Flat(rtt).WithSegment(HumpStart, HumpEnd, rttMs: rtt + 25, jitterMs: 6);
    // A ~1 ms drift in the window: under the absolute +2 ms elevation bar (reads "clean" / not elevated),
    // but over the 0.5 ms median-shift margin (the line-wide test still sees it rose). Models a high-
    // baseline path whose small bufferbloat offset hides under its own threshold.
    private static List<LatencySample> SmallRise(double rtt = 5) => Flat(rtt).WithSegment(HumpStart, HumpEnd, rttMs: rtt + 1, jitterMs: 0.6);

    private static AsnSeries Hop(int asn, string ip, List<LatencySample> samples, params string[] ancestors) => new()
    {
        AsnNumber = asn,
        AsnName = $"AS{asn}-{ip}",
        TargetIds = { ip },
        Samples = samples,
        HopIps = { ip },
        AncestorIps = ancestors.ToList()
    };

    private static AsnSeries Dest(string ip, List<LatencySample> samples, params string[] ancestors)
    {
        var s = Hop(0, ip, samples, ancestors);
        return new AsnSeries
        {
            AsnNumber = 0,
            AsnName = ip,
            TargetIds = { ip },
            Samples = samples,
            HopIps = { ip },
            AncestorIps = ancestors.ToList(),
            IsDestination = true
        };
    }

    private static List<(DateTime Time, double? Utilization)> HighLoad()
    {
        var list = new List<(DateTime, double?)>();
        for (var t = HumpStart; t < HumpEnd; t = t.AddMinutes(1)) list.Add((t, 0.9));
        return list;
    }

    private static CongestionTopology Topo(bool load) => new()
    {
        AccessEgressHopIps = new HashSet<string>(new[] { Bng }, StringComparer.OrdinalIgnoreCase),
        HopNumberByIp = HopNumbers,
        Load = load ? HighLoad() : new List<(DateTime, double?)>(),
        HasTraceMap = true
    };

    [Fact]
    public void Each_bottleneck_reports_its_own_duration_not_the_cluster_span()
    {
        // Two independent bottlenecks overlap in time: a near backhaul hop that clears in 45 min,
        // and an off-corridor dead-end hop elevated for 3 h. They land in one time-cluster, but each
        // event must report its OWN elevation span - the short one must not inherit the long one's.
        var shortEnd = HumpStart.AddMinutes(45);
        var longEnd = HumpStart.AddHours(3);
        List<LatencySample> ElevatedUntil(DateTime end, double rtt) =>
            Flat(rtt).WithSegment(HumpStart, end, rttMs: rtt + 25, jitterMs: 6);

        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, ElevatedUntil(shortEnd, 5), Bng, Border), // 45 min; next hop clean -> own bottleneck
            Hop(200, Transit, Flat(8), Bng, Border, Backhaul),           // clean downstream witness
            Hop(300, DeadEnd, ElevatedUntil(longEnd, 5), Bng, Border),   // 3 h; off-corridor dead-end -> own bottleneck
            Dest(DestControl, Flat(), Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        var backhaul = events.Single(e => e.BottleneckHopIp == Backhaul);
        var deadEnd = events.Single(e => e.BottleneckHopIp == DeadEnd);
        backhaul.Duration.Should().BeLessThan(TimeSpan.FromHours(1));
        deadEnd.Duration.Should().BeGreaterThan(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Similar_duration_bottlenecks_share_one_clean_window()
    {
        // Two bottlenecks elevated for roughly the same long span (slightly staggered). Each covers
        // most of the cluster, so both report the SAME shared window instead of fragmenting into
        // slightly different per-hop start/end - the genuine co-temporal event reads as one window.
        List<LatencySample> ElevatedBetween(DateTime from, DateTime to, double rtt) =>
            Flat(rtt).WithSegment(from, to, rttMs: rtt + 25, jitterMs: 6);

        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, ElevatedBetween(HumpStart, HumpStart.AddHours(3), 5), Bng, Border),       // 3 h
            Hop(200, Transit, Flat(8), Bng, Border, Backhaul),                                            // clean witness
            Hop(300, DeadEnd, ElevatedBetween(HumpStart.AddMinutes(15), HumpStart.AddMinutes(165), 5), Bng, Border), // 2.5 h, staggered
            Dest(DestControl, Flat(), Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        var backhaul = events.Single(e => e.BottleneckHopIp == Backhaul);
        var deadEnd = events.Single(e => e.BottleneckHopIp == DeadEnd);
        backhaul.Start.Should().Be(deadEnd.Start);
        backhaul.End.Should().Be(deadEnd.End);
    }

    [Fact]
    public void Localizes_to_backhaul_hop_and_does_not_blame_downstream_transit()
    {
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestCorridor, Elevated(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().HaveCount(1);
        var e = events[0];
        e.Disposition.Should().Be(CongestionDisposition.Confirmed);
        e.Scope.Should().Be(CongestionScope.Hop);
        e.BottleneckHopIp.Should().Be(Backhaul);
        e.AsnNumbers.Should().Equal(100);          // the backhaul ASN, not transit 200
        e.LoadCoincident.Should().BeTrue();
        e.Suppressed.Should().BeFalse();
        e.CleanParallelPaths.Should().BeGreaterThan(0);   // off-path hops stayed clean -> hop-isolated, not access-wide
    }

    [Fact]
    public void Brief_load_at_the_access_hop_registers_despite_a_wide_transit_hop_and_padded_window()
    {
        // The access hop bursts for ~6 min straddling the 15-min bucket boundary, so the event reports
        // over a padded 30-min window. A deeper transit hop is elevated across the WHOLE window (its
        // spikes miss the brief load), so the window-fraction test dilutes to ~0.2 and would read False.
        // The nearest-hop arm samples load only at the ACCESS hop's own excursions (all under the load)
        // and correctly registers it as load-coincident. Mirrors the real 14:45 event.
        var burstStart = HumpStart.AddMinutes(12);
        var burstEnd = HumpStart.AddMinutes(18);
        List<LatencySample> AccessBurst() => Flat(5).WithSegment(burstStart, burstEnd, rttMs: 30, jitterMs: 6);
        // Deep transit hop elevated across the whole padded window - its excursions span it and mostly
        // miss the 6-min load, so pooling/window-fraction is diluted below the bar.
        List<LatencySample> WideTransit() => Flat(30).WithSegment(HumpStart, HumpStart.AddMinutes(30), rttMs: 35, jitterMs: 4);

        var series = new List<AsnSeries>
        {
            Hop(100, Bng, AccessBurst()),
            Hop(100, Border, AccessBurst(), Bng),
            Hop(200, Transit, WideTransit(), Bng, Border),
            Dest(DestCorridor, AccessBurst(), Bng, Border, Transit)
        };

        var load = new List<(DateTime, double?)>();
        for (var t = HumpStart; t < HumpStart.AddMinutes(30); t = t.AddMinutes(1))
            load.Add((t, t >= burstStart && t < burstEnd ? 0.9 : 0.1)); // high only during the burst
        var topo = new CongestionTopology
        {
            AccessEgressHopIps = new HashSet<string>(new[] { Bng }, StringComparer.OrdinalIgnoreCase),
            HopNumberByIp = HopNumbers,
            Load = load,
            HasTraceMap = true
        };

        var events = CongestionLocalizer.Localize(series, topo, Options);

        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e => e.LoadCoincident);
    }

    [Fact]
    public void Brief_load_that_misses_the_access_hop_elevation_is_not_load_coincident()
    {
        // Same shape, but the load spike is EARLY (before the access burst) - the access hop rose when
        // the line was NOT loaded. Neither arm should fire: window-fraction is diluted, and the nearest-
        // hop arm sees low load at the access hop's excursion moments. Guards against a false positive.
        var burstStart = HumpStart.AddMinutes(12);
        var burstEnd = HumpStart.AddMinutes(18);
        List<LatencySample> AccessBurst() => Flat(5).WithSegment(burstStart, burstEnd, rttMs: 30, jitterMs: 6);
        List<LatencySample> WideTransit() => Flat(30).WithSegment(HumpStart, HumpStart.AddMinutes(30), rttMs: 35, jitterMs: 4);

        var series = new List<AsnSeries>
        {
            Hop(100, Bng, AccessBurst()),
            Hop(100, Border, AccessBurst(), Bng),
            Hop(200, Transit, WideTransit(), Bng, Border),
            Dest(DestCorridor, AccessBurst(), Bng, Border, Transit)
        };

        var load = new List<(DateTime, double?)>();
        for (var t = HumpStart; t < HumpStart.AddMinutes(30); t = t.AddMinutes(1))
            load.Add((t, t < HumpStart.AddMinutes(6) ? 0.9 : 0.1)); // high early, NOT during the burst
        var topo = new CongestionTopology
        {
            AccessEgressHopIps = new HashSet<string>(new[] { Bng }, StringComparer.OrdinalIgnoreCase),
            HopNumberByIp = HopNumbers,
            Load = load,
            HasTraceMap = true
        };

        var events = CongestionLocalizer.Localize(series, topo, Options);

        events.Should().NotBeEmpty();
        events.Should().OnlyContain(e => !e.LoadCoincident);
    }

    [Fact]
    public void Named_bottleneck_that_did_not_fire_reports_its_own_rise_not_a_deeper_member()
    {
        // The access hop is elevated only briefly (excursions inside one bucket -> no fired 30-min event),
        // so it's the DERIVED bottleneck; a deeper hop fired with a small near-baseline delta. The row must
        // report the named access hop's OWN rise (from its samples), not the deeper member's numbers.
        var burstStart = HumpStart.AddMinutes(3);
        var burstEnd = HumpStart.AddMinutes(9);   // 6 min, one bucket -> no fired event, but IsElevated
        var accessShortBurst = Flat(3).WithSegment(burstStart, burstEnd, rttMs: 20, jitterMs: 6);
        var deepSmall = Flat(10).WithSegment(HumpStart, HumpStart.AddMinutes(45), rttMs: 13, jitterMs: 3); // fires, small delta

        var series = new List<AsnSeries>
        {
            Hop(100, Bng, accessShortBurst),
            Hop(200, Transit, deepSmall, Bng),
            Dest(DestCorridor, deepSmall, Bng, Transit)   // downstream witness -> propagated (Confirmed)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        var e = events.Single(x => x.BottleneckHopIp == Bng);
        e.BaselineRttMs.Should().BeApproximately(3, 2);          // the access hop's baseline, not the deep hop's ~10
        e.PeakRttMs.Should().BeGreaterThan(e.BaselineRttMs + 3); // shows a real rise, not a ~0 delta
    }

    [Fact]
    public void Burst_event_reports_the_spike_magnitude_not_the_bucket_median()
    {
        // An intermittent-spike ("burst") event: RTT/jitter medians stay near baseline, only sporadic
        // samples spike. The detector reports peak as the bucket MEDIAN, which hides the spikes, so the
        // row would show a ~0 delta. The row must report the spike magnitude (sample p90 RTT / max jitter).
        // Uses an off-trace-map target (unlocalized, identity == null) - e.g. a speedtest / endpoint target.
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 2, jitterMs: 0.3)
            .Select((s, i) => s.Time >= HumpStart && s.Time < HumpStart.AddMinutes(45) && i % 3 == 0
                ? new LatencySample(s.Time, 8, 8 + 5, 5, 0)   // sporadic spike: RTT 8 ms, jitter 5 ms
                : s)
            .ToList();
        var series = new List<AsnSeries>
        {
            new() { AsnNumber = 500, AsnName = "AS500", TargetIds = { "203.0.113.9" }, Samples = samples, HopIps = { "203.0.113.9" } }
        };
        var topo = new CongestionTopology
        {
            HopNumberByIp = HopNumbers,
            Load = new List<(DateTime, double?)>(),
            HasTraceMap = true
        };

        var events = CongestionLocalizer.Localize(series, topo, Options);

        events.Should().NotBeEmpty();
        var e = events[0];
        e.PeakRttMs.Should().BeGreaterThan(5);      // reflects the ~8 ms spike, not the ~2 ms bucket median
        e.PeakJitterMs.Should().BeGreaterThan(2);   // reflects the ~5 ms jitter spike, not the flat median
    }

    [Fact]
    public void Dead_end_transit_is_unverifiable_and_not_merged_into_the_corridor_event()
    {
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Elevated(), Bng, Border),          // off-path dead-end: elevated, no monitored descendant
            Dest(DestCorridor, Elevated(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().HaveCount(2);
        var corridor = events.Single(e => e.BottleneckHopIp == Backhaul);
        var deadEnd = events.Single(e => e.BottleneckHopIp == DeadEnd);

        corridor.Disposition.Should().Be(CongestionDisposition.Confirmed);
        corridor.AsnNumbers.Should().NotContain(300);            // off-path dead-end not folded in
        deadEnd.Disposition.Should().Be(CongestionDisposition.Unverifiable);
        deadEnd.AsnNumbers.Should().Equal(300);
        deadEnd.Suppressed.Should().BeFalse();                   // surfaced, but...
        deadEnd.Confidence.Should().BeLessThan(corridor.Confidence);
    }

    [Fact]
    public void Access_wide_elevation_under_load_is_self_inflicted_and_suppressed()
    {
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),
            Hop(100, Border, Elevated(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Elevated(), Bng, Border),
            Dest(DestCorridor, Elevated(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, Elevated(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().ContainSingle(e => e.Disposition == CongestionDisposition.SelfInflicted);
        var self = events.Single(e => e.Disposition == CongestionDisposition.SelfInflicted);
        self.BottleneckHopIp.Should().Be(Bng);
        self.Suppressed.Should().BeTrue();
        events.Should().NotContain(e => e.Disposition == CongestionDisposition.Confirmed);
    }

    [Fact]
    public void Line_wide_rise_is_loaded_latency_even_when_a_high_baseline_path_reads_clean_on_the_absolute_bar()
    {
        // The Jun 5 fix: a high-baseline/high-variance path drifts up ~1 ms with everything else but
        // stays under the absolute +2 ms elevation bar, so the old "any clean control" veto wrongly
        // blocked self-infliction. The median-shift line-wide test still sees it rose, so the egress
        // event is correctly Loaded Latency (SelfInflicted), not a hop bottleneck.
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),
            Hop(100, Border, Elevated(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Elevated(), Bng, Border),
            Dest(DestCorridor, Elevated(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, SmallRise(), Bng, Border)   // rose ~1 ms, under the bar -> "clean" to the old veto
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        var self = events.Single(e => e.Disposition == CongestionDisposition.SelfInflicted);
        self.BottleneckHopIp.Should().Be(Bng);
        self.Suppressed.Should().BeTrue();
    }

    [Fact]
    public void Access_egress_elevated_under_load_but_not_line_wide_stays_confirmed()
    {
        // Egress + its own corridor elevated, but the transit, dead-end and destinations stayed flat -
        // only ~40% of paths rose, so it is NOT line-wide. A real localized issue at the egress, not
        // loaded latency: it must stay Confirmed (scored), never SelfInflicted.
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),
            Hop(100, Border, Elevated(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Flat(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestCorridor, Flat(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().NotContain(e => e.Disposition == CongestionDisposition.SelfInflicted);
        events.Single(e => e.BottleneckHopIp == Bng).Disposition.Should().Be(CongestionDisposition.Confirmed);
    }

    [Fact]
    public void Obvious_whole_transit_congestion_with_no_load_is_confirmed_against_the_transit()
    {
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, Flat()),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestCorridor, Elevated(), Bng, Border, Backhaul, Transit),  // downstream of transit, confirms propagation
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        events.Should().HaveCount(1);
        var e = events[0];
        e.Disposition.Should().Be(CongestionDisposition.Confirmed);
        e.BottleneckHopIp.Should().Be(Transit);
        e.AsnNumbers.Should().Equal(200);
        e.LoadCoincident.Should().BeFalse();
    }

    [Fact]
    public void Near_hop_elevation_that_does_not_propagate_is_absolved_as_control_plane_noise()
    {
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Elevated(), Bng),                   // elevated near hop...
            Hop(100, Backhaul, Flat(), Bng, Border),             // ...but immediate downstream is clean
            Hop(200, Transit, Flat(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestCorridor, Flat(), Bng, Border, Backhaul, Transit),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        events.Should().HaveCount(1);
        events[0].Disposition.Should().Be(CongestionDisposition.ControlPlaneNoise);
        events[0].BottleneckHopIp.Should().Be(Border);
        events[0].Suppressed.Should().BeTrue();
    }

    [Fact]
    public void Bottleneck_is_confirmed_when_a_downstream_hop_is_elevated_but_did_not_fire_its_own_event()
    {
        // The downstream transit inherits the bottleneck's delay as a short spike - real, but too
        // brief to fire its own sustained event. Propagation must still see it via the softer
        // in-window excursion test, not absolve the real bottleneck as control-plane noise.
        var shortSpike = Flat(8).WithSegment(HumpStart, HumpStart.AddMinutes(10), rttMs: 33, jitterMs: 6);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, Elevated(), Bng, Border),            // bottleneck: fires its own event
            Hop(200, Transit, shortSpike, Bng, Border, Backhaul),   // downstream: elevated, does NOT fire
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        var backhaul = events.Single(e => e.BottleneckHopIp == Backhaul);
        backhaul.Disposition.Should().Be(CongestionDisposition.Confirmed);
    }

    [Fact]
    public void Propagation_recognizes_a_jitter_rise_downstream_not_just_rtt()
    {
        // The bottleneck's delay reaches the downstream hop as JITTER while its RTT stays flat.
        // RTT-only propagation would absolve the bottleneck as control-plane noise; the jitter arm
        // must see the propagation and confirm the real bottleneck.
        var jitterOnly = Flat(5).WithSegment(HumpStart, HumpEnd, rttMs: 5, jitterMs: 3);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat()),
            Hop(100, Backhaul, Elevated(), Bng, Border),             // bottleneck, fires (rtt + jitter)
            Hop(200, Transit, jitterOnly, Bng, Border, Backhaul),    // downstream: jitter up, RTT flat
            Hop(300, DeadEnd, Flat(), Bng, Border),
            Dest(DestControl, Flat(), Bng, Border)
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        var backhaul = events.Single(e => e.BottleneckHopIp == Backhaul);
        backhaul.Disposition.Should().Be(CongestionDisposition.Confirmed);
    }

    [Fact]
    public void Unverifiable_hop_inherits_confirmed_from_a_same_asn_sibling_in_the_same_window()
    {
        // Two hops on AS 500 elevated in the same window: one has a downstream witness (Confirmed),
        // the other is a dead-end (would be Unverifiable) - it inherits Confirmed from the sibling.
        var hopNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.1"] = 1,
            ["10.0.5.1"] = 4,
            ["10.0.5.2"] = 4,
            ["10.0.6.1"] = 5
        };
        var series = new List<AsnSeries>
        {
            Hop(100, "10.0.0.1", Flat()),
            Hop(500, "10.0.5.1", Elevated(), "10.0.0.1"),                 // AS500 hop with a witness beyond it
            Hop(500, "10.0.5.2", Elevated(), "10.0.0.1"),                 // AS500 dead-end hop (no descendant)
            Hop(600, "10.0.6.1", Elevated(), "10.0.0.1", "10.0.5.1")      // downstream of 10.0.5.1, confirms it
        };
        var topo = new CongestionTopology
        {
            AccessEgressHopIps = new HashSet<string>(new[] { "10.0.0.1" }, StringComparer.OrdinalIgnoreCase),
            HopNumberByIp = hopNumbers,
            Load = new List<(DateTime, double?)>(),
            HasTraceMap = true
        };

        var events = CongestionLocalizer.Localize(series, topo, Options);

        var deadEnd = events.Single(e => e.BottleneckHopIp == "10.0.5.2");
        deadEnd.Disposition.Should().Be(CongestionDisposition.Confirmed);
        deadEnd.ConfirmedBySibling.Should().BeTrue();
    }

    [Fact]
    public void A_parallel_path_that_only_picked_up_the_jitter_floor_counts_as_a_clean_control()
    {
        // Backhaul is the localized bottleneck under load. A parallel branch (DeadEnd) only picked up
        // the load's jitter floor - its RTT stayed at baseline - so it did NOT get localized congestion
        // and must count as a clean control (evidence Backhaul is its own capacity, not access
        // bufferbloat). A jitter-inclusive "elevated" test would wrongly exclude it and drop the count.
        var jitterFloor = Flat(5).WithSegment(HumpStart, HumpEnd, rttMs: 5, jitterMs: 3);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Flat()),
            Hop(100, Border, Flat(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),            // localized bottleneck
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),   // downstream victim
            Hop(300, DeadEnd, jitterFloor, Bng, Border),            // parallel branch: jitter floor only, RTT flat
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        var backhaul = events.Single(e => e.BottleneckHopIp == Backhaul);
        backhaul.Disposition.Should().Be(CongestionDisposition.Confirmed);
        // Bng, Border, and the jitter-floor DeadEnd all stayed at the floor -> all clean controls.
        // Without the floor-relative test, the jitter-floor DeadEnd would be excluded (count would be 2).
        backhaul.CleanParallelPaths.Should().Be(3);
    }

    [Fact]
    public void A_target_with_no_data_during_the_event_is_not_counted_as_a_clean_parallel_path()
    {
        // A confirmed bottleneck under load, with two parallel paths that don't cross it: one with
        // data spanning the event (genuinely clean), and one whose samples start only AFTER the event
        // (a newer target added later, or a monitoring gap). Only the path with data may count clean.
        var hopNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["10.0.0.1"] = 1,
            ["10.0.5.1"] = 4,
            ["10.0.6.1"] = 5,
            ["10.0.7.1"] = 4,
            ["10.0.8.1"] = 4
        };
        var afterOnly = TestSeries.Flat(HumpEnd.AddHours(1), TimeSpan.FromHours(6), 5, 0.5);
        var series = new List<AsnSeries>
        {
            Hop(100, "10.0.0.1", Flat()),                              // access egress, clean
            Hop(500, "10.0.5.1", Elevated(), "10.0.0.1"),             // bottleneck (elevated under load)
            Hop(600, "10.0.6.1", Elevated(), "10.0.0.1", "10.0.5.1"), // downstream witness -> Confirmed
            Hop(700, "10.0.7.1", Flat(), "10.0.0.1"),                 // clean parallel, has data in the window
            Hop(800, "10.0.8.1", afterOnly, "10.0.0.1")               // newer parallel, no data during the event
        };
        var topo = new CongestionTopology
        {
            AccessEgressHopIps = new HashSet<string>(new[] { "10.0.0.1" }, StringComparer.OrdinalIgnoreCase),
            HopNumberByIp = hopNumbers,
            Load = HighLoad(),
            HasTraceMap = true
        };

        var events = CongestionLocalizer.Localize(series, topo, Options);

        var evt = events.Single(e => e.BottleneckHopIp == "10.0.5.1");
        evt.Disposition.Should().Be(CongestionDisposition.Confirmed);
        // Access hop + 10.0.7.1 (both clean, with data in the window) count; 10.0.8.1 (no data during
        // the event) does not - it would be 3 without the data-presence guard.
        evt.CleanParallelPaths.Should().Be(2);
    }

    [Fact]
    public void A_uniform_line_wide_rise_with_a_long_mild_tail_still_collapses()
    {
        // The Jun 23 shape: every path has a strong ~1 h core plus a long jitter tail whose RTT sits
        // back at baseline (the jitter keeps the run alive). The MEDIAN RTT over the full window
        // dilutes toward baseline, so a median-based line-wide test flickers below threshold and the
        // incident fragments into per-hop rows. The high-percentile test sees the strong core, so a
        // genuinely line-wide, uniform incident collapses to one shared event even with a long tail.
        List<LatencySample> CoreThenTail() => Flat(5)
            .WithSegment(HumpStart, HumpStart.AddHours(1), rttMs: 30, jitterMs: 6)
            .WithSegment(HumpStart.AddHours(1), HumpStart.AddHours(3), rttMs: 5, jitterMs: 3);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, CoreThenTail()),
            Hop(100, Border, CoreThenTail(), Bng),
            Hop(100, Backhaul, CoreThenTail(), Bng, Border),
            Hop(200, Transit, CoreThenTail(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, CoreThenTail(), Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        events.Should().ContainSingle();
    }

    [Fact]
    public void A_high_variance_rise_under_load_is_not_loaded_latency_even_when_breadth_passes()
    {
        // The 19:15 shape: the access egress rose hard while the rest only picked up the shared load
        // floor (jitter up, RTT barely moved). The high-percentile breadth test now counts the floor
        // paths as "rose", so line-wide breadth passes - but the rise is NOT uniform (one path far above
        // the floor). The uniformity gate must keep this from reading as self-inflicted Loaded Latency;
        // it's localized congestion on top of load, not your access link slowing everything equally.
        List<LatencySample> Floor() => Flat(5).WithSegment(HumpStart, HumpEnd, rttMs: 6, jitterMs: 3);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),                  // access egress: hard rise
            Hop(100, Border, Floor(), Bng),             // the rest: shared floor only (jitter up, RTT flat)
            Hop(100, Backhaul, Floor(), Bng, Border),
            Hop(200, Transit, Floor(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Floor(), Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().NotContain(e => e.Disposition == CongestionDisposition.SelfInflicted);
    }

    [Fact]
    public void Floor_jitter_rise_with_only_some_paths_risen_in_rtt_does_not_collapse_to_loaded_latency()
    {
        // Under load the jitter floor lifts every path, but only some paths actually rose in RTT.
        // That's a shared floor + localized congestion, not "everything slowed" - it must NOT collapse
        // to one Loaded Latency event; the RTT-risen paths localize per-hop instead.
        var jitterOnly = Flat(5).WithSegment(HumpStart, HumpEnd, rttMs: 5, jitterMs: 3);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, jitterOnly),
            Hop(100, Border, jitterOnly, Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, jitterOnly, Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: true), Options);

        events.Should().NotContain(e => e.Disposition == CongestionDisposition.SelfInflicted);
    }

    [Fact]
    public void Relative_line_wide_with_no_load_collapses_to_one_shared_confirmed_event()
    {
        // Every monitored path is elevated relative to its own baseline, rooted at the access egress,
        // with no heavy local load: one shared upstream incident attributed to the convergence hop,
        // Confirmed (scored) - not N separate per-hop rows.
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),
            Hop(100, Border, Elevated(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, Elevated(), Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        events.Should().ContainSingle();
        events[0].Disposition.Should().Be(CongestionDisposition.Confirmed);
        events[0].BottleneckHopIp.Should().Be(Bng);
        events[0].AttributionReason.Should().Contain("shared upstream");
    }

    [Fact]
    public void Shared_incident_span_trims_a_lingering_hop()
    {
        // Four hops rise together for ~45 min; one lingers 3 h past the rest. The collapsed incident
        // must span only the shared window (~45 min), not be dragged out to the lingering hop's 3 h.
        var lingering = Flat(5).WithSegment(HumpStart, HumpStart.AddHours(3), rttMs: 30, jitterMs: 6);
        var series = new List<AsnSeries>
        {
            Hop(100, Bng, Elevated()),
            Hop(100, Border, Elevated(), Bng),
            Hop(100, Backhaul, Elevated(), Bng, Border),
            Hop(200, Transit, Elevated(), Bng, Border, Backhaul),
            Hop(300, DeadEnd, lingering, Bng, Border),
        };

        var events = CongestionLocalizer.Localize(series, Topo(load: false), Options);

        events.Should().ContainSingle();
        events[0].Duration.Should().BeLessThan(TimeSpan.FromHours(1.5));
    }

    [Fact]
    public void Jitter_rise_below_the_absolute_floor_does_not_fire()
    {
        // RTT clearly elevated and jitter ratio over 2x, but the absolute jitter rise
        // (0.4 ms) is under CongestionJitterMinDeltaMs - a stable far hop's ICMP wobble.
        var samples = TestSeries.Flat(TestSeries.Start, Day, rttMs: 20, jitterMs: 0.2)
            .WithSegment(HumpStart, HumpEnd, rttMs: 26, jitterMs: 0.6);

        var events = CongestionDetector.DetectForSeries(TestSeries.Asn(64500, "FarHop", samples), Options);

        events.Should().BeEmpty();
    }
}
