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
        [Bng] = 1, [Border] = 2, [Backhaul] = 3, [Transit] = 4, [DeadEnd] = 3, [DestCorridor] = 5, [DestControl] = 5
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
            AsnNumber = 0, AsnName = ip, TargetIds = { ip }, Samples = samples,
            HopIps = { ip }, AncestorIps = ancestors.ToList(), IsDestination = true
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
