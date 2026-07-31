using FluentAssertions;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using Xunit;

namespace NetworkOptimizer.Web.Tests.IspHealth;

public class IspHealthScorerTests
{
    private static readonly IspHealthOptions Options = new();
    private static readonly TimeSpan Day = TimeSpan.FromHours(24);
    private static readonly AccessProfile Gpon = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;

    private static readonly DateTime LoadedDownStart = TestSeries.Start.AddHours(12);
    private static readonly DateTime LoadedDownEnd = TestSeries.Start.AddHours(18);
    private static readonly DateTime LoadedUpStart = TestSeries.Start.AddHours(18);
    private static readonly DateTime LoadedUpEnd = TestSeries.Start.AddHours(21);

    /// <summary>
    /// 24h GPON scenario on a 1000/500 Mbps plan: idle except a 6 h download-loaded
    /// stretch and a 3 h upload-loaded stretch. First hop sits at idleRtt when idle
    /// and rises by the given deltas under load.
    /// </summary>
    private static IspHealthInputs BuildInputs(
        double idleRtt = 1.5,
        double loadedDownDelta = 1.0,
        double loadedUpDelta = 1.0,
        double lossPct = 0,
        bool withExpectedSpeeds = true,
        List<AsnSeries>? transit = null,
        List<AsnSeries>? ispAsn = null,
        List<AsnSeries>? ispTargets = null,
        List<AsnSeries>? destinations = null,
        List<List<LatencySample>>? accessHops = null,
        string? firstHopTargetId = null,
        List<CongestionEvent>? congestion = null,
        List<SpeedTestSample>? speedTests = null,
        bool smartQueuesEnabled = false,
        double? internetDeltaMs = null,
        bool lineIdle = false,
        bool hopOrderKnown = false,
        List<OutageEvent>? outages = null,
        TimeSpan? scoreWindow = null,
        HashSet<string>? notTracedTargetIds = null)
    {
        // lineIdle: a near-zero, flat WAN with no load bursts (~0% average load), for
        // exercising the load-calibrated packet-loss ceiling at the idle end.
        var rates = lineIdle
            ? TestSeries.Throughput(TestSeries.Start, Day, 1, 1)
            : TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
                .Select(r => r.Time >= LoadedDownStart && r.Time < LoadedDownEnd
                    ? r with { DownloadBps = 800_000_000 }
                    : r.Time >= LoadedUpStart && r.Time < LoadedUpEnd
                        ? r with { UploadBps = 400_000_000 }
                        : r)
                .ToList();

        var firstHop = TestSeries.Flat(TestSeries.Start, Day, idleRtt, 0.3, lossPct)
            .WithSegment(LoadedDownStart, LoadedDownEnd, idleRtt + loadedDownDelta, 0.3)
            .WithSegment(LoadedUpStart, LoadedUpEnd, idleRtt + loadedUpDelta, 0.3);

        return new IspHealthInputs
        {
            WindowStart = TestSeries.Start,
            WindowEnd = TestSeries.Start + (scoreWindow ?? Day),
            FirstHopSeries = firstHop,
            AccessHopSeries = accessHops ?? new List<List<LatencySample>>(),
            FirstHopTargetId = firstHopTargetId,
            LossPoolSeries = new List<List<LatencySample>> { firstHop },
            TransitAsnSeries = transit ?? new List<AsnSeries>(),
            IspAsnSeries = ispAsn ?? new List<AsnSeries>(),
            IspTargetSeries = ispTargets ?? new List<AsnSeries>(),
            DestinationSeries = destinations ?? new List<AsnSeries>(),
            WanRates = rates,
            InternetMedianDeltaMs = internetDeltaMs,
            ExpectedDownloadMbps = withExpectedSpeeds ? 1000 : null,
            ExpectedUploadMbps = withExpectedSpeeds ? 500 : null,
            ExpectedSpeedSource = withExpectedSpeeds ? "UniFi Network" : null,
            WanSpeedTests = speedTests ?? new List<SpeedTestSample>
            {
                new(TestSeries.Start.AddHours(6), 980, 490)
            },
            CongestionEvents = congestion ?? new List<CongestionEvent>(),
            SmartQueuesEnabled = smartQueuesEnabled,
            HopOrderKnown = hopOrderKnown,
            NotTracedTargetIds = notTracedTargetIds ?? new HashSet<string>(),
            Outages = outages ?? new List<OutageEvent>()
        };
    }

    [Fact]
    public void Outage_drops_overall_by_the_duration_curve()
    {
        new IspHealthScorer(Options).Score(BuildInputs(), Gpon).OverallScore.Should().Be(100);

        OutageEvent Outage(double mins) => new()
        {
            Start = TestSeries.Start.AddHours(2),
            End = TestSeries.Start.AddHours(2).AddMinutes(mins)
        };
        int OverallWith(double mins) => new IspHealthScorer(Options)
            .Score(BuildInputs(outages: new List<OutageEvent> { Outage(mins) }), Gpon).OverallScore;

        // Default severity curve: 10 min -> 14, 60 min -> 45, 8 h -> 90 (off a clean 100).
        OverallWith(10).Should().Be(86);
        OverallWith(60).Should().Be(55);
        OverallWith(480).Should().Be(10);
    }

    // A short, broad, near-total drop, like the user's 28 s / 100% across 5 of 9 targets.
    private static OutageEvent ShortBroadOutage(int seconds = 28, double peakLossPct = 100, int degraded = 5, int total = 9) => new()
    {
        Start = TestSeries.Start.AddHours(2),
        End = TestSeries.Start.AddHours(2).AddSeconds(seconds),
        PeakLossPct = peakLossPct,
        DegradedTargetCount = degraded,
        PathTargetCount = total
    };

    [Fact]
    public void Recurring_micro_outages_compound_far_beyond_one_point()
    {
        int DropFor(int count)
        {
            var events = new List<OutageEvent>();
            for (var i = 0; i < count; i++)
                events.Add(ShortBroadOutage());
            return 100 - new IspHealthScorer(Options).Score(BuildInputs(outages: events), Gpon).OverallScore;
        }

        // A single 28 s near-total broad drop is felt: a few points, not the old flat 1.
        var one = DropFor(1);
        one.Should().BeInRange(2, 5);

        // Ten of them across the window cost far more - the occurrence component compounds rather
        // than collapsing to ~one point the way summed-duration alone would.
        var ten = DropFor(10);
        ten.Should().BeGreaterThan(5 * one);
        ten.Should().BeGreaterThan(15);
    }

    [Fact]
    public void Acknowledged_outage_is_excluded_from_penalty_and_findings_but_still_masks_loss()
    {
        OutageEvent HourOutage(bool acknowledged) => new()
        {
            Start = TestSeries.Start.AddHours(2),
            End = TestSeries.Start.AddHours(3),
            PeakLossPct = 100,
            DegradedTargetCount = 8,
            PathTargetCount = 9,
            Acknowledged = acknowledged
        };

        var unacked = new IspHealthScorer(Options).Score(BuildInputs(outages: new List<OutageEvent> { HourOutage(false) }), Gpon);
        unacked.OverallScore.Should().BeLessThan(100);
        unacked.Issues.Should().Contain(i => i.OutageStarts.Count > 0);

        // "That was me": no penalty, no finding, and the dark hour's loss is still masked
        // from the Packet Loss factor like any blackout (samples inside the span at 100%).
        var inputs = BuildInputs(outages: new List<OutageEvent> { HourOutage(true) });
        inputs.LossPoolSeries[0] = inputs.LossPoolSeries[0]
            .Select(s => s.Time >= TestSeries.Start.AddHours(2) && s.Time < TestSeries.Start.AddHours(3)
                ? s with { LossPercent = 100 }
                : s)
            .ToList();
        var acked = new IspHealthScorer(Options).Score(inputs, Gpon);
        acked.OverallScore.Should().Be(100);
        acked.Issues.Should().NotContain(i => i.OutageStarts.Count > 0);
        acked.Outages.Should().ContainSingle(o => o.Acknowledged && o.ScorePenaltyPoints == 0);
    }

    [Fact]
    public void Outage_penalty_scales_with_breadth_and_depth()
    {
        int Drop(OutageEvent o) => 100 - new IspHealthScorer(Options)
            .Score(BuildInputs(outages: new List<OutageEvent> { o }), Gpon).OverallScore;

        // Same 60 s duration; the broad near-total event must out-penalize the narrow shallow one
        // purely on severity (breadth x depth), since their duration contribution is identical.
        var broadDeep = Drop(ShortBroadOutage(seconds: 60, peakLossPct: 100, degraded: 8, total: 9));
        var narrowShallow = Drop(ShortBroadOutage(seconds: 60, peakLossPct: 55, degraded: 4, total: 9));
        broadDeep.Should().BeGreaterThan(narrowShallow);
    }

    [Fact]
    public void Outage_at_a_quiet_usage_hour_is_softened_and_the_finding_says_so()
    {
        OutageEvent FullOutage(double usageWeight) => new()
        {
            Start = TestSeries.Start.AddHours(2),
            End = TestSeries.Start.AddHours(2).AddMinutes(5),
            PeakLossPct = 100,
            DegradedTargetCount = 9,
            PathTargetCount = 9,
            UsageWeight = usageWeight
        };
        int Drop(OutageEvent o) => 100 - new IspHealthScorer(Options)
            .Score(BuildInputs(outages: new List<OutageEvent> { o }), Gpon).OverallScore;

        // Same outage, quiet-hour weight dings less than a busy-hour (full-weight) one.
        Drop(FullOutage(0.5)).Should().BeLessThan(Drop(FullOutage(1.0)));

        // And the finding explains the softening for a clearly quiet-time event.
        var report = new IspHealthScorer(Options)
            .Score(BuildInputs(outages: new List<OutageEvent> { FullOutage(0.4) }), Gpon);
        report.Issues.Should().Contain(i => i.Description.Contains("typically idle"));
    }

    [Fact]
    public void Micro_outages_weigh_less_over_a_longer_window_a_long_outage_does_not()
    {
        int Drop(List<OutageEvent> outages, TimeSpan window) => 100 - new IspHealthScorer(Options)
            .Score(BuildInputs(outages: outages, scoreWindow: window), Gpon).OverallScore;

        // Frequency is a rate: the same two micro-drops spread over a week is a steadier line than
        // the same two in 48 h, so the occurrence-dominated penalty fades over the longer window.
        var micros = new List<OutageEvent> { ShortBroadOutage(), ShortBroadOutage() };
        var micro48h = Drop(micros, TimeSpan.FromHours(48));
        var micro7d = Drop(micros, TimeSpan.FromDays(7));
        micro7d.Should().BeLessThan(micro48h);
        (micro48h - micro7d).Should().BeGreaterThanOrEqualTo(2);

        // A long, duration-dominated outage weighs roughly the same in either window (duration is
        // absolute; only its small occurrence part rescales).
        var longOutage = new List<OutageEvent>
        {
            new()
            {
                Start = TestSeries.Start.AddHours(2),
                End = TestSeries.Start.AddHours(4),
                PeakLossPct = 100,
                DegradedTargetCount = 9,
                PathTargetCount = 9
            }
        };
        var long48h = Drop(longOutage, TimeSpan.FromHours(48));
        var long7d = Drop(longOutage, TimeSpan.FromDays(7));
        long7d.Should().BeCloseTo(long48h, 3);
    }

    [Fact]
    public void Outage_finding_spells_out_the_score_impact()
    {
        var outage = new OutageEvent
        {
            Start = TestSeries.Start.AddHours(2),
            End = TestSeries.Start.AddHours(2).AddMinutes(60)
        };
        var report = new IspHealthScorer(Options)
            .Score(BuildInputs(outages: new List<OutageEvent> { outage }), Gpon);

        // 60 min off a clean 100 is a 45-point penalty; the finding states it for transparency.
        var finding = report.Issues.Single(i => i.Title == "Internet outage in the window");
        finding.Description.Should().Contain("lowered your ISP Health score by 45 points");
    }

    [Fact]
    public void Ideal_gpon_inputs_score_excellent()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(), Gpon);

        report.AccessDimension.Score.Should().Be(100);
        report.OverallScore.Should().Be(100);
        report.HasExpectedSpeeds.Should().BeTrue();
        report.HasLoadedSamples.Should().BeTrue();
        report.Issues.Should().NotContain(i => i.Title.Contains("Bufferbloat"));
    }

    [Fact]
    public void Midband_idle_latency_scores_average()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.5), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Idle Latency");
        factor.Score.Should().Be(92);
    }

    [Fact]
    public void Loaded_latency_surfaces_spiky_far_hop_not_hidden_by_flat_near_hop()
    {
        // The near hop stays flat under download load; a second access hop (the OLT)
        // briefly spikes to 8 ms. The OLT is the only target above the jitter floor,
        // so global min picks its delta - it must surface, not be hidden by the flat hop.
        var rates = TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
            .Select(r => r.Time >= LoadedDownStart && r.Time < LoadedDownEnd
                ? r with { DownloadBps = 800_000_000 }
                : r)
            .ToList();

        var nearHop = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3);
        var olt = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)
            .WithSegment(LoadedDownStart, LoadedDownEnd, 8.0, 0.3);

        var inputs = new IspHealthInputs
        {
            WindowStart = TestSeries.Start,
            WindowEnd = TestSeries.Start + Day,
            FirstHopSeries = nearHop,
            AccessHopSeries = new List<List<LatencySample>> { nearHop, olt },
            LossPoolSeries = new List<List<LatencySample>> { nearHop },
            WanRates = rates,
            ExpectedDownloadMbps = 1000,
            ExpectedUploadMbps = 500,
            ExpectedSpeedSource = "UniFi Network",
            WanSpeedTests = new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 980, 490) }
        };

        var withOlt = new IspHealthScorer(Options).Score(inputs, Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Loaded Latency");

        withOlt.Score.Should().BeLessThan(100);
        withOlt.ValueText.Should().Contain("6.0 ms down");
    }

    [Fact]
    public void Below_band_idle_latency_scores_higher_than_above_band()
    {
        var scorer = new IspHealthScorer(Options);
        var low = scorer.Score(BuildInputs(idleRtt: 1.5), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Idle Latency").Score;
        var high = scorer.Score(BuildInputs(idleRtt: 4.0), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Idle Latency").Score;

        low.Should().Be(100);
        high.Should().BeLessThan(75);
    }

    [Fact]
    public void Loss_past_acceptable_drops_drastically()
    {
        // On an idle line, 0.075% is well past the strict idle ceiling and collapses.
        var report = new IspHealthScorer(Options).Score(BuildInputs(lossPct: 0.075, lineIdle: true), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Packet Loss");
        factor.Score.Should().BeLessThan(30);
    }

    [Fact]
    public void Packet_loss_ceiling_is_calibrated_to_average_load()
    {
        // The same 0.1% loss is fine on a line that ran loaded much of the window but a
        // real problem on an idle line, where ~no loss is expected.
        var idle = new IspHealthScorer(Options).Score(BuildInputs(lossPct: 0.1, lineIdle: true), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Packet Loss").Score;
        var loaded = new IspHealthScorer(Options).Score(BuildInputs(lossPct: 0.1), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Packet Loss").Score;

        loaded.Should().BeGreaterThan(idle!.Value,
            "the loss ceiling tolerates more when the line was busy over the window");
    }

    [Fact]
    public void Loss_at_ideal_scores_near_perfect()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(lossPct: 0.02), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Packet Loss");
        factor.Score.Should().Be(95);
    }

    [Fact]
    public void Loaded_delta_at_acceptable_scores_seventy()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(loadedDownDelta: 10, loadedUpDelta: 10), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Loaded Latency");
        factor.Score.Should().Be(70);
    }

    [Fact]
    public void Renormalizes_when_expected_speeds_missing()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(withExpectedSpeeds: false), Gpon);

        report.HasExpectedSpeeds.Should().BeFalse();
        report.HasLoadedSamples.Should().BeFalse();
        report.AccessDimension.Factors.Single(f => f.Name == "Loaded Latency").Score.Should().BeNull();
        report.AccessDimension.Factors.Single(f => f.Name == "Loaded Loss").Score.Should().BeNull();
        report.AccessDimension.Score.Should().Be(100);
        report.Issues.Should().Contain(i => i.Title == "Expected ISP speeds not set");
    }

    [Fact]
    public void Sqm_recommendation_triggers_one_band_width_past_excellent()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(loadedDownDelta: 11), Gpon);

        var issue = report.Issues.Single(i => i.Title == "Bufferbloat under load");
        issue.Severity.Should().Be(IspIssueSeverity.Warning);
        issue.Recommendation.Should().Contain("Smart Queues");
        issue.Recommendation.Should().NotContain("Adaptive SQM");
    }

    [Fact]
    public void Sqm_recommendation_mentions_adaptive_sqm_for_recurring_congestion()
    {
        var congestion = new List<CongestionEvent>
        {
            new() { Start = TestSeries.Start.AddHours(19), End = TestSeries.Start.AddHours(21), AsnNumbers = { 64500 } },
            new() { Start = TestSeries.Start.AddHours(21), End = TestSeries.Start.AddHours(22), AsnNumbers = { 64500 } }
        };
        var report = new IspHealthScorer(Options).Score(BuildInputs(loadedDownDelta: 11, congestion: congestion), Gpon);

        var issue = report.Issues.Single(i => i.Title == "Bufferbloat under load");
        issue.Recommendation.Should().Contain("Adaptive SQM");
    }

    [Fact]
    public void No_sqm_recommendation_when_loaded_behavior_is_excellent()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(loadedDownDelta: 1, loadedUpDelta: 1), Gpon);

        report.Issues.Should().NotContain(i => i.Title == "Bufferbloat under load");
    }

    [Fact]
    public void Overall_is_equal_thirds_of_dimensions()
    {
        var noisy = new List<LatencySample>();
        for (var t = TestSeries.Start; t < TestSeries.Start + Day; t = t.AddMinutes(1))
        {
            var rtt = t.Minute % 2 == 0 ? 10.0 : 30.0;
            noisy.Add(new LatencySample(t, rtt, rtt + 8, 8, 0));
        }
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", noisy) };
        var ispAsn = new List<AsnSeries> { TestSeries.Asn(64496, "AccessOne", TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)) };

        var report = new IspHealthScorer(Options).Score(BuildInputs(transit: transit, ispAsn: ispAsn), Gpon);

        var expected = (int)Math.Round(
            (report.AccessDimension.Score!.Value
             + report.TransitDimension.Score!.Value
             + report.IspAsnDimension.Score!.Value) / 3.0);
        report.OverallScore.Should().Be(expected);
        report.TransitDimension.Score.Should().BeLessThan(report.IspAsnDimension.Score!.Value);
    }

    [Fact]
    public void Speed_at_plan_scores_perfect()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 960, 485) }), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan");
        factor.Score.Should().Be(100);
        report.MeasuredDownloadMbps.Should().Be(960);
        report.ExpectedDownloadMbps.Should().Be(1000);
        report.ExpectedSpeedSource.Should().Be("UniFi Network");
    }

    [Fact]
    public void Speed_blends_capacity_with_typical_delivery()
    {
        // Capacity (best) hits plan but the typical (median) result is low, so low
        // tests count: down 0.6 x 100 + 0.4 x 67.75, up 0.6 x 100 + 0.4 x 67 -> 87
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample>
            {
                new(TestSeries.Start.AddHours(19), 600, 300),
                new(TestSeries.Start.AddHours(6), 970, 480)
            }), Gpon);

        report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan").Score.Should().Be(87);
    }

    [Fact]
    public void Speed_trims_outlier_tests_before_grading()
    {
        // One broken-server result among ten; the 15% trim drops it so the factor
        // reflects the healthy tests
        var tests = Enumerable.Range(0, 9)
            .Select(i => new SpeedTestSample(TestSeries.Start.AddHours(i + 2), 960, 480))
            .ToList();
        tests.Add(new SpeedTestSample(TestSeries.Start.AddHours(12), 40, 480));

        var report = new IspHealthScorer(Options).Score(BuildInputs(speedTests: tests), Gpon);

        report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan").Score.Should().Be(100);
    }

    [Fact]
    public void Underdelivered_speed_scores_low_and_raises_issue()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 600, 300) }), Gpon);

        report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan").Score.Should().Be(40);
        report.Issues.Should().Contain(i => i.Title == "Throughput below plan");
    }

    [Fact]
    public void Missing_speed_tests_exclude_factor_and_renormalize()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample>()), Gpon);

        report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan").Score.Should().BeNull();
        report.AccessDimension.Score.Should().Be(100);
    }

    [Fact]
    public void Stale_speed_test_within_fallback_still_scores()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample> { new(TestSeries.Start.AddDays(-3), 980, 490) }), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan");
        factor.Score.Should().Be(100);
        factor.Description.Should().Contain("older than");
    }

    [Fact]
    public void Sparse_window_tops_up_to_min_samples_from_before_window()
    {
        // Two tests inside the 24 h window plus older tests within the 7-day fallback:
        // selection reaches back to reach SpeedTestMinSamples (4) and grades all four.
        // The newest graded test is in-window, so the factor is not marked stale.
        var report = new IspHealthScorer(Options).Score(BuildInputs(
            speedTests: new List<SpeedTestSample>
            {
                new(TestSeries.Start.AddHours(3), 980, 490),
                new(TestSeries.Start.AddHours(6), 970, 485),
                new(TestSeries.Start.AddDays(-1), 960, 480),
                new(TestSeries.Start.AddDays(-2), 950, 475),
                new(TestSeries.Start.AddDays(-4), 940, 470)
            }), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Speed vs Plan");
        factor.Description.Should().Contain("Fastest of 4 WAN tests");
        factor.Description.Should().NotContain("older than");
    }

    [Fact]
    public void Loaded_latency_falls_back_to_speed_test_measurements()
    {
        // No passive load (line idle all day), but a WAN speed test measured its own
        // loaded latency: +1.5 ms over its unloaded ping -> excellent for GPON
        var inputs = BuildInputs(
            speedTests: new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 980, 490, PingMs: 12, DownloadLatencyMs: 13.5, UploadLatencyMs: 13.0) });
        var idleRates = TestSeries.Throughput(TestSeries.Start, TimeSpan.FromHours(24), 50, 5);
        inputs = new IspHealthInputs
        {
            WindowStart = inputs.WindowStart,
            WindowEnd = inputs.WindowEnd,
            FirstHopSeries = inputs.FirstHopSeries,
            LossPoolSeries = inputs.LossPoolSeries,
            TransitAsnSeries = inputs.TransitAsnSeries,
            IspAsnSeries = inputs.IspAsnSeries,
            WanRates = idleRates,
            ExpectedDownloadMbps = inputs.ExpectedDownloadMbps,
            ExpectedUploadMbps = inputs.ExpectedUploadMbps,
            ExpectedSpeedSource = inputs.ExpectedSpeedSource,
            WanSpeedTests = inputs.WanSpeedTests,
            CongestionEvents = inputs.CongestionEvents
        };

        var report = new IspHealthScorer(Options).Score(inputs, Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Loaded Latency");
        factor.Score.Should().Be(100);
        factor.Description.Should().Contain("WAN speed tests");
        report.HasLoadedSamples.Should().BeTrue();
    }

    [Fact]
    public void Sqm_recommendation_triggers_from_speed_test_loaded_latency()
    {
        // Speed test shows +60 ms bufferbloat with no passive load windows
        var inputs = BuildInputs(
            speedTests: new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 980, 490, PingMs: 12, DownloadLatencyMs: 72, UploadLatencyMs: 14) });
        var report = new IspHealthScorer(Options).Score(inputs, Gpon);

        // Passive loaded windows exist in BuildInputs and are excellent; the passive
        // delta wins for the factor, so force the fallback by clearing load
        var idleInputs = new IspHealthInputs
        {
            WindowStart = inputs.WindowStart,
            WindowEnd = inputs.WindowEnd,
            FirstHopSeries = inputs.FirstHopSeries,
            LossPoolSeries = inputs.LossPoolSeries,
            WanRates = TestSeries.Throughput(TestSeries.Start, TimeSpan.FromHours(24), 50, 5),
            ExpectedDownloadMbps = inputs.ExpectedDownloadMbps,
            ExpectedUploadMbps = inputs.ExpectedUploadMbps,
            WanSpeedTests = inputs.WanSpeedTests
        };
        var idleReport = new IspHealthScorer(Options).Score(idleInputs, Gpon);

        idleReport.Issues.Should().Contain(i => i.Title == "Bufferbloat under load");
    }

    [Fact]
    public void Sqm_recommendation_adapts_when_smart_queues_already_enabled()
    {
        var report = new IspHealthScorer(Options).Score(
            BuildInputs(loadedDownDelta: 11, smartQueuesEnabled: true), Gpon);

        var issue = report.Issues.Single(i => i.Title == "Bufferbloat under load");
        issue.Recommendation.Should().NotContain("Enable Smart Queues");
        issue.Recommendation.Should().Contain("configured rates");
    }

    [Fact]
    public void Rural_transit_reach_scores_excellent()
    {
        // First hop at 2 ms (GPON), first transit hops at 8 ms absolute: reach delta 6 ms
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 8, 0.5)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit), Gpon);

        var graded = report.TransitAsns.Single();
        graded.ReachDeltaMs.Should().BeApproximately(6, 0.5);
        graded.ReachLatencyScore.Should().BeInRange(93, 96);
    }

    [Fact]
    public void Metro_subms_transit_reach_scores_perfect()
    {
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 2.5, 0.3)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit), Gpon);

        report.TransitAsns.Single().ReachLatencyScore.Should().Be(100);
    }

    [Fact]
    public void Acceptable_transit_reach_scores_good()
    {
        // 12 ms absolute on a 2 ms access hop: delta 10 ms, still good but not excellent
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 12, 0.5)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit), Gpon);

        report.TransitAsns.Single().ReachLatencyScore.Should().BeInRange(89, 92);
    }

    [Fact]
    public void Rural_far_pop_stays_solid_in_rural_internet_context()
    {
        // Internet sits +13.5 ms beyond access here (rural); a clean POP at 24 ms
        // (1.6x internet distance) is solid geography, not a bad transit
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "FarRegionalTransit", TestSeries.Flat(TestSeries.Start, Day, 24, 0.5)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit, internetDeltaMs: 13.5), Gpon);

        report.TransitAsns.Single().OverallScore.Should().BeGreaterThanOrEqualTo(85);
    }

    [Fact]
    public void Metro_pop_is_judged_against_metro_internet_context()
    {
        // Internet sits +2 ms beyond access (metro); a POP at +5 ms is 2.5x internet
        // distance and grades poorly even though +5 absolute would look fine
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "MetroTransit", TestSeries.Flat(TestSeries.Start, Day, 7, 0.5)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit, internetDeltaMs: 2.0), Gpon);

        report.TransitAsns.Single().ReachLatencyScore.Should().BeLessThanOrEqualTo(78);
    }

    [Fact]
    public void Without_internet_context_far_pops_keep_high_floor()
    {
        // No internet targets: only top-end gravity applies, so distance alone
        // cannot drag a clean rural POP below the high 80s
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "FarTransit", TestSeries.Flat(TestSeries.Start, Day, 24, 0.5)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(idleRtt: 2.0, transit: transit), Gpon);

        report.TransitAsns.Single().OverallScore.Should().BeGreaterThanOrEqualTo(85);
    }

    [Fact]
    public void Asn_loss_lowers_the_grade()
    {
        var lossy = TestSeries.Flat(TestSeries.Start, Day, 8, 0.5, lossPct: 1.5);
        var clean = TestSeries.Flat(TestSeries.Start, Day, 8, 0.5);

        var withLoss = new IspHealthScorer(Options).Score(BuildInputs(
            transit: new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", lossy) }), Gpon);
        var withoutLoss = new IspHealthScorer(Options).Score(BuildInputs(
            transit: new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", clean) }), Gpon);

        withLoss.TransitAsns.Single().LossScore.Should().BeLessThan(60);
        withLoss.TransitAsns.Single().OverallScore.Should().BeLessThan(withoutLoss.TransitAsns.Single().OverallScore!.Value);
    }

    [Fact]
    public void Isp_asns_are_not_graded_on_reach()
    {
        var ispAsn = new List<AsnSeries> { TestSeries.Asn(64496, "AccessOne", TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)) };
        var report = new IspHealthScorer(Options).Score(BuildInputs(ispAsn: ispAsn), Gpon);

        var graded = report.IspAsns.Single();
        graded.ReachLatencyScore.Should().BeNull();
        graded.ReachDeltaMs.Should().BeNull();
        graded.OverallScore.Should().NotBeNull();
    }

    private static AsnSeries IspHop(string targetId, string name, double rttMs, double jitterMs, double lossPct = 0) => new()
    {
        AsnNumber = 64496,
        AsnName = name,
        TargetIds = { targetId },
        Samples = TestSeries.Flat(TestSeries.Start, Day, rttMs, jitterMs, lossPct),
        RoleTargetIds = { targetId }
    };

    // TestSeries.Flat holds RttAvgMs constant (MAD 0); this alternates the per-minute RTT by
    // +/- madMs around rttMs so the sample set has a median of rttMs and an RTT MAD of exactly
    // madMs, while EffectiveJitterMs stays at a low fixed value (so jitter scoring is out of the way
    // and only stability moves). Used to exercise the MAD floor and stability witness absolution.
    private static List<LatencySample> Wander(double rttMs, double madMs, double jitterMs = 0.3, double lossPct = 0)
    {
        var samples = new List<LatencySample>();
        var i = 0;
        for (var t = TestSeries.Start; t < TestSeries.Start + Day; t = t.AddMinutes(1))
        {
            var rtt = rttMs + (i++ % 2 == 0 ? -madMs : madMs);
            samples.Add(new LatencySample(t, rtt, rtt + jitterMs, jitterMs, lossPct));
        }
        return samples;
    }

    private static AsnSeries IspHopMad(string targetId, string name, double rttMs, double madMs, string hopIp) => new()
    {
        AsnNumber = 64496,
        AsnName = name,
        TargetIds = { targetId },
        RoleTargetIds = { targetId },
        HopIps = { hopIp },
        Samples = Wander(rttMs, madMs)
    };

    private static AsnSeries DestMad(string ip, double rttMs, double madMs, params string[] ancestors) => new()
    {
        AsnNumber = 0,
        AsnName = ip,
        TargetIds = { $"dest-{ip}" },
        HopIps = { ip },
        AncestorIps = ancestors.ToList(),
        Samples = Wander(rttMs, madMs),
        IsDestination = true
    };

    [Fact]
    public void All_isp_hops_are_graded_independently()
    {
        // Both hops are the same ISP ASN; each is graded on its own, and the dimension
        // averages every hop grade (not just the first clean hop).
        var hops = new List<AsnSeries>
        {
            IspHop("isp-hop-near", "Near ISP Hop", 2.0, 0.3),
            IspHop("isp-hop-far", "Far ISP Hop", 6.0, 1.5, lossPct: 0.8)
        };

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-hop-near"), Gpon);

        report.IspAsns.Should().ContainSingle("the hops collapse to one ASN card on Networks on Your Path");
        report.IspTargets.Should().HaveCount(2);
        var near = report.IspTargets.Single(t => t.TargetId == "isp-hop-near");
        var far = report.IspTargets.Single(t => t.TargetId == "isp-hop-far");
        near.OverallScore.Should().BeGreaterThan(far.OverallScore!.Value,
            "the near hop is clean while the far hop has jitter, loss, and distance");
        report.IspAsnDimension.Score.Should().Be(
            (int)Math.Round((near.OverallScore!.Value + far.OverallScore!.Value) / 2.0),
            "the dimension score averages all ISP hop grades");
    }

    [Fact]
    public void Far_isp_hop_is_dinged_for_intra_asn_distance_not_perfect()
    {
        // A second POP on the same ISP, 2 ms further out and otherwise clean, should read
        // "fine but not perfect" (~85), not a flawless 100.
        var hops = new List<AsnSeries>
        {
            IspHop("isp-hop-near", "Near ISP Hop", 2.1, 0.3),
            IspHop("isp-hop-far", "Far ISP Hop", 4.1, 0.3)
        };

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-hop-near"), Gpon);

        var far = report.IspTargets.Single(t => t.TargetId == "isp-hop-far");
        far.ReachDeltaMs.Should().BeApproximately(2.0, 0.2);
        far.OverallScore.Should().BeInRange(80, 89);
        report.IspTargets.Single(t => t.TargetId == "isp-hop-near").OverallScore.Should().Be(100);
    }

    [Fact]
    public void Higher_isp_jitter_lowers_the_dimension()
    {
        // Without hop order, an ISP sibling can't absolve another (we can't prove which is
        // downstream), so a jittery hop stays jittery and lowers the dimension vs a clean ISP.
        var clean = new List<AsnSeries>
        {
            IspHop("a", "A", 2.1, 0.4),
            IspHop("b", "B", 2.1, 0.4)
        };
        var jittery = new List<AsnSeries>
        {
            IspHop("a", "A", 2.1, 0.4),
            IspHop("b", "B", 2.1, 3.0)
        };

        var cleanReport = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: clean, ispTargets: clean, firstHopTargetId: "a"), Gpon);
        var jitteryReport = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: jittery, ispTargets: jittery, firstHopTargetId: "a"), Gpon);

        jitteryReport.IspAsnDimension.Score.Should().BeLessThan(cleanReport.IspAsnDimension.Score!.Value,
            "higher mean ISP jitter lowers the ISP grade");
    }

    [Fact]
    public void Isp_jitter_is_capped_by_the_cleanest_transit_asn()
    {
        // The ISP hops look jittery (3 ms, likely ICMP deprioritization), but a transit ASN
        // reached through the ISP is clean at 0.4 ms - proving the ISP path is steady. The
        // ISP grade must not be punished for the false ISP-hop jitter.
        var ispHops = new List<AsnSeries>
        {
            IspHop("isp-a", "ISP A", 2.1, 3.0),
            IspHop("isp-b", "ISP B", 2.2, 3.0)
        };
        var cleanTransit = new List<AsnSeries>
        {
            TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 8, 0.4))
        };

        var withCleanTransit = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: ispHops, ispTargets: ispHops, firstHopTargetId: "isp-a", transit: cleanTransit), Gpon);
        var noTransit = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: ispHops, ispTargets: ispHops, firstHopTargetId: "isp-a"), Gpon);

        withCleanTransit.IspAsnDimension.Score.Should().BeGreaterThan(noTransit.IspAsnDimension.Score!.Value,
            "a clean transit ASN beyond the ISP caps the ISP's jitter");
        withCleanTransit.IspAsns.Single().JitterAssimilated.Should().BeTrue("the transit floor capped the ISP jitter");
        noTransit.IspAsns.Single().JitterAssimilated.Should().BeFalse("no transit to assimilate from");
    }

    [Fact]
    public void Transit_jitter_above_the_isp_mean_does_not_raise_isp_jitter()
    {
        // The cap is min, not max: a jittery transit ASN must never drag the ISP jitter UP.
        // ISP hops are clean (0.3 ms); transit is jittery (2.0 ms). The ISP keeps its own
        // mean for both score and display.
        var ispHops = new List<AsnSeries>
        {
            IspHop("isp-a", "ISP A", 2.1, 0.3),
            IspHop("isp-b", "ISP B", 2.1, 0.3)
        };
        var jitteryTransit = new List<AsnSeries>
        {
            TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 8, 2.0))
        };

        var withJitteryTransit = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: ispHops, ispTargets: ispHops, firstHopTargetId: "isp-a", transit: jitteryTransit), Gpon);
        var noTransit = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: ispHops, ispTargets: ispHops, firstHopTargetId: "isp-a"), Gpon);

        withJitteryTransit.IspAsnDimension.Score.Should().Be(noTransit.IspAsnDimension.Score,
            "a jittery transit ASN must not raise the ISP jitter (cap is min, not max)");
        withJitteryTransit.IspAsns.Single().ScoredJitterMs.Should().BeApproximately(0.3, 0.05,
            "the displayed ISP jitter stays at the ISP mean when transit is not cleaner");
    }

    [Fact]
    public void Rtt_wander_below_the_tech_ideal_scores_perfect_stability()
    {
        // Everything else held equal (same RTT, jitter, no loss, single hop so reach is 0), OverallScore
        // isolates stability. MAD at/below the tech's ideal MAD band anchor (GPON 0.15 ms) is perfect;
        // MAD out toward the poor anchor is penalized - the per-tech absolute-MAD band, not a ratio.
        var steady = IspHopMad("h", "H", 3.0, 0.1, "192.0.2.1");
        var wobbly = IspHopMad("h", "H", 3.0, 0.8, "192.0.2.1");

        var steadyScore = new IspHealthScorer(Options)
            .Score(BuildInputs(ispAsn: new() { steady }, ispTargets: new() { steady }, firstHopTargetId: "h"), Gpon)
            .IspTargets.Single().OverallScore;
        var wobblyScore = new IspHealthScorer(Options)
            .Score(BuildInputs(ispAsn: new() { wobbly }, ispTargets: new() { wobbly }, firstHopTargetId: "h"), Gpon)
            .IspTargets.Single().OverallScore;

        steadyScore.Should().Be(100, "0.1 ms MAD is below the GPON ideal (0.15 ms)");
        wobblyScore.Should().BeLessThan(100, "0.8 ms MAD is well past the GPON typical (0.4 ms)");
    }

    [Fact]
    public void Same_rtt_wander_grades_by_access_tech()
    {
        // The band is tech-aware: 3 ms MAD is a disaster on fiber but normal on LEO. The same hop
        // scores near-zero stability on GPON and near-perfect on the Satellite (Starlink) profile.
        var satellite = IspHealthProfiles.GetProfile(AccessTechnology.Satellite)!;
        var hop = IspHopMad("s", "S", 30.0, 3.0, "192.0.2.1");

        var onGpon = new IspHealthScorer(Options)
            .Score(BuildInputs(ispAsn: new() { hop }, ispTargets: new() { hop }, firstHopTargetId: "s"), Gpon)
            .IspTargets.Single().OverallScore;
        var onSatellite = new IspHealthScorer(Options)
            .Score(BuildInputs(ispAsn: new() { hop }, ispTargets: new() { hop }, firstHopTargetId: "s"), satellite)
            .IspTargets.Single().OverallScore;

        onSatellite.Should().BeGreaterThan(onGpon!.Value + 20,
            "3 ms RTT MAD is normal LEO wander but far past fiber's poor anchor");
    }

    [Fact]
    public void Clean_destination_routing_through_a_hop_absolves_its_rtt_wander()
    {
        // The ISP hop's own RTT wanders (0.8 ms MAD - ICMP-deprioritized control plane), but a
        // monitored destination proven to route through it is steady end-to-end (0.10 ms MAD). That
        // upper-bounds the hop's true wander, so its stability is absolved to perfect (below GPON ideal).
        const string hopIp = "192.0.2.1";
        var wobbly = IspHopMad("isp", "ISP", 3.0, 0.8, hopIp);
        var cleanDest = DestMad("203.0.113.9", 3.0, 0.10, hopIp);

        var absolved = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: new() { wobbly }, ispTargets: new() { wobbly }, destinations: new() { cleanDest },
                firstHopTargetId: "isp", hopOrderKnown: true), Gpon);
        var alone = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: new() { wobbly }, ispTargets: new() { wobbly }, firstHopTargetId: "isp"), Gpon);

        absolved.IspTargets.Single().OverallScore.Should().Be(100,
            "a steadier forwarded path proves the hop's own wander is a per-hop artifact");
        absolved.IspTargets.Single().OverallScore.Should().BeGreaterThan(
            alone.IspTargets.Single().OverallScore!.Value);
    }

    [Fact]
    public void A_clean_destination_that_does_not_cross_a_hop_does_not_absolve_it()
    {
        // Same wobbly hop, but the clean destination routes through a DIFFERENT hop. The
        // routes-through gate must block it - a clean path says nothing about a hop it doesn't cross.
        var wobbly = IspHopMad("isp", "ISP", 3.0, 0.8, "192.0.2.1");
        var offPathDest = DestMad("203.0.113.9", 3.0, 0.15, "198.51.100.7");

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: new() { wobbly }, ispTargets: new() { wobbly }, destinations: new() { offPathDest },
                firstHopTargetId: "isp", hopOrderKnown: true), Gpon);

        report.IspTargets.Single().OverallScore.Should().BeLessThan(100,
            "a clean path that does not cross the hop must not absolve its wander");
    }

    [Fact]
    public void Congestion_on_non_first_hop_affects_isp_dimension()
    {
        var hops = new List<AsnSeries>
        {
            IspHop("isp-hop-near", "Near ISP Hop", 2.0, 0.3),
            IspHop("isp-hop-far", "Far ISP Hop", 5.0, 0.5)
        };
        var congestion = new List<CongestionEvent>
        {
            new()
            {
                Start = TestSeries.Start.AddHours(18),
                End = TestSeries.Start.AddHours(22),
                AsnNumbers = { 64496 },
                TargetIds = { "isp-hop-far" }
            }
        };

        var withCongestion = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-hop-near", congestion: congestion), Gpon);
        var withoutCongestion = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-hop-near"), Gpon);

        withCongestion.IspAsns.Single().CongestionEventCount.Should().Be(1, "the event fired on a hop of this ASN");
        withCongestion.IspAsnDimension.Score.Should().BeLessThan(
            withoutCongestion.IspAsnDimension.Score!.Value,
            "congestion on any ISP hop lowers the ISP dimension score");
    }

    [Fact]
    public void With_hop_order_a_divergent_isp_hop_is_not_absolved_but_an_on_path_one_is()
    {
        // With ancestor data, a clean transit absolves only the ISP hop it routes through
        // (the hop is in its ancestor set). A divergent hop the transit never traverses keeps
        // its own jitter - closing the divergent-path absolve hole.
        var onPath = new AsnSeries
        {
            AsnNumber = 64496,
            AsnName = "ISP",
            TargetIds = { "isp-onpath" },
            RoleTargetIds = { "isp-onpath" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.1, 3.0),
            HopIps = { "10.0.0.1" }
        };
        var divergent = new AsnSeries
        {
            AsnNumber = 64496,
            AsnName = "ISP",
            TargetIds = { "isp-divergent" },
            RoleTargetIds = { "isp-divergent" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.1, 3.0),
            HopIps = { "10.0.0.9" }
        };
        var transit = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "Transit",
            TargetIds = { "transit" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 8, 0.4),
            HopIps = { "20.0.0.1" },
            AncestorIps = { "10.0.0.1" } // routes through the on-path hop only
        };
        var hops = new List<AsnSeries> { onPath, divergent };

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-onpath",
                transit: new List<AsnSeries> { transit }, hopOrderKnown: true), Gpon);

        var onPathGrade = report.IspTargets.Single(t => t.TargetId == "isp-onpath");
        var divergentGrade = report.IspTargets.Single(t => t.TargetId == "isp-divergent");
        onPathGrade.OverallScore.Should().BeGreaterThan(divergentGrade.OverallScore!.Value,
            "the transit absolves the hop it routes through, not the divergent one");
    }

    [Fact]
    public void A_clean_destination_absolves_an_icmp_deprioritized_hop_it_routes_through()
    {
        // An ISP hop measures high jitter to itself (ICMP control-plane deprioritization),
        // but a monitored destination reached THROUGH it (the hop is in the destination's
        // ancestor set) has clean end-to-end jitter - proof the forwarding plane is smooth.
        // The destination's jitter is a hard upper bound on the hop's true jitter, so it
        // absolves the hop. No transit routes through the hop; only the destination does.
        var hop = new AsnSeries
        {
            AsnNumber = 64496,
            AsnName = "ISP",
            TargetIds = { "isp-icmp-hop" },
            RoleTargetIds = { "isp-icmp-hop" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 4.5, 7.0), // high self-jitter
            HopIps = { "10.0.0.9" }
        };
        var cleanDestination = new AsnSeries
        {
            AsnNumber = 64512,
            AsnName = "Destination",
            TargetIds = { "dest-clean" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 6, 0.4), // smooth end-to-end
            HopIps = { "30.0.0.1" },
            AncestorIps = { "10.0.0.9" } // reached through the ICMP-deprioritized hop
        };
        var hops = new List<AsnSeries> { hop };

        var absolved = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-icmp-hop",
                destinations: new List<AsnSeries> { cleanDestination }, hopOrderKnown: true), Gpon)
            .IspTargets.Single(t => t.TargetId == "isp-icmp-hop");

        // Same hop, but the destination does NOT route through it (different ancestor): no absolve.
        var divergentDest = new AsnSeries
        {
            AsnNumber = 64512,
            AsnName = "Destination",
            TargetIds = { "dest-clean" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 6, 0.4),
            HopIps = { "30.0.0.1" },
            AncestorIps = { "10.0.0.1" } // a different hop, not ours
        };
        var notAbsolved = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-icmp-hop",
                destinations: new List<AsnSeries> { divergentDest }, hopOrderKnown: true), Gpon)
            .IspTargets.Single(t => t.TargetId == "isp-icmp-hop");

        absolved.OverallScore.Should().BeGreaterThan(notAbsolved.OverallScore!.Value,
            "a clean destination routing through the hop proves its jitter is an ICMP artifact");
    }

    [Fact]
    public void Isp_target_health_carries_per_target_grade()
    {
        var hops = new List<AsnSeries>
        {
            IspHop("isp-hop-near", "Near ISP Hop", 2.0, 0.3),
            IspHop("isp-hop-far", "Far ISP Hop", 6.0, 1.5, lossPct: 0.8)
        };

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-hop-near"), Gpon);

        report.IspTargets.Should().HaveCount(2);
        var nearTarget = report.IspTargets.Single(t => t.TargetId == "isp-hop-near");
        var farTarget = report.IspTargets.Single(t => t.TargetId == "isp-hop-far");
        nearTarget.OverallScore.Should().NotBeNull();
        farTarget.OverallScore.Should().NotBeNull();
        nearTarget.OverallScore.Should().BeGreaterThan(farTarget.OverallScore!.Value);
        nearTarget.IsGradedHop.Should().BeTrue();
        farTarget.IsGradedHop.Should().BeFalse();
    }

    [Fact]
    public void A_clean_farther_cluster_absolves_false_near_jitter()
    {
        // The near cluster shows 4 ms jitter (false - ICMP deprioritization on that hop),
        // but the farther cluster, reached through it, is clean at 0.4 ms. The ASN must take
        // the better of the two, so it is not punished for the false near jitter.
        var nearCluster = TestSeries.Flat(TestSeries.Start, Day, 10, 4.0);
        var farCluster = TestSeries.Flat(TestSeries.Start, Day, 13, 0.4);
        var withFartherSource = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-near" },
            Samples = nearCluster,
            JitterSourceSamples = farCluster
        };
        var nearOnly = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-near" },
            Samples = nearCluster
        };

        var graded = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { withFartherSource }), Gpon).TransitAsns.Single();
        var ungraded = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { nearOnly }), Gpon).TransitAsns.Single();

        graded.JitterScore.Should().BeGreaterThan(ungraded.JitterScore!.Value,
            "the clean farther cluster disproves the near hop's false jitter");
        graded.JitterScore.Should().BeGreaterThan(85);
        graded.ScoredJitterMs.Should().BeApproximately(0.4, 0.1,
            "the displayed jitter is the absolved value, not the near hop's 4 ms");
        graded.JitterAssimilated.Should().BeTrue("the farther cluster pulled the jitter down");
        graded.RawJitterMs.Should().BeApproximately(4.0, 0.1, "the raw near reading is kept for the tooltip");
    }

    [Fact]
    public void A_clean_destination_absolves_a_transit_asn_it_routes_through()
    {
        // A transit ASN shows high self-jitter (ICMP deprioritization) with no farther cluster of
        // its own to disprove it. A monitored destination reached THROUGH the ASN (its hop is in the
        // destination's ancestor set) is clean end-to-end - a hard upper bound on the ASN's true
        // jitter - so it absolves the transit ASN. Arm A. Divergent ancestry = no absolve (strict).
        AsnSeries Transit() => new()
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-jittery" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 10, 4.0),
            HopIps = { "20.0.0.1" }
        };
        var cleanDest = new AsnSeries
        {
            AsnNumber = 64512,
            AsnName = "Destination",
            TargetIds = { "dest-clean" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 13, 0.4),
            HopIps = { "30.0.0.1" },
            AncestorIps = { "20.0.0.1" } // reached through the transit ASN's hop
        };
        var divergentDest = new AsnSeries
        {
            AsnNumber = 64512,
            AsnName = "Destination",
            TargetIds = { "dest-clean" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 13, 0.4),
            HopIps = { "30.0.0.1" },
            AncestorIps = { "10.9.9.9" } // a different hop - does not route through our transit
        };

        var absolved = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { Transit() },
                destinations: new List<AsnSeries> { cleanDest }, hopOrderKnown: true), Gpon).TransitAsns.Single();
        var notAbsolved = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { Transit() },
                destinations: new List<AsnSeries> { divergentDest }, hopOrderKnown: true), Gpon).TransitAsns.Single();

        absolved.JitterScore.Should().BeGreaterThan(notAbsolved.JitterScore!.Value,
            "a clean destination routing through the transit ASN proves its jitter is an ICMP artifact");
        absolved.JitterAssimilated.Should().BeTrue("the clean destination pulled the jitter down");
    }

    [Fact]
    public void Transit_health_weights_asns_by_internet_host_involvement()
    {
        // Two transit ASNs, one clean and one jittery. The jittery one carries NO monitored internet
        // host, so involvement weighting drops it to the 25% floor and the dimension leans on the
        // clean, host-carrying ASN - scoring higher than the plain average an install with no
        // attribution would get. Arm 4. The destination routes only through the clean ASN, so Arm A
        // can't rescue the jittery one's jitter (isolating the weighting effect).
        List<AsnSeries> Transits() => new()
        {
            new AsnSeries
            {
                AsnNumber = 64500, AsnName = "CleanTransit",
                TargetIds = { "transit-clean" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 10, 0.4),
                HopIps = { "20.0.0.1" }
            },
            new AsnSeries
            {
                AsnNumber = 64600, AsnName = "JitteryTransit",
                TargetIds = { "transit-jittery" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 12, 4.0),
                HopIps = { "21.0.0.1" }
            }
        };
        var dest = new AsnSeries
        {
            AsnNumber = 64512, AsnName = "Destination",
            TargetIds = { "dest-clean" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 13, 0.4),
            HopIps = { "30.0.0.1" },
            AncestorIps = { "20.0.0.1" } // routes through the clean transit only
        };

        var weighted = new IspHealthScorer(Options).Score(
            BuildInputs(transit: Transits(), destinations: new List<AsnSeries> { dest }, hopOrderKnown: true), Gpon)
            .TransitDimension;
        // Baseline: no attribution, so both ASNs weigh equally - the plain average, no tooltips.
        var equal = new IspHealthScorer(Options).Score(
            BuildInputs(transit: Transits(), hopOrderKnown: true), Gpon)
            .TransitDimension;

        weighted.Score.Should().BeGreaterThan(equal.Score!.Value,
            "down-weighting the jittery, host-less transit lifts the dimension toward the clean, host-carrying one");
        weighted.Factors.Single(f => f.Name == "CleanTransit").InvolvementTooltip.Should().Contain("100% weight");
        weighted.Factors.Single(f => f.Name == "JitteryTransit").InvolvementTooltip.Should().Contain("25%");
        equal.Factors.Should().OnlyContain(f => f.InvolvementTooltip == null,
            "with no attributable host there is no involvement to differentiate");
    }

    [Fact]
    public void Off_path_jittery_isp_hop_is_flagged_for_disable()
    {
        // A hop that answers pings but appears on no trace (an OLT that ICMP-deprioritizes traceroute)
        // with high jitter is flagged SuggestDisable. The graded on-path hop never is.
        var graded = new AsnSeries
        {
            AsnNumber = 64496, AsnName = "ISP", TargetIds = { "isp-near" }, RoleTargetIds = { "isp-near" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3), HopIps = { "10.0.0.1" }
        };
        var offPathJittery = new AsnSeries
        {
            AsnNumber = 64496, AsnName = "ISP", TargetIds = { "isp-olt" }, RoleTargetIds = { "isp-olt" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 4.0, 6.0), HopIps = { "10.0.0.9" }
        };
        var hops = new List<AsnSeries> { graded, offPathJittery };

        var report = new IspHealthScorer(Options).Score(
            BuildInputs(ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-near",
                hopOrderKnown: true, notTracedTargetIds: new HashSet<string> { "isp-olt" }), Gpon);

        var olt = report.IspTargets.Single(t => t.TargetId == "isp-olt");
        olt.NotOnTracedPath.Should().BeTrue();
        olt.SuggestDisable.Should().BeTrue("off the traced path and dragging its score with jitter");
        report.IspTargets.Single(t => t.TargetId == "isp-near").SuggestDisable.Should().BeFalse(
            "the graded on-path hop is never suggested for disable");
    }

    [Fact]
    public void Transit_asns_with_no_attributable_hosts_are_floored_and_labeled()
    {
        // A fully-peered site: destinations reach the internet directly, so their (complete) ancestry
        // crosses no transit and every transit ASN carries zero monitored hosts. With attribution
        // available (hop order + destinations), that is a TRUE zero - each transit is floored at 25%
        // and labeled, not left at the equal-weight "unknown" fallback. Arm 4.
        List<AsnSeries> Transits() => new()
        {
            new AsnSeries { AsnNumber = 64500, AsnName = "TransitA", TargetIds = { "ta" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 10, 0.5), HopIps = { "20.0.0.1" } },
            new AsnSeries { AsnNumber = 64600, AsnName = "TransitB", TargetIds = { "tb" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 12, 0.5), HopIps = { "21.0.0.1" } }
        };
        var peeredDest = new AsnSeries
        {
            AsnNumber = 64512, AsnName = "Destination", TargetIds = { "dest" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 8, 0.4), HopIps = { "30.0.0.1" },
            AncestorIps = { "9.9.9.9" } // routes through neither transit (peered)
        };

        var dim = new IspHealthScorer(Options).Score(
            BuildInputs(transit: Transits(), destinations: new List<AsnSeries> { peeredDest }, hopOrderKnown: true), Gpon)
            .TransitDimension;

        // The two transit ASNs carry zero hosts, so both are floored at 25% and labeled.
        dim.Factors.Where(f => f.Name != "IX Peering").Should()
            .OnlyContain(f => f.InvolvementTooltip != null && f.InvolvementTooltip.Contains("25%"),
                "complete ancestry showing zero transit involvement floors and labels every transit ASN");
        // And because the destination is reached directly (no transit, low delta), its measured quality
        // is surfaced as the IX Peering entry carrying full weight - not the neutral-100 fill.
        dim.Factors.Should().ContainSingle(f => f.Name == "IX Peering")
            .Which.InvolvementTooltip.Should().Contain("100% weight");
    }

    [Fact]
    public void Ix_peering_entry_requires_both_low_delta_and_no_transit_on_path()
    {
        // Only the direct-peered destination becomes the synthetic IX Peering entry. The other two are
        // each excluded by one arm of the AND rule:
        //   - ViaTransit: low RTT, but its path crosses a transit ASN (the "fast but not peering" case).
        //   - FarPeer: crosses no transit, but a large best-case delta (peering behind a hidden L2 haul).
        var transit = new List<AsnSeries>
        {
            new() { AsnNumber = 64500, AsnName = "Transit", TargetIds = { "t" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 10, 0.5), HopIps = { "20.0.0.1" } }
        };
        var peered = new AsnSeries
        {
            AsnNumber = 13335, AsnName = "Peered", TargetIds = { "peered" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 4, 0.3), HopIps = { "30.0.0.1" },
            AncestorIps = { "10.0.0.1" } // access ISP hop only - crosses no transit
        };
        var viaTransit = new AsnSeries
        {
            AsnNumber = 15169, AsnName = "ViaTransit", TargetIds = { "via" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 4, 0.3), HopIps = { "31.0.0.1" },
            AncestorIps = { "10.0.0.1", "20.0.0.1" } // low RTT but routes through the transit ASN
        };
        var farPeer = new AsnSeries
        {
            AsnNumber = 54113, AsnName = "FarPeer", TargetIds = { "far" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 20, 0.3), HopIps = { "32.0.0.1" },
            AncestorIps = { "10.0.0.1" } // crosses no transit, but ~18 ms beyond the access hop
        };

        var dim = new IspHealthScorer(Options).Score(
            BuildInputs(transit: transit, destinations: new List<AsnSeries> { peered, viaTransit, farPeer },
                hopOrderKnown: true), Gpon).TransitDimension;

        var ix = dim.Factors.Should().ContainSingle(f => f.Name == "IX Peering").Which;
        ix.Score.Should().NotBeNull("the peered destination's own measured quality drives the entry");
        ix.InvolvementTooltip.Should().Contain("1 of 3 internet targets",
            "only the direct-peered destination qualifies; the transit-crossing and far ones are excluded");
    }

    [Fact]
    public void Ix_peering_entry_is_absent_when_no_destination_is_directly_peered()
    {
        // Every destination crosses the transit ASN (a rural-style backhaul), so no IX Peering entry is
        // synthesized and Transit Health grades on the real transit alone - the prior behavior stands.
        var transit = new List<AsnSeries>
        {
            new() { AsnNumber = 64500, AsnName = "Transit", TargetIds = { "t" },
                Samples = TestSeries.Flat(TestSeries.Start, Day, 10, 0.5), HopIps = { "20.0.0.1" } }
        };
        var viaTransit = new AsnSeries
        {
            AsnNumber = 15169, AsnName = "ViaTransit", TargetIds = { "via" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 12, 0.3), HopIps = { "31.0.0.1" },
            AncestorIps = { "10.0.0.1", "20.0.0.1" }
        };

        var dim = new IspHealthScorer(Options).Score(
            BuildInputs(transit: transit, destinations: new List<AsnSeries> { viaTransit },
                hopOrderKnown: true), Gpon).TransitDimension;

        dim.Factors.Should().NotContain(f => f.Name == "IX Peering");
    }

    [Fact]
    public void Without_hop_order_a_transit_asn_is_graded_on_its_near_cluster()
    {
        // Backward compat: installs that have not re-run discovery have no stored hop
        // order, so the service never sets JitterSourceSamples. The ASN must still grade
        // cleanly - on its nearest cluster's own jitter, with no farther-cluster absolve.
        var nearJittery = TestSeries.Flat(TestSeries.Start, Day, 10, 4.0);
        var noHopOrder = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-near" },
            Samples = nearJittery
            // JitterSourceSamples intentionally empty (no hop order available)
        };

        var graded = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { noHopOrder }), Gpon).TransitAsns.Single();

        graded.OverallScore.Should().NotBeNull();
        graded.MedianJitterMs.Should().BeApproximately(4.0, 0.1, "jitter is the near cluster's own, never absolved without proof");
    }

    [Fact]
    public void A_jittery_farther_cluster_never_downgrades_the_nearer()
    {
        // The near cluster is clean (0.4 ms); the farther cluster is jittery (4 ms). The far
        // cluster's jitter is its own problem further along the path and must NOT drag the
        // nearer cluster's grade down. Absolve-only: take the better, never the worse.
        var nearClean = TestSeries.Flat(TestSeries.Start, Day, 10, 0.4);
        var farJittery = TestSeries.Flat(TestSeries.Start, Day, 13, 4.0);
        var withJitteryFar = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-near" },
            Samples = nearClean,
            JitterSourceSamples = farJittery
        };
        var nearOnly = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "TransitOne",
            TargetIds = { "transit-near" },
            Samples = nearClean
        };

        var withFar = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { withJitteryFar }), Gpon).TransitAsns.Single();
        var without = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { nearOnly }), Gpon).TransitAsns.Single();

        withFar.JitterScore.Should().Be(without.JitterScore,
            "a jittery farther cluster must not downgrade the clean nearer cluster");
        withFar.JitterAssimilated.Should().BeFalse("nothing was assimilated - the near cluster was already cleaner");
    }

    [Fact]
    public void Displayed_rtt_winsorizes_a_flap_so_one_spike_does_not_distort_it()
    {
        // 8 ms baseline all window with a 5-minute spike to 2000 ms (a route flap). The raw
        // mean would jump to ~15 ms; the winsorized mean (P99-capped) stays at the baseline.
        var spikeStart = TestSeries.Start.AddHours(6);
        var series = TestSeries.Flat(TestSeries.Start, Day, 8, 0.5)
            .WithSegment(spikeStart, spikeStart.AddMinutes(5), 2000, 0.5);
        var transit = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", series) };

        var graded = new IspHealthScorer(Options).Score(BuildInputs(transit: transit), Gpon).TransitAsns.Single();

        graded.MeanRttMs.Should().BeApproximately(8, 1.5, "a sub-1% flap is winsorized out of the displayed RTT");
    }

    [Fact]
    public void Congestion_events_lower_the_asn_grade()
    {
        var series = new List<AsnSeries> { TestSeries.Asn(64500, "TransitOne", TestSeries.Flat(TestSeries.Start, Day, 10, 0.5)) };
        var congestion = new List<CongestionEvent>
        {
            new() { Start = TestSeries.Start.AddHours(18), End = TestSeries.Start.AddHours(22), AsnNumbers = { 64500 } }
        };

        var withEvents = new IspHealthScorer(Options).Score(BuildInputs(transit: series, congestion: congestion), Gpon);
        var withoutEvents = new IspHealthScorer(Options).Score(BuildInputs(transit: series), Gpon);

        var graded = withEvents.TransitAsns.Single();
        graded.CongestionEventCount.Should().Be(1);
        graded.CongestionScore.Should().Be(20); // 100 - 20/hr x 4 h
        graded.OverallScore.Should().BeLessThan(withoutEvents.TransitAsns.Single().OverallScore!.Value);
    }

    [Fact]
    public void Same_asn_as_isp_and_transit_attributes_congestion_by_role()
    {
        // A vertically integrated carrier can be the same ASN for both the access ISP and a
        // transit provider. A congestion event on the transit hops must credit only the
        // transit card, not the ISP card.
        var ispSeries = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "IntegratedCarrier",
            TargetIds = { "carrier-isp-hop" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3),
            RoleTargetIds = { "carrier-isp-hop" }
        };
        var transitSeries = new AsnSeries
        {
            AsnNumber = 64500,
            AsnName = "IntegratedCarrier",
            TargetIds = { "carrier-transit-hop" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3),
            RoleTargetIds = { "carrier-transit-hop" }
        };
        var congestion = new List<CongestionEvent>
        {
            new()
            {
                Start = TestSeries.Start.AddHours(19),
                End = TestSeries.Start.AddHours(21),
                AsnNumbers = { 64500 },
                TargetIds = { "carrier-transit-hop" }
            }
        };
        var report = new IspHealthScorer(Options).Score(
            BuildInputs(transit: new List<AsnSeries> { transitSeries }, ispAsn: new List<AsnSeries> { ispSeries }, congestion: congestion), Gpon);

        report.TransitAsns.Single().CongestionEventCount.Should().Be(1, "the event fired on the transit hop");
        report.IspAsns.Single().CongestionEventCount.Should().Be(0, "the ISP hop was not congested");
    }

    [Fact]
    public void Shared_congestion_event_produces_info_issue()
    {
        var congestion = new List<CongestionEvent>
        {
            new() { Start = TestSeries.Start.AddHours(19), End = TestSeries.Start.AddHours(21), AsnNumbers = { 64500, 64501 } }
        };
        var report = new IspHealthScorer(Options).Score(BuildInputs(congestion: congestion), Gpon);

        report.Issues.Should().Contain(i => i.Title == "Shared upstream congestion");
    }

    [Fact]
    public void Path_shifts_never_affect_the_score()
    {
        var baseline = new IspHealthScorer(Options).Score(BuildInputs(), Gpon);
        var inputs = BuildInputs();
        inputs.PathShifts.Add(new PathShiftEvent { Time = TestSeries.Start.AddHours(6), BeforeMedianMs = 10, AfterMedianMs = 20 });
        var withShifts = new IspHealthScorer(Options).Score(inputs, Gpon);

        withShifts.OverallScore.Should().Be(baseline.OverallScore);
        withShifts.PathShifts.Should().HaveCount(1);
    }

    // ─── Pooled loaded-latency: ISP access hops only, raw baseline-subtracted samples
    // pooled across all hops, filtered > 0.5 ms, p25 of the pool. Stable with sparse
    // residential data and robust to ICMP deprioritization. ───

    private static List<LatencySample> LoadedDownHop(double idle, double loadedDelta) =>
        TestSeries.Flat(TestSeries.Start, Day, idle, 0.2, 0)
            .WithSegment(LoadedDownStart, LoadedDownEnd, idle + loadedDelta, 0.2);

    private static double? ResolvedDownDelta(IspHealthInputs inputs)
    {
        var lw = LoadClassifier.Classify(inputs.WanRates, inputs.ExpectedDownloadMbps, inputs.ExpectedUploadMbps, Options);
        return new IspHealthScorer(Options).ResolveLoadedDeltas(inputs, lw).DownMs;
    }

    [Fact]
    public void Loaded_latency_pools_access_hop_samples()
    {
        // Two access hops with similar deltas - pooled p25 reflects the common signal.
        var inputs = BuildInputs(
            accessHops: new() { LoadedDownHop(2, 4), LoadedDownHop(3, 4.5) });

        ResolvedDownDelta(inputs).Should().BeApproximately(4, 1.0);
    }

    [Fact]
    public void Loaded_latency_rejects_icmp_deprioritized_access_hop()
    {
        // One access hop slams to +12 ms under load (ICMP throttle). The other two are
        // at +3. With pooled samples, the deprioritized hop's samples are in the top of
        // the distribution and p25 lands on the real +3 signal.
        var inputs = BuildInputs(
            accessHops: new() { LoadedDownHop(2, 3), LoadedDownHop(3, 3), LoadedDownHop(2.5, 12) });

        ResolvedDownDelta(inputs).Should().BeApproximately(3, 1.0);
        ResolvedDownDelta(inputs).Should().BeLessThan(6);
    }

    [Fact]
    public void Loaded_latency_uses_thin_single_hop_data()
    {
        // One access hop with loaded data - pooled samples from that hop are used.
        var inputs = BuildInputs(accessHops: new() { LoadedDownHop(2, 5) });

        ResolvedDownDelta(inputs).Should().BeApproximately(5, 1.0);
    }

    [Fact]
    public void Loaded_latency_filters_sub_half_ms_deltas()
    {
        // Access hops show sub-0.5 ms delta under load (no meaningful bufferbloat).
        // All samples filtered out, returns null (falls back to speed tests).
        var inputs = BuildInputs(
            accessHops: new() { LoadedDownHop(2, 0.1), LoadedDownHop(3, 0.2) });

        ResolvedDownDelta(inputs).Should().BeNull();
    }

    [Fact]
    public void Loaded_latency_ignores_transit_and_destinations()
    {
        // Transit and internet targets do not contribute to loaded latency.
        // Access hops at +3, destinations at +100 - result is still ~+3.
        var inputs = BuildInputs(
            accessHops: new() { LoadedDownHop(2, 3), LoadedDownHop(3, 3) },
            destinations: new() { new() { AsnNumber = 15169, Samples = LoadedDownHop(13, 100) } });

        ResolvedDownDelta(inputs).Should().BeApproximately(3, 1.0);
    }

    // ── Item E: per-tech jitter band applied to scoring ───────────────────────────

    [Fact]
    public void Docsis_inherent_jitter_is_not_penalized_like_fiber()
    {
        // The same ISP hop at DOCSIS-typical 3 ms jitter: normal for cable, poor for fiber.
        var docsis = IspHealthProfiles.GetProfile(AccessTechnology.Docsis)!;
        var hops = new List<AsnSeries> { IspHop("isp-a", "ISP A", 8.0, 3.0) };

        var docsisReport = new IspHealthScorer(Options).Score(
            BuildInputs(idleRtt: 8.0, ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-a"), docsis);
        var fiberReport = new IspHealthScorer(Options).Score(
            BuildInputs(idleRtt: 8.0, ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-a"), Gpon);

        docsisReport.IspAsnDimension.Score.Should().BeGreaterThan(
            fiberReport.IspAsnDimension.Score!.Value + 10,
            "3 ms jitter is normal on DOCSIS but poor on fiber");
    }

    // ── Item D: ISP access-hop reach blend (internet-relative lift) ────────────────

    [Fact]
    public void Far_isp_hop_is_lifted_when_modest_versus_internet_distance()
    {
        // Two hops in one ISP ASN; the far hop sits 4 ms past the near one but the internet
        // itself is 30 ms out, so that distance is modest in context and should be absolved up.
        var hops = new List<AsnSeries>
        {
            IspHop("isp-near", "Near", 2.0, 0.3),
            IspHop("isp-far", "Far", 6.0, 0.3)
        };

        var withContext = new IspHealthScorer(Options).Score(
            BuildInputs(idleRtt: 2.0, ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-near", internetDeltaMs: 30.0), Gpon);
        var without = new IspHealthScorer(Options).Score(
            BuildInputs(idleRtt: 2.0, ispAsn: hops, ispTargets: hops, firstHopTargetId: "isp-near"), Gpon);

        var farWith = withContext.IspTargets.Single(t => t.TargetId == "isp-far").OverallScore;
        var farWithout = without.IspTargets.Single(t => t.TargetId == "isp-far").OverallScore;

        farWith.Should().BeGreaterThan(farWithout!.Value,
            "the internet-relative blend lifts a hop that's modest relative to internet distance");
        // Lift only - the near (zero-distance) hop is untouched at the top.
        withContext.IspTargets.Single(t => t.TargetId == "isp-near").OverallScore.Should().Be(100);
    }
}

public class IspHealthProfilesTests
{
    [Theory]
    [InlineData(AccessTechnology.Gpon)]
    [InlineData(AccessTechnology.XgsPon)]
    [InlineData(AccessTechnology.Docsis)]
    [InlineData(AccessTechnology.Satellite)]
    [InlineData(AccessTechnology.DirectEthernet)]
    [InlineData(AccessTechnology.FixedWireless)]
    [InlineData(AccessTechnology.Cellular)]
    [InlineData(AccessTechnology.Dsl)]
    [InlineData(AccessTechnology.PppoE)]
    [InlineData(AccessTechnology.Other)]
    public void Every_selectable_technology_has_a_profile(AccessTechnology tech)
    {
        IspHealthProfiles.GetProfile(tech).Should().NotBeNull();
    }

    [Fact]
    public void Unknown_has_no_profile()
    {
        IspHealthProfiles.GetProfile(AccessTechnology.Unknown).Should().BeNull();
    }

    [Fact]
    public void Neutral_profiles_are_flagged()
    {
        IspHealthProfiles.GetProfile(AccessTechnology.PppoE)!.IsNeutral.Should().BeTrue();
        IspHealthProfiles.GetProfile(AccessTechnology.Other)!.IsNeutral.Should().BeTrue();
        IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!.IsNeutral.Should().BeFalse();
    }

    [Fact]
    public void Upstream_loaded_loss_bands_are_at_most_downstream()
    {
        foreach (var tech in Enum.GetValues<AccessTechnology>().Where(t => t != AccessTechnology.Unknown))
        {
            var p = IspHealthProfiles.GetProfile(tech)!;
            p.LoadedLossUpHighPct.Should().BeLessThanOrEqualTo(p.LoadedLossDownHighPct, $"{tech} upstream band should not exceed downstream");
        }
    }

    // ── Item E: per-tech jitter band ──────────────────────────────────────────────

    [Fact]
    public void Jitter_bands_are_set_for_known_techs_and_null_for_neutral()
    {
        foreach (var tech in new[]
        {
            AccessTechnology.Gpon, AccessTechnology.XgsPon, AccessTechnology.Docsis,
            AccessTechnology.Satellite, AccessTechnology.DirectEthernet,
            AccessTechnology.FixedWireless, AccessTechnology.Cellular, AccessTechnology.Dsl
        })
        {
            var p = IspHealthProfiles.GetProfile(tech)!;
            p.JitterIdealMs.Should().NotBeNull($"{tech} should carry a jitter band");
            p.JitterTypicalMs.Should().NotBeNull();
            p.JitterPoorMs.Should().NotBeNull();
        }

        // Neutral techs keep the measured-floor curve - no band.
        IspHealthProfiles.GetProfile(AccessTechnology.PppoE)!.JitterTypicalMs.Should().BeNull();
        IspHealthProfiles.GetProfile(AccessTechnology.Other)!.JitterTypicalMs.Should().BeNull();
    }

    // ── PPPoE session overlay ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(AccessTechnology.Gpon, 2.0)]
    [InlineData(AccessTechnology.XgsPon, 2.0)]
    [InlineData(AccessTechnology.DirectEthernet, 1.0)]
    [InlineData(AccessTechnology.FixedWireless, 1.0)]
    [InlineData(AccessTechnology.Dsl, 1.0)]
    [InlineData(AccessTechnology.Docsis, 0.0)]
    [InlineData(AccessTechnology.Satellite, 0.0)]
    [InlineData(AccessTechnology.Cellular, 0.0)]
    public void Pppoe_overlay_shifts_every_rtt_anchor_by_the_same_offset(AccessTechnology tech, double offset)
    {
        var baseline = IspHealthProfiles.GetProfile(tech)!;
        var pppoe = IspHealthProfiles.ApplyPppoeSession(baseline, tech);

        // Additive and uniform: a fixed topology cost moves "poor" as far as "ideal".
        pppoe.IdleRttIdealMs.Should().BeApproximately(baseline.IdleRttIdealMs + offset, 0.001);
        pppoe.IdleRttNormalLowMs.Should().BeApproximately(baseline.IdleRttNormalLowMs + offset, 0.001);
        pppoe.IdleRttNormalHighMs.Should().BeApproximately(baseline.IdleRttNormalHighMs + offset, 0.001);
        pppoe.IdleRttPoorMs.Should().BeApproximately(baseline.IdleRttPoorMs + offset, 0.001);
    }

    [Fact]
    public void Pppoe_overlay_composes_jitter_in_quadrature_not_linearly()
    {
        var baseline = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;
        var pppoe = IspHealthProfiles.ApplyPppoeSession(baseline, AccessTechnology.Gpon);

        // sqrt(0.4^2 + 0.25^2) = 0.4717, NOT 0.65. The distinction is the point: linear addition
        // would move the ideal anchor 63% and let a genuinely jittery line score a flat 100.
        pppoe.JitterIdealMs.Should().BeApproximately(0.47, 0.005);
        pppoe.JitterTypicalMs.Should().BeApproximately(0.74, 0.005);

        // The same 0.25 ms is absorbed almost entirely once the medium is already noisy.
        pppoe.JitterPoorMs.Should().BeApproximately(3.01, 0.005);
    }

    [Fact]
    public void Pppoe_overlay_leaves_a_null_jitter_band_null()
    {
        // Neutral profiles score jitter off the measured path floor; the overlay must not
        // conjure a band for them, or they would silently switch scoring modes.
        var neutral = IspHealthProfiles.GetProfile(AccessTechnology.Other)!;
        var pppoe = IspHealthProfiles.ApplyPppoeSession(neutral, AccessTechnology.Other);

        pppoe.JitterIdealMs.Should().BeNull();
        pppoe.JitterTypicalMs.Should().BeNull();
        pppoe.JitterPoorMs.Should().BeNull();
    }

    [Fact]
    public void Pppoe_overlay_widens_loaded_loss_additively()
    {
        var baseline = IspHealthProfiles.GetProfile(AccessTechnology.Gpon)!;
        var pppoe = IspHealthProfiles.ApplyPppoeSession(baseline, AccessTechnology.Gpon);

        pppoe.LoadedLossDownLowPct.Should().BeApproximately(1.5, 0.001);
        pppoe.LoadedLossDownHighPct.Should().BeApproximately(3.0, 0.001);
        pppoe.LoadedLossUpLowPct.Should().BeApproximately(1.0, 0.001);
        pppoe.LoadedLossUpHighPct.Should().BeApproximately(2.0, 0.001);
    }

    [Fact]
    public void Pppoe_overlay_never_tightens_a_band()
    {
        // The overlay exists to forgive a cost the user's medium is not responsible for. Any
        // axis it makes STRICTER would penalize PPPoE users for having PPPoE detected.
        foreach (var tech in Enum.GetValues<AccessTechnology>().Where(t => t != AccessTechnology.Unknown))
        {
            var b = IspHealthProfiles.GetProfile(tech)!;
            var p = IspHealthProfiles.ApplyPppoeSession(b, tech);

            p.IdleRttIdealMs.Should().BeGreaterThanOrEqualTo(b.IdleRttIdealMs, $"{tech} ideal RTT");
            p.IdleRttPoorMs.Should().BeGreaterThanOrEqualTo(b.IdleRttPoorMs, $"{tech} poor RTT");
            p.LoadedLossDownHighPct.Should().BeGreaterThanOrEqualTo(b.LoadedLossDownHighPct, $"{tech} loaded loss down");
            p.LoadedLossUpHighPct.Should().BeGreaterThanOrEqualTo(b.LoadedLossUpHighPct, $"{tech} loaded loss up");
            if (b.JitterIdealMs is { } ideal)
                p.JitterIdealMs.Should().BeGreaterThanOrEqualTo(ideal, $"{tech} jitter ideal");
        }
    }

    [Fact]
    public void Pppoe_overlay_preserves_the_medium_identity()
    {
        // The overlay adjusts thresholds; it does not reclassify the line. SharedMedium in
        // particular drives the packet-loss recommendation text, and a PPPoE session does not
        // turn a dedicated pair into a contended one.
        var dsl = IspHealthProfiles.GetProfile(AccessTechnology.Dsl)!;
        var pppoe = IspHealthProfiles.ApplyPppoeSession(dsl, AccessTechnology.Dsl);

        pppoe.SharedMedium.Should().BeFalse();
        pppoe.IsNeutral.Should().Be(dsl.IsNeutral);
        pppoe.DisplayName.Should().Be(dsl.DisplayName);
    }

    [Fact]
    public void Pppoe_overlay_is_a_no_op_on_a_wan_already_stored_as_pppoe()
    {
        // The PppoE profile IS the stand-in for a PPPoE line. A legacy WAN still carrying that
        // value, on a real ppp* interface, would otherwise be forgiven twice.
        var stored = IspHealthProfiles.GetProfile(AccessTechnology.PppoE)!;
        var overlaid = IspHealthProfiles.ApplyPppoeSession(stored, AccessTechnology.PppoE);

        overlaid.Should().BeEquivalentTo(stored);
    }

    [Fact]
    public void Pppoe_overlay_still_applies_to_other()
    {
        // "Other" means a medium we don't list, which a PPPoE session genuinely rides on top of -
        // unlike PppoE itself, it is not already standing in for the session.
        var other = IspHealthProfiles.GetProfile(AccessTechnology.Other)!;
        var overlaid = IspHealthProfiles.ApplyPppoeSession(other, AccessTechnology.Other);

        overlaid.LoadedLossDownHighPct.Should().BeGreaterThan(other.LoadedLossDownHighPct);
    }

    [Fact]
    public void Pppoe_overlay_keeps_upstream_loss_band_within_downstream()
    {
        // Same invariant the base profiles are held to above - the overlay must not invert it.
        foreach (var tech in Enum.GetValues<AccessTechnology>().Where(t => t != AccessTechnology.Unknown))
        {
            var p = IspHealthProfiles.ApplyPppoeSession(IspHealthProfiles.GetProfile(tech)!, tech);
            p.LoadedLossUpHighPct.Should().BeLessThanOrEqualTo(p.LoadedLossDownHighPct, $"{tech} upstream band should not exceed downstream");
        }
    }
}
