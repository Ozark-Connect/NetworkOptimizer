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
        double idleRtt = 2.0,
        double loadedDownDelta = 1.0,
        double loadedUpDelta = 1.0,
        double lossPct = 0,
        bool withExpectedSpeeds = true,
        List<AsnSeries>? transit = null,
        List<AsnSeries>? ispAsn = null,
        List<CongestionEvent>? congestion = null,
        List<SpeedTestSample>? speedTests = null,
        bool smartQueuesEnabled = false,
        double? internetDeltaMs = null)
    {
        var rates = TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
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
            WindowEnd = TestSeries.Start + Day,
            FirstHopSeries = firstHop,
            LossPoolSeries = new List<List<LatencySample>> { firstHop },
            TransitAsnSeries = transit ?? new List<AsnSeries>(),
            IspAsnSeries = ispAsn ?? new List<AsnSeries>(),
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
            SmartQueuesEnabled = smartQueuesEnabled
        };
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
        factor.Score.Should().Be(75);
    }

    [Fact]
    public void Loaded_latency_takes_worst_access_hop_so_a_spiky_far_hop_is_not_hidden_by_a_flat_near_hop()
    {
        // The near hop stays flat under download load; a second access hop (the OLT)
        // briefly spikes to 8 ms. p95 worst-hop must surface the spike rather than letting
        // the flat near hop read +0 ms - the regression that prompted this (real Mac event).
        var rates = TestSeries.Throughput(TestSeries.Start, Day, 50, 5)
            .Select(r => r.Time >= LoadedDownStart && r.Time < LoadedDownEnd
                ? r with { DownloadBps = 800_000_000 }
                : r)
            .ToList();

        var nearHop = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3);
        var spikeStart = LoadedDownStart.AddHours(1);
        var olt = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3)
            .WithSegment(spikeStart, spikeStart.AddMinutes(30), 8.0, 0.3);

        IspHealthInputs Make(List<List<LatencySample>> accessHops) => new()
        {
            WindowStart = TestSeries.Start,
            WindowEnd = TestSeries.Start + Day,
            FirstHopSeries = nearHop,
            AccessHopSeries = accessHops,
            LossPoolSeries = new List<List<LatencySample>> { nearHop },
            WanRates = rates,
            ExpectedDownloadMbps = 1000,
            ExpectedUploadMbps = 500,
            ExpectedSpeedSource = "UniFi Network",
            WanSpeedTests = new List<SpeedTestSample> { new(TestSeries.Start.AddHours(6), 980, 490) }
        };

        var scorer = new IspHealthScorer(Options);
        var nearOnly = scorer.Score(Make(new List<List<LatencySample>> { nearHop }), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Loaded Latency");
        var withOlt = scorer.Score(Make(new List<List<LatencySample>> { nearHop, olt }), Gpon)
            .AccessDimension.Factors.Single(f => f.Name == "Loaded Latency");

        nearOnly.Score.Should().Be(100);
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
        high.Should().BeLessThan(62);
    }

    [Fact]
    public void Loss_past_acceptable_drops_drastically()
    {
        var report = new IspHealthScorer(Options).Score(BuildInputs(lossPct: 0.075), Gpon);

        var factor = report.AccessDimension.Factors.Single(f => f.Name == "Packet Loss");
        factor.Score.Should().BeLessThan(30);
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
        // AT&T is AS7018 for both the access ISP and a transit provider. A congestion
        // event on the transit hops must credit only the transit card, not the ISP card.
        var ispSeries = new AsnSeries
        {
            AsnNumber = 7018,
            AsnName = "AT&T",
            TargetIds = { "att-isp-hop" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3),
            RoleTargetIds = { "att-isp-hop" }
        };
        var transitSeries = new AsnSeries
        {
            AsnNumber = 7018,
            AsnName = "AT&T",
            TargetIds = { "att-transit-hop" },
            Samples = TestSeries.Flat(TestSeries.Start, Day, 2.0, 0.3),
            RoleTargetIds = { "att-transit-hop" }
        };
        var congestion = new List<CongestionEvent>
        {
            new()
            {
                Start = TestSeries.Start.AddHours(19),
                End = TestSeries.Start.AddHours(21),
                AsnNumbers = { 7018 },
                TargetIds = { "att-transit-hop" }
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
}
