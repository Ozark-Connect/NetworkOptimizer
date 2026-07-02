using FluentAssertions;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

/// <summary>
/// Tests for internet-outage detection and shaping, calibrated to the real AT&T outage on
/// a test site (2026-06-17): the OLT briefly went dark at onset then recovered ~10 min
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

    /// <summary>A target sampled every 5 s across the window, dark (100% loss) where the predicate holds.</summary>
    private static List<LatencySample> Cadenced(Func<DateTime, bool> isDark)
    {
        var s = new List<LatencySample>();
        for (var t = TestSeries.Start; t < TestSeries.Start + Window; t += TimeSpan.FromSeconds(5))
            s.Add(new LatencySample(t, 20, 20.5, 0.5, isDark(t) ? 100 : 0));
        return s;
    }

    /// <summary>A target sampled every 10 s, at <paramref name="loss"/> within [from, to) and clean otherwise.</summary>
    private static List<LatencySample> LossSeries(DateTime from, DateTime to, double loss)
    {
        var s = new List<LatencySample>();
        for (var t = TestSeries.Start; t < TestSeries.Start + Window; t += TimeSpan.FromSeconds(10))
            s.Add(new LatencySample(t, 20, 20.5, 0.5, t >= from && t < to ? loss : 0));
        return s;
    }

    private static readonly (DateTime Start, DateTime End)[] NoDarkWindows = System.Array.Empty<(DateTime, DateTime)>();

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
        events[0].IsBrief.Should().BeFalse();
        // Breadth/depth are populated for blackouts too (both internet triggers went fully dark),
        // so the scorer's severity = breadth x depth reads full for a real outage.
        events[0].PeakLossPct.Should().Be(100);
        events[0].DegradedTargetCount.Should().Be(2);
        events[0].PathTargetCount.Should().Be(2);
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
    public void Gateway_dark_reads_as_a_local_lan_outage()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var gateway = Series(0, (OutStart, OutEnd, 100)); // the LAN gateway itself went dark
        var olt = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("Gateway", 0, gateway, IsGateway: true),
            new OutageDetector.Hop("AT&T nokia-olt", 1, olt),
            new OutageDetector.Hop("Cloudflare", 2, internet1),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Scope.Should().Be(OutageScope.Local);
    }

    [Fact]
    public void Reachable_gateway_does_not_alter_wan_scope()
    {
        // Gateway stayed up while the access hop and everything beyond went dark - still a whole-WAN
        // outage, never Local, and the gateway's presence must not flip FullWan to Upstream.
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var gateway = Series(0); // reachable throughout
        var olt = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("Gateway", 0, gateway, IsGateway: true),
            new OutageDetector.Hop("AT&T nokia-olt", 1, olt),
            new OutageDetector.Hop("Cloudflare", 2, internet1),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Scope.Should().Be(OutageScope.FullWan);
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
    public void Momentary_blip_below_the_minimum_is_ignored()
    {
        // ~20 s of total loss - below the 30 s floor, a momentary blip, not reported.
        var darkEnd = OutStart.AddSeconds(20);
        var internet1 = Cadenced(t => t >= OutStart && t < darkEnd);
        var internet2 = Cadenced(t => t >= OutStart && t < darkEnd);

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Drop_between_30s_and_2min_is_flagged_as_a_brief_disruption()
    {
        // A clean 45 s drop: above the 30 s floor, below the 2 min brief/full divider. It is
        // reported but classified brief - the lighter tier for short transit/upstream flaps.
        var darkEnd = OutStart.AddSeconds(45);
        var internet1 = Cadenced(t => t >= OutStart && t < darkEnd);
        var internet2 = Cadenced(t => t >= OutStart && t < darkEnd);

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().ContainSingle();
        events[0].IsBrief.Should().BeTrue();
        events[0].Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(30));
        events[0].Duration.Should().BeLessThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void Coincident_partial_loss_across_many_targets_and_asns_is_a_partial_disruption()
    {
        // ~40 s where four independent destinations across four ASNs degrade together at 60-80%
        // loss - never the 95% dark threshold. That is a partial-loss disruption, not a blackout.
        var ds = OutStart;
        var de = OutStart.AddSeconds(40);
        var hops = new[]
        {
            new OutageDetector.Hop("Cloudflare", 0, LossSeries(ds, de, 60), AsnLabel: "Cloudflare"),
            new OutageDetector.Hop("Fastly", 1, LossSeries(ds, de, 80), AsnLabel: "Fastly"),
            new OutageDetector.Hop("AS3356", 2, LossSeries(ds, de, 60), AsnLabel: "AS3356"),
            new OutageDetector.Hop("AS22773", 3, LossSeries(ds, de, 80), AsnLabel: "AS22773"),
            new OutageDetector.Hop("CleanA", 4, LossSeries(ds, de, 0), AsnLabel: "CleanA"),
            new OutageDetector.Hop("CleanB", 5, LossSeries(ds, de, 0), AsnLabel: "CleanB"),
        };

        var events = OutageDetector.DetectPartial(hops, NoDarkWindows, Options);

        events.Should().ContainSingle();
        events[0].IsPartial.Should().BeTrue();
        events[0].IsBrief.Should().BeTrue();
        events[0].PeakLossPct.Should().BeGreaterThanOrEqualTo(60);
        events[0].DegradedTargetCount.Should().Be(4);
        events[0].PathTargetCount.Should().Be(6);
    }

    [Fact]
    public void A_single_lossy_target_is_not_a_partial_disruption()
    {
        var ds = OutStart;
        var de = OutStart.AddMinutes(5);
        var hops = new[]
        {
            new OutageDetector.Hop("Fastly", 0, LossSeries(ds, de, 80), AsnLabel: "Fastly"),
            new OutageDetector.Hop("CleanA", 1, LossSeries(ds, de, 0), AsnLabel: "CleanA"),
            new OutageDetector.Hop("CleanB", 2, LossSeries(ds, de, 0), AsnLabel: "CleanB"),
            new OutageDetector.Hop("CleanC", 3, LossSeries(ds, de, 0), AsnLabel: "CleanC"),
        };

        var events = OutageDetector.DetectPartial(hops, NoDarkWindows, Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Many_degraded_targets_behind_one_asn_do_not_trip_partial_detection()
    {
        // Four lossy targets, but all one ASN - that ASN's own issue, not a path-wide event.
        var ds = OutStart;
        var de = OutStart.AddSeconds(40);
        var hops = new[]
        {
            new OutageDetector.Hop("AS3356 a", 0, LossSeries(ds, de, 80), AsnLabel: "AS3356"),
            new OutageDetector.Hop("AS3356 b", 1, LossSeries(ds, de, 80), AsnLabel: "AS3356"),
            new OutageDetector.Hop("AS3356 c", 2, LossSeries(ds, de, 80), AsnLabel: "AS3356"),
            new OutageDetector.Hop("AS3356 d", 3, LossSeries(ds, de, 80), AsnLabel: "AS3356"),
            new OutageDetector.Hop("CleanA", 4, LossSeries(ds, de, 0), AsnLabel: "CleanA"),
        };

        var events = OutageDetector.DetectPartial(hops, NoDarkWindows, Options);

        events.Should().BeEmpty();
    }

    [Fact]
    public void Partial_disruption_disambiguates_repeated_asn_labels()
    {
        // Several hops of one access ISP would all read as just "AT&T"; only the repeated label gets
        // disambiguated to the specific hop, while unique labels are left as the clean ASN name.
        var ds = OutStart;
        var de = OutStart.AddSeconds(40);
        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, LossSeries(ds, de, 80), AsnLabel: "AT&T"),
            new OutageDetector.Hop("AT&T mtnview-border", 1, LossSeries(ds, de, 80), AsnLabel: "AT&T"),
            new OutageDetector.Hop("Cloudflare", 2, LossSeries(ds, de, 80), AsnLabel: "Cloudflare"),
            new OutageDetector.Hop("Fastly", 3, LossSeries(ds, de, 80), AsnLabel: "Fastly"),
        };

        var events = OutageDetector.DetectPartial(hops, NoDarkWindows, Options);

        events.Should().ContainSingle();
        var names = events[0].Tiers.Select(t => t.Name).ToList();
        names.Should().Contain("AT&T nokia-olt");
        names.Should().Contain("AT&T mtnview-border");
        names.Should().NotContain("AT&T");                 // the bare duplicate label is replaced
        names.Should().Contain("Cloudflare");              // unique labels stay clean
        names.Should().Contain("Fastly");
    }

    [Fact]
    public void Partial_window_overlapping_a_blackout_is_excluded()
    {
        // A blackout also clears the partial threshold; the partial pass must not surface it again.
        var ds = OutStart;
        var de = OutStart.AddSeconds(40);
        var hops = new[]
        {
            new OutageDetector.Hop("Cloudflare", 0, LossSeries(ds, de, 60), AsnLabel: "Cloudflare"),
            new OutageDetector.Hop("Fastly", 1, LossSeries(ds, de, 80), AsnLabel: "Fastly"),
            new OutageDetector.Hop("AS3356", 2, LossSeries(ds, de, 60), AsnLabel: "AS3356"),
            new OutageDetector.Hop("AS22773", 3, LossSeries(ds, de, 80), AsnLabel: "AS22773"),
        };
        var darkWindows = new[] { (OutStart.AddSeconds(-5), OutStart.AddSeconds(45)) };

        var events = OutageDetector.DetectPartial(hops, darkWindows, Options);

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

    [Fact]
    public void Brief_clear_during_staggered_recovery_does_not_split_one_outage()
    {
        // Two trigger targets, both dark across the outage but with a 1-minute healthy blip in
        // the middle (staggered recovery / probe jitter clears the dark-fraction gate for one
        // bucket). That must read as ONE outage, not two - the short gap is coalesced.
        var clearStart = OutStart.AddMinutes(5);
        var clearEnd = clearStart.AddMinutes(1);
        List<LatencySample> WithBlip() => Series(0, (OutStart, clearStart, 100), (clearEnd, OutEnd, 100));
        var internet1 = WithBlip();
        var internet2 = WithBlip();

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().ContainSingle();
        events[0].Start.Should().Be(OutStart);
        events[0].End.Should().Be(OutEnd);
    }

    [Fact]
    public void Monitoring_gap_inside_a_dark_outage_stays_one_outage()
    {
        // The reported failure: one long LAN/gateway outage whose probe stream drops out for
        // stretches mid-event (no samples at all - the agent couldn't record). The dark runs on
        // either side of each data gap must read as ONE continuous outage: a monitoring gap is
        // missing data, not an observed recovery, so it never actually ended. Before the fix each
        // dark run became its own event, all snapping forward to the single true recovery instant
        // and stacking up as overlapping duplicates.
        var cadence = TimeSpan.FromSeconds(10);
        var gapStart = OutStart.AddMinutes(3);
        var gapEnd = OutStart.AddMinutes(7); // 4-minute no-data gap, well beyond OutageMaxGapSeconds
        // Dark (100% loss) across the whole outage EXCEPT the data gap, where no samples exist at
        // all; healthy (0% loss) outside the outage. Recovery is only ever observed at OutEnd.
        List<LatencySample> WithDataGap()
        {
            var s = new List<LatencySample>();
            for (var t = TestSeries.Start; t < TestSeries.Start + Window; t += cadence)
            {
                var inOutage = t >= OutStart && t < OutEnd;
                if (inOutage && t >= gapStart && t < gapEnd) continue; // no sample recorded
                s.Add(new LatencySample(t, 20, 20.5, 0.5, inOutage ? 100 : 0));
            }
            return s;
        }
        var internet1 = WithDataGap();
        var internet2 = WithDataGap();

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().ContainSingle();
        events[0].Start.Should().Be(OutStart);
        events[0].End.Should().Be(OutEnd);
    }

    [Fact]
    public void Gap_longer_than_the_tolerance_stays_two_separate_outages()
    {
        // A long healthy stretch between two dark spans is a genuine recovery then a second
        // outage - well beyond OutageMaxGapSeconds, so the two must NOT be coalesced.
        var firstEnd = OutStart.AddMinutes(3);
        var secondStart = OutStart.AddMinutes(12); // 9-minute clear gap, > OutageMaxGapSeconds
        var secondEnd = secondStart.AddMinutes(3);
        List<LatencySample> TwoSpans() => Series(0, (OutStart, firstEnd, 100), (secondStart, secondEnd, 100));
        var internet1 = TwoSpans();
        var internet2 = TwoSpans();

        var events = OutageDetector.Detect(Triggers(internet1, internet2), System.Array.Empty<OutageDetector.Hop>(), Options);

        events.Should().HaveCount(2);
    }

    [Fact]
    public void No_data_hop_is_not_chosen_as_the_break_point()
    {
        // A target added after the outage has no samples in the outage window. It must NOT
        // render as a hop that "stayed reachable" through the outage, nor be picked as the
        // last-reachable hop that attributes the break - it simply wasn't being measured.
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var olt = Series(0); // measured throughout, stayed reachable
        // Added after the outage: data only AFTER the outage window, none during it.
        var lateTarget = TestSeries.Flat(OutEnd.AddMinutes(1), TimeSpan.FromMinutes(5), rttMs: 20, jitterMs: 0.5, lossPct: 0);

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt),
            new OutageDetector.Hop("Late CDN", 1, lateTarget),
            new OutageDetector.Hop("Cloudflare", 2, internet1),
            new OutageDetector.Hop("Google", 2, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        // The no-data hop is absent from the shape entirely.
        events[0].Tiers.Should().NotContain(t => t.Name == "Late CDN");
        // The break is attributed to the OLT, not the deeper no-data hop.
        events[0].Scope.Should().Be(OutageScope.Upstream);
        events[0].LastReachableHop.Should().Be("AT&T nokia-olt");
    }

    [Fact]
    public void Hop_that_recovers_early_is_not_dragged_to_the_end_by_a_late_blip()
    {
        // The validated AT&T shape: the OLT goes dark at onset, recovers early, then twitches
        // dark for a single probe much later as the upstream heals. That lone late sample must
        // NOT be read as the OLT's recovery - it came back early. Recovery anchors to the last
        // sustained dark bucket, which a single sub-minute sample can't form.
        var cadence = TimeSpan.FromSeconds(5);
        var recover = OutStart.AddMinutes(2);
        var lateBlip = OutStart.AddMinutes(9);
        List<LatencySample> Build(Func<DateTime, bool> isDark)
        {
            var s = new List<LatencySample>();
            for (var t = TestSeries.Start; t < TestSeries.Start + Window; t += cadence)
                s.Add(new LatencySample(t, 20, 20.5, 0.5, isDark(t) ? 100 : 0));
            return s;
        }
        var internet1 = Build(t => t >= OutStart && t < OutEnd);
        var internet2 = Build(t => t >= OutStart && t < OutEnd);
        var olt = Build(t => (t >= OutStart && t < recover) || (t >= lateBlip && t < lateBlip + cadence));

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt),
            new OutageDetector.Hop("Cloudflare", 1, internet1),
            new OutageDetector.Hop("Google", 1, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        var oltState = events[0].Tiers.Single(t => t.Name == "AT&T nokia-olt");
        oltState.RecoveredAt.Should().NotBeNull();
        oltState.RecoveredAt!.Value.Should().BeCloseTo(recover, TimeSpan.FromSeconds(10));
        oltState.RecoveredAt.Value.Should().BeBefore(OutStart.AddMinutes(5));
    }

    [Fact]
    public void Window_and_recovery_carry_real_seconds_not_minute_buckets()
    {
        // Samples land at :17 past each minute. Detection buckets internally, but the reported
        // onset, recovery, and per-hop recovery must come from the actual sample instants -
        // otherwise every time renders on a bucket edge.
        var offset = TimeSpan.FromSeconds(17);
        List<LatencySample> Build()
        {
            var s = new List<LatencySample>();
            for (var t = TestSeries.Start + offset; t < TestSeries.Start + Window + offset; t = t.AddMinutes(1))
            {
                var dark = t >= OutStart + offset && t < OutEnd + offset;
                s.Add(new LatencySample(t, 20, 20.5, 0.5, dark ? 100 : 0));
            }
            return s;
        }
        var internet1 = Build();
        var internet2 = Build();
        var hops = new[]
        {
            new OutageDetector.Hop("Cloudflare", 1, internet1),
            new OutageDetector.Hop("Google", 1, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Start.Should().Be(OutStart + offset);
        events[0].End.Should().Be(OutEnd + offset);
        events[0].Tiers.Where(t => t.RecoveredAt.HasValue)
            .Should().OnlyContain(t => t.RecoveredAt!.Value.Second == 17);
    }

    [Fact]
    public void Groupable_access_hops_with_the_same_signature_merge_distinct_ones_stay_separate()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        // Two AT&T access hops recover together (dark the whole outage); the OLT recovers earlier.
        var olt = Series(0, (OutStart, OutStart.AddMinutes(3), 100));
        var accessLate1 = Series(0, (OutStart, OutEnd, 100));
        var accessLate2 = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, olt, Groupable: true, AsnLabel: "AT&T"),
            new OutageDetector.Hop("AT&T hop-1", 1, accessLate1, Groupable: true, AsnLabel: "AT&T"),
            new OutageDetector.Hop("AT&T hop-2", 2, accessLate2, Groupable: true, AsnLabel: "AT&T"),
            new OutageDetector.Hop("Cloudflare", 3, internet1),
            new OutageDetector.Hop("Google", 4, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        var access = events[0].Tiers.Where(t => t.Name.StartsWith("AT&T")).ToList();
        // Same ASN owns two rows, so each is disambiguated: the lone early OLT by its hostname
        // tail, the two that recovered together by a hop count.
        access.Should().HaveCount(2);
        access.Should().Contain(t => t.Name == "AT&T (nokia-olt)");
        access.Should().Contain(t => t.Name == "AT&T (2 hops)");
        // Internet endpoints keep their own names and are never grouped.
        events[0].Tiers.Should().Contain(t => t.Name == "Cloudflare");
        events[0].Tiers.Should().Contain(t => t.Name == "Google");
    }

    [Fact]
    public void A_unique_access_asn_shows_just_the_asn_name()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var access = Series(0, (OutStart, OutEnd, 100));

        var hops = new[]
        {
            new OutageDetector.Hop("AT&T nokia-olt", 0, access, Groupable: true, AsnLabel: "AT&T"),
            new OutageDetector.Hop("Cloudflare", 1, internet1),
            new OutageDetector.Hop("Google", 2, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        // One access row for the ASN -> no disambiguation, just the ASN name.
        events[0].Tiers.Should().Contain(t => t.Name == "AT&T");
        events[0].Tiers.Should().NotContain(t => t.Name.Contains("nokia-olt"));
    }

    [Fact]
    public void A_target_with_no_data_during_the_outage_is_not_listed()
    {
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        // A hop that only started reporting after the outage ended (didn't exist during it).
        var addedLater = TestSeries.Flat(OutEnd, TimeSpan.FromMinutes(5), rttMs: 20, jitterMs: 0.5, lossPct: 0);

        var hops = new[]
        {
            new OutageDetector.Hop("Late Hop", 0, addedLater, Groupable: true, AsnLabel: "Late"),
            new OutageDetector.Hop("Cloudflare", 1, internet1),
            new OutageDetector.Hop("Google", 2, internet2),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Tiers.Should().NotContain(t => t.Name.Contains("Late"));
        events[0].Tiers.Should().Contain(t => t.Name == "Cloudflare");
    }

    [Fact]
    public void Outage_with_no_in_window_hop_data_reports_with_no_tiers_and_does_not_throw()
    {
        // A fresh install backfilling: the internet trigger has data, but every monitored hop
        // only started reporting after the window. The outage is still flagged; nothing crashes.
        var internet1 = Series(0, (OutStart, OutEnd, 100));
        var internet2 = Series(0, (OutStart, OutEnd, 100));
        var lateOnly = TestSeries.Flat(OutEnd, TimeSpan.FromMinutes(5), rttMs: 20, jitterMs: 0.5, lossPct: 0);
        var hops = new[]
        {
            new OutageDetector.Hop("Late A", 0, lateOnly, Groupable: true, AsnLabel: "Late"),
            new OutageDetector.Hop("Late B", 1, lateOnly, Groupable: false),
        };

        var events = OutageDetector.Detect(Triggers(internet1, internet2), hops, Options);

        events.Should().ContainSingle();
        events[0].Tiers.Should().BeEmpty();
        events[0].Scope.Should().Be(OutageScope.FullWan);
        events[0].LastReachableHop.Should().BeNull();
    }
}
