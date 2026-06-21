using System.Globalization;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Pure scoring engine for ISP Health. Takes pre-assembled inputs (latency series,
/// throughput, detected events) plus an access technology profile and produces the
/// full report. No I/O; fully unit-testable. Formulas and anchor points are tuned
/// against real incident data.
/// </summary>
public class IspHealthScorer
{
    private readonly IspHealthOptions _options;
    private readonly ILogger? _logger;

    // Outage windows for the current report. An outage's only score impact is the single
    // capped Packet Loss penalty, so its near-total-loss samples are excluded from every
    // other loss aggregation (per-ASN/hop grades, loaded loss, displayed loss) - otherwise
    // they would double-count and tank the Transit/ISP dimensions. Set per Score() call.
    private IReadOnlyList<OutageEvent> _outages = System.Array.Empty<OutageEvent>();

    // The access profile for the current report, set per Score() call. Carries the per-tech jitter
    // band (Item E) used to grade ISP and transit jitter against the access medium's inherent floor.
    private AccessProfile? _profile;

    private bool InOutage(DateTime time) => _outages.Any(o => time >= o.Start && time < o.End);

    public IspHealthScorer(IspHealthOptions options, ILogger? logger = null)
    {
        _options = options;
        _logger = logger;
    }

    public IspHealthReport Score(IspHealthInputs inputs, AccessProfile profile)
    {
        _outages = inputs.Outages;
        _profile = profile;
        if (inputs.LoadExclusionWindows.Count > 0)
        {
            foreach (var (exStart, exEnd) in inputs.LoadExclusionWindows)
                _logger?.LogDebug("ISP Health: excluding SQM probe window {Start} to {End}", exStart.ToString("u"), exEnd.ToString("u"));
        }
        var loadWindows = LoadClassifier.Classify(inputs.WanRates, inputs.ExpectedDownloadMbps, inputs.ExpectedUploadMbps, _options, inputs.LoadExclusionWindows, _logger);
        var hasExpectedSpeeds = inputs.ExpectedDownloadMbps.HasValue || inputs.ExpectedUploadMbps.HasValue;

        var idleBaseline = ComputeIdleBaseline(inputs.FirstHopSeries, loadWindows);
        var avgLoad = ComputeAverageLoad(inputs);
        var (speedVsPlan, bestSpeedTest, typicalDownMbps, typicalUpMbps) = ScoreSpeedVsPlan(inputs);
        var idleLatency = ScoreIdleLatency(idleBaseline, profile);
        var idleLoss = ScoreIdleLoss(inputs.LossPoolSeries, profile, avgLoad);
        var loadedDeltas = ResolveLoadedDeltas(inputs, loadWindows);

        // The path jitter floor: the quietest median jitter measured anywhere along the
        // path (ISP hops and transit clusters). It represents the access layer's inherent
        // stability - every probe crosses it - so jitter is graded relative to this floor.
        var jitterFloor = ComputeJitterFloor(inputs);
        _logger?.LogDebug("ISP Health: path jitter floor {Floor} ms", FormatMsOrNull(jitterFloor));
        var (loadedLatency, hasLoadedLatency) = ScoreLoadedLatency(loadedDeltas, profile);
        var (loadedLoss, hasLoadedLoss) = ScoreLoadedLoss(inputs.LossPoolSeries, loadWindows, profile);

        var accessFactors = new List<IspScoreFactor> { speedVsPlan, idleLatency, idleLoss, loadedLatency, loadedLoss };
        var accessDimension = BuildDimension("Access Layer", _options.AccessWeight, accessFactors);

        var accessMedianRtt = SeriesStats.Median(
            inputs.FirstHopSeries.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList());

        var transitAsns = inputs.TransitAsnSeries.Select(s => GradeAsn(s, inputs.CongestionEvents, jitterFloor, accessMedianRtt, inputs.InternetMedianDeltaMs)).ToList();

        // Every ISP hop is graded; the dimension averages them all. Each hop's jitter is
        // absolved per-hop and routes-through-gated (a transit ASN or deeper ISP hop only
        // absolves a hop it is proven downstream of), so a divergent clean transit can't
        // clear a congested hop it never traverses. Hops further out on the same ISP also
        // get a soft intra-ASN reach ceiling. Access layer idle latency still uses FirstHopSeries.
        var ispHopGrades = GradeIspHops(inputs.IspAsnSeries, inputs.TransitAsnSeries, transitAsns, inputs.DestinationSeries, inputs.CongestionEvents, jitterFloor, inputs.HopOrderKnown, accessMedianRtt, inputs.InternetMedianDeltaMs);
        // Collapse the per-hop grades to one entry per ASN for the Networks on Your Path card.
        var ispAsns = AggregateIspAsns(ispHopGrades, inputs.CongestionEvents, _options.JitterAssimilationMinDeltaMs);
        var transitDimension = BuildAsnDimension("Transit Health", _options.TransitWeight, transitAsns);
        var ispAsnDimension = BuildIspDimension(_options.IspAsnWeight, ispHopGrades);

        var overall = CombineDimensions(accessDimension, transitDimension, ispAsnDimension);
        // An outage is scored once, here at the top level, on a duration curve - so a long
        // outage actually drives the score down instead of being diluted to a couple of
        // points inside one factor. Scored by total downtime alone, shape-independent.
        // Local (LAN/gateway) outages are surfaced but never penalize the ISP - the gateway being
        // unreachable is the user's own LAN, not the ISP's fault (they still mask their dark window
        // from the other factors via InOutage, so that loss isn't double-counted against the ISP).
        var wanOutages = inputs.Outages.Where(o => o.Scope != OutageScope.Local).ToList();
        var outageMinutes = wanOutages.Sum(o => o.Duration.TotalMinutes);
        if (outageMinutes > 0)
        {
            var penalty = OutageScorePenalty(outageMinutes);
            // Attribute the total (curve-based) penalty across the WAN outages by duration share so
            // each outage row can show its own "-N points". Rounded shares may differ from the curve
            // total by <=1 pt - cosmetic; the actual score deduction uses the curve total below.
            foreach (var o in wanOutages)
                o.ScorePenaltyPoints = (int)Math.Round(penalty * (o.Duration.TotalMinutes / outageMinutes));
            _logger?.LogDebug("ISP Health: outage penalty {Penalty} pts over {Min} min downtime ({Before} -> {After})",
                penalty.ToString("0.#", CultureInfo.InvariantCulture), outageMinutes.ToString("0", CultureInfo.InvariantCulture),
                overall, (int)Math.Max(0, Math.Round(overall - penalty)));
            overall = (int)Math.Max(0, Math.Round(overall - penalty));
        }

        var report = new IspHealthReport
        {
            OverallScore = overall,
            ComputedAt = DateTime.UtcNow,
            WindowStart = inputs.WindowStart,
            WindowEnd = inputs.WindowEnd,
            Profile = profile,
            AccessDimension = accessDimension,
            TransitDimension = transitDimension,
            IspAsnDimension = ispAsnDimension,
            TransitAsns = transitAsns,
            IspAsns = ispAsns,
            IspTargets = inputs.IspTargetSeries.Select(s => BuildIspTargetHealth(s, inputs.FirstHopTargetId, ispHopGrades, _options.RttWinsorPercentile)).ToList(),
            CongestionEvents = inputs.CongestionEvents,
            PathShifts = inputs.PathShifts,
            Outages = inputs.Outages,
            HasExpectedSpeeds = hasExpectedSpeeds,
            HasUpstreamTraceMap = inputs.HopOrderKnown,
            HasLoadedSamples = hasLoadedLatency || hasLoadedLoss,
            ExpectedDownloadMbps = inputs.ExpectedDownloadMbps,
            ExpectedUploadMbps = inputs.ExpectedUploadMbps,
            ExpectedSpeedSource = inputs.ExpectedSpeedSource,
            MeasuredDownloadMbps = bestSpeedTest?.DownloadMbps,
            MeasuredUploadMbps = bestSpeedTest?.UploadMbps,
            TypicalDownloadMbps = typicalDownMbps,
            TypicalUploadMbps = typicalUpMbps,
            SpeedTestTime = bestSpeedTest?.Time
        };
        report.Issues.AddRange(CollectIssues(inputs, profile, report, loadWindows, loadedDeltas));
        return report;
    }

    /// <summary>Where the loaded latency evidence came from.</summary>
    internal record LoadedDeltas(double? DownMs, double? UpMs, bool DownFromSpeedTest, bool UpFromSpeedTest)
    {
        public bool FromSpeedTests => DownFromSpeedTest || UpFromSpeedTest;
    }

    /// <summary>
    /// Loaded latency deltas per direction. Passive evidence first: latency samples
    /// inside windows where WAN throughput was solid for LoadWindowSeconds. When a
    /// direction lacks enough passive samples, falls back to the WAN speed tests'
    /// own measurements: loaded latency during the saturating test minus the test's
    /// unloaded ping on the same path.
    /// </summary>
    internal LoadedDeltas ResolveLoadedDeltas(
        IspHealthInputs inputs,
        Dictionary<DateTime, LoadWindow> loadWindows)
    {
        double? down = null, up = null;
        if (loadWindows.Count > 0)
        {
            down = LoadedLatencyDelta(inputs, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
            up = LoadedLatencyDelta(inputs, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);
        }

        bool downFromSpeedTest = false, upFromSpeedTest = false;
        if (down == null || up == null)
        {
            var (tests, _) = SelectSpeedTests(inputs);
            var downDeltas = tests
                .Where(t => t.DownloadLatencyMs.HasValue && t.PingMs.HasValue)
                .Select(t => Math.Max(0, t.DownloadLatencyMs!.Value - t.PingMs!.Value))
                .ToList();
            var upDeltas = tests
                .Where(t => t.UploadLatencyMs.HasValue && t.PingMs.HasValue)
                .Select(t => Math.Max(0, t.UploadLatencyMs!.Value - t.PingMs!.Value))
                .ToList();
            if (down == null && downDeltas.Count > 0)
            {
                down = SeriesStats.Median(downDeltas);
                downFromSpeedTest = true;
            }
            if (up == null && upDeltas.Count > 0)
            {
                up = SeriesStats.Median(upDeltas);
                upFromSpeedTest = true;
            }
        }
        return new LoadedDeltas(down, up, downFromSpeedTest, upFromSpeedTest);
    }

    /// <summary>
    /// Median RTT of the first clean ISP hop during idle windows. Without load
    /// classification, falls back to the 10th percentile of all RTTs, which
    /// approximates the uncongested floor.
    /// </summary>
    private double? ComputeIdleBaseline(IReadOnlyList<LatencySample> firstHop, Dictionary<DateTime, LoadWindow> loadWindows)
    {
        var rtts = firstHop.Where(s => s.RttAvgMs.HasValue).ToList();
        if (rtts.Count == 0) return null;

        var idleRtts = rtts
            .Where(s => loadWindows.TryGetValue(FloorToWindow(s.Time), out var w) && w.IsIdle)
            .Select(s => s.RttAvgMs!.Value)
            .ToList();
        if (idleRtts.Count > 0) return SeriesStats.WinsorizedMean(idleRtts, _options.RttWinsorPercentile);

        return SeriesStats.Percentile(rtts.Select(s => s.RttAvgMs!.Value).ToList(), 0.10);
    }

    /// <summary>
    /// Picks the WAN speed tests to grade: those inside the score window, else the
    /// most recent within SpeedTestFallbackDays (marked stale).
    /// </summary>
    private (List<SpeedTestSample> Tests, bool Stale) SelectSpeedTests(IspHealthInputs inputs)
    {
        var inWindow = inputs.WanSpeedTests.Where(t => t.Time >= inputs.WindowStart && t.Time <= inputs.WindowEnd).ToList();
        if (inWindow.Count > 0) return (inWindow, false);

        var fallbackStart = inputs.WindowEnd.AddDays(-_options.SpeedTestFallbackDays);
        var latest = inputs.WanSpeedTests.Where(t => t.Time >= fallbackStart).OrderByDescending(t => t.Time).FirstOrDefault();
        return latest == null ? (new List<SpeedTestSample>(), false) : (new List<SpeedTestSample> { latest }, true);
    }

    /// <summary>
    /// Grades demonstrated WAN throughput against the configured plan speeds. Per
    /// direction, the lowest SpeedTestOutlierTrimFraction of results is discarded
    /// (broken test servers, flukes), then the score blends the best remaining result
    /// (demonstrated capacity) with the median (typical delivery) so chronically low
    /// tests count without a single bad test tanking the factor.
    /// </summary>
    private (IspScoreFactor Factor, SpeedTestSample? Best, double? TypicalDown, double? TypicalUp) ScoreSpeedVsPlan(IspHealthInputs inputs)
    {
        if (!inputs.ExpectedDownloadMbps.HasValue && !inputs.ExpectedUploadMbps.HasValue)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Set your ISP speeds in UniFi Network to grade throughput against your plan."
            }, null, null, null);
        }

        var (tests, stale) = SelectSpeedTests(inputs);
        if (tests.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "No recent WAN speed test. Run one (or enable scheduled WAN tests) to grade throughput against your plan."
            }, null, null, null);
        }

        var down = ScoreDirection(tests.Select(t => t.DownloadMbps), inputs.ExpectedDownloadMbps);
        var up = ScoreDirection(tests.Select(t => t.UploadMbps), inputs.ExpectedUploadMbps);
        var scores = new[] { down?.Score, up?.Score }.Where(s => s.HasValue).Select(s => s!.Value).ToList();
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Expected ISP speeds are configured as zero; cannot grade throughput."
            }, null, null, null);
        }

        var bestDown = down?.BestMbps ?? tests.Max(t => t.DownloadMbps);
        var bestUp = up?.BestMbps ?? tests.Max(t => t.UploadMbps);
        var bestTest = tests.OrderByDescending(t => t.DownloadMbps + t.UploadMbps).First();

        var staleNote = stale ? $" Latest test is older than the {_options.ScoreWindowHours} h window." : "";
        var typicalDown = down?.TypicalMbps ?? bestDown;
        var typicalUp = up?.TypicalMbps ?? bestUp;
        var planText = $"{FormatMbps(inputs.ExpectedDownloadMbps ?? 0)} / {FormatMbps(inputs.ExpectedUploadMbps ?? 0)} Mbps plan";
        var multi = tests.Count > 1;
        var description = multi
            ? $"Fastest of {tests.Count} WAN tests vs your {planText}. Typical {FormatMbps(typicalDown)} / {FormatMbps(typicalUp)} Mbps (down / up).{staleNote}"
            : $"Your latest WAN speed test vs your {planText} (down / up).{staleNote}";
        return (new IspScoreFactor
        {
            Name = "Speed vs Plan",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.SpeedVsPlanWeight,
            ValueText = multi ? $"{FormatMbps(bestDown)} / {FormatMbps(bestUp)} Mbps best" : $"{FormatMbps(bestDown)} / {FormatMbps(bestUp)} Mbps",
            Description = description
        }, new SpeedTestSample(bestTest.Time, bestDown, bestUp), down?.TypicalMbps, up?.TypicalMbps);
    }

    /// <summary>
    /// Outlier-trims one direction's results and blends capacity (best) with typical
    /// delivery (median of the rest). Returns the score plus the best and typical for display.
    /// </summary>
    private (double Score, double BestMbps, double TypicalMbps)? ScoreDirection(IEnumerable<double> resultsMbps, double? expectedMbps)
    {
        if (expectedMbps is not > 0) return null;
        var sorted = resultsMbps.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return null;
        var trim = (int)Math.Floor(sorted.Count * _options.SpeedTestOutlierTrimFraction);
        var kept = sorted.Skip(Math.Min(trim, sorted.Count - 1)).ToList();

        var best = kept[^1];
        var typical = SeriesStats.Median(kept)!.Value;
        var totalWeight = _options.SpeedCapacityWeight + _options.SpeedTypicalWeight;
        var score = (ScoreSpeedRatio(best / expectedMbps.Value) * _options.SpeedCapacityWeight
                     + ScoreSpeedRatio(typical / expectedMbps.Value) * _options.SpeedTypicalWeight) / totalWeight;
        return (score, best, typical);
    }

    private static double ScoreSpeedRatio(double ratio) => ScoreCurve.Interpolate(ratio,
        (0.2, 0), (0.4, 10), (0.6, 40), (0.8, 70), (0.9, 90), (0.95, 100));

    private IspScoreFactor ScoreIdleLatency(double? idleBaseline, AccessProfile profile)
    {
        if (idleBaseline == null)
        {
            return new IspScoreFactor
            {
                Name = "Idle Latency",
                Weight = _options.IdleLatencyWeight,
                Description = "No ISP hop latency data in the window."
            };
        }

        var mid = (profile.IdleRttNormalLowMs + profile.IdleRttNormalHighMs) / 2.0;
        var score = ScoreCurve.Interpolate(idleBaseline.Value,
            (profile.IdleRttIdealMs, 100),
            (profile.IdleRttNormalLowMs, 96),
            (mid, 92),
            (profile.IdleRttNormalHighMs, 85),
            (profile.IdleRttPoorMs, 25),
            (profile.IdleRttPoorMs * 2, 0));

        return new IspScoreFactor
        {
            Name = "Idle Latency",
            Score = (int)Math.Round(score),
            Weight = _options.IdleLatencyWeight,
            ValueText = FormatMs(idleBaseline.Value),
            Description = $"Idle latency to the first ISP hop vs the {FormatMsBand(profile.IdleRttNormalLowMs)} to {FormatMsBand(profile.IdleRttNormalHighMs)} normal band for {profile.DisplayName}."
        };
    }

    private IspScoreFactor ScoreIdleLoss(List<List<LatencySample>> lossPool, AccessProfile profile, double avgLoad)
    {
        // Steady loss is graded on samples OUTSIDE any outage span, so the number reflects
        // true physical-layer loss rather than a discrete internet-down event. Outages are
        // scored separately at the top level (see the outage severity penalty in Score).
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue && !InOutage(s.Time))
            .Select(s => s.LossPercent!.Value)
            .ToList();
        if (losses.Count == 0)
        {
            return new IspScoreFactor
            {
                Name = "Packet Loss",
                Weight = _options.IdleLossWeight,
                Description = "No loss data in the window."
            };
        }

        var meanLoss = losses.Average();
        // Calibrate the acceptable loss ceiling to the average load over the window. An idle
        // line should drop ~nothing; loss only climbs as utilization approaches saturation,
        // so the ceiling rises QUADRATICALLY in load - staying near the idle threshold at low
        // load and reaching the connection's loaded-loss band at LossSaturationLoadFraction
        // (shared-medium access tops out its loss ~75% load, not 100%), holding there above it.
        var t = Math.Clamp(Math.Clamp(avgLoad, 0, 1) / _options.LossSaturationLoadFraction, 0, 1);
        var acceptable = profile.IdleLossAcceptablePct
            + t * t * (profile.LoadedLossDownLowPct - profile.IdleLossAcceptablePct);
        var score = meanLoss <= acceptable
            ? ScoreCurve.Interpolate(meanLoss, (0, 100), (profile.IdleLossIdealPct, 95), (acceptable, 70))
            : ScoreCurve.ExponentialFalloff(meanLoss, acceptable, 70);

        _logger?.LogDebug("ISP Health: packet loss {Loss}% vs load-calibrated ceiling {Ceiling}% ({Load} avg load)",
            meanLoss.ToString("0.###", CultureInfo.InvariantCulture), acceptable.ToString("0.###", CultureInfo.InvariantCulture),
            avgLoad.ToString("0%", CultureInfo.InvariantCulture));

        return new IspScoreFactor
        {
            Name = "Packet Loss",
            Score = (int)Math.Round(score),
            Weight = _options.IdleLossWeight,
            ValueText = FormatPct(meanLoss),
            Description = $"Average loss across ISP, transit, and anycast DNS targets vs the {FormatPct(acceptable)} ceiling for {profile.DisplayName} at {avgLoad.ToString("0%", CultureInfo.InvariantCulture)} average load."
        };
    }

    /// <summary>
    /// Average WAN utilization over the window. Uses the same windowing and per-window
    /// utilization basis as <see cref="LoadClassifier"/> (which drives Loaded Loss): group
    /// rates into LoadWindowSeconds windows, take the busier direction's peak rate in each
    /// as a fraction of the configured plan. Averaged into "average load" here rather than
    /// thresholded into loaded/idle. 0 when there are no expected speeds or no rate data.
    /// </summary>
    private double ComputeAverageLoad(IspHealthInputs inputs)
    {
        var expectedDownBps = inputs.ExpectedDownloadMbps * 1_000_000;
        var expectedUpBps = inputs.ExpectedUploadMbps * 1_000_000;
        if (inputs.WanRates.Count == 0 || (expectedDownBps is null && expectedUpBps is null)) return 0;

        var windowSize = TimeSpan.FromSeconds(_options.LoadWindowSeconds);
        var utils = new List<double>();
        foreach (var group in inputs.WanRates.GroupBy(r => CongestionDetector.FloorTime(r.Time, windowSize)))
        {
            var down = group.Max(r => r.DownloadBps ?? 0);
            var up = group.Max(r => r.UploadBps ?? 0);
            var d = expectedDownBps > 0 ? down / expectedDownBps.Value : 0;
            var u = expectedUpBps > 0 ? up / expectedUpBps.Value : 0;
            utils.Add(Math.Clamp(Math.Max(d, u), 0, 1));
        }
        return utils.Count > 0 ? utils.Average() : 0;
    }

    private (IspScoreFactor Factor, bool HasData) ScoreLoadedLatency(LoadedDeltas deltas, AccessProfile profile)
    {
        var scores = new List<double>();
        if (deltas.DownMs.HasValue) scores.Add(ScoreLoadedDelta(deltas.DownMs.Value, profile));
        if (deltas.UpMs.HasValue) scores.Add(ScoreLoadedDelta(deltas.UpMs.Value, profile));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Latency",
                Weight = _options.LoadedLatencyWeight,
                Description = "No load on the line and no recent WAN speed test with loaded latency measurements."
            }, false);
        }

        // A negative delta means latency did not rise under load (noise/faster); show
        // it as +0 ms rather than a confusing "+-0.1". Always show both directions; a
        // direction with no loaded samples reads "n/a" (distinct from a measured +0 ms).
        var parts = new List<string>
        {
            deltas.DownMs.HasValue ? $"+{FormatLoadedDelta(deltas.DownMs.Value)} down" : "n/a down",
            deltas.UpMs.HasValue ? $"+{FormatLoadedDelta(deltas.UpMs.Value)} up" : "n/a up"
        };
        var valuedDirections = (deltas.DownMs.HasValue ? 1 : 0) + (deltas.UpMs.HasValue ? 1 : 0);
        var speedTestDirections = (deltas.DownMs.HasValue && deltas.DownFromSpeedTest ? 1 : 0)
            + (deltas.UpMs.HasValue && deltas.UpFromSpeedTest ? 1 : 0);
        var source = speedTestDirections == 0 ? ""
            : speedTestDirections == valuedDirections ? " Measured by WAN speed tests."
            : " Partially determined by WAN speed tests.";

        return (new IspScoreFactor
        {
            Name = "Loaded Latency",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLatencyWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Latency increase under load vs +{FormatMsBand(profile.LoadedDeltaExcellentMs)} excellent and +{FormatMsBand(profile.LoadedDeltaAcceptableMs)} acceptable for {profile.DisplayName}.{source}"
        }, true);
    }

    private double ScoreLoadedDelta(double delta, AccessProfile profile)
    {
        var acc = profile.LoadedDeltaAcceptableMs;
        return ScoreCurve.Interpolate(delta,
            (profile.LoadedDeltaExcellentMs, 100),
            (acc, 70),
            (acc * 2, 30),
            (acc * 4, 0));
    }

    /// <summary>
    /// Loaded-latency delta from ISP access hops only. Each access hop's loaded RTT
    /// samples are baseline-subtracted and pooled; the median of the pool (filtered
    /// > 0.5 ms) is the result. Pooling raw samples instead of per-target aggregates
    /// is stable even with sparse loaded data (typical residential). Loaded windows are
    /// dilated (see <see cref="DilateLoadedWindows"/>) so the ramp-in rise and drain tail of
    /// an event - which fall in transition windows outside the strict rate threshold - are
    /// captured rather than dropped.
    /// </summary>
    private double? LoadedLatencyDelta(
        IspHealthInputs inputs,
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        const double noiseFloor = 0.5;
        var loaded = DilateLoadedWindows(loadWindows, directionSelector, oppositeSelector);

        var accessCohort = inputs.AccessHopSeries.Count > 0
            ? inputs.AccessHopSeries
            : new List<List<LatencySample>> { inputs.FirstHopSeries };

        var pooledDeltas = new List<double>();
        foreach (var hop in accessCohort)
        {
            var baseline = ComputeIdleBaseline(hop, loadWindows);
            if (baseline == null) continue;

            var deltas = hop
                .Where(s => s.RttAvgMs.HasValue && loaded.Contains(FloorToWindow(s.Time)))
                .Select(s => s.RttAvgMs!.Value - baseline.Value);

            pooledDeltas.AddRange(deltas);
        }

        var credible = pooledDeltas.Where(d => d >= noiseFloor).ToList();
        if (credible.Count < _options.MinLoadedSamples) return null;
        return Math.Max(0, SeriesStats.Median(credible)!.Value);
    }

    private (IspScoreFactor Factor, bool HasData) ScoreLoadedLoss(
        List<List<LatencySample>> lossPool,
        Dictionary<DateTime, LoadWindow> loadWindows,
        AccessProfile profile)
    {
        if (loadWindows.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Loss",
                Weight = _options.LoadedLossWeight,
                Description = "Loaded loss needs expected ISP speeds and load on the line."
            }, false);
        }

        var downLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
        var upLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);

        var scores = new List<double>();
        if (downLoss.HasValue) scores.Add(ScoreLossBand(downLoss.Value, profile.LoadedLossDownLowPct, profile.LoadedLossDownHighPct));
        if (upLoss.HasValue) scores.Add(ScoreLossBand(upLoss.Value, profile.LoadedLossUpLowPct, profile.LoadedLossUpHighPct));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Loss",
                Weight = _options.LoadedLossWeight,
                Description = "The line was never under sustained load during the window."
            }, false);
        }

        // Always show both directions; a direction with no loaded samples reads "n/a"
        // (distinct from a measured 0%).
        var parts = new List<string>
        {
            downLoss.HasValue ? $"{FormatPct(downLoss.Value)} down" : "n/a down",
            upLoss.HasValue ? $"{FormatPct(upLoss.Value)} up" : "n/a up"
        };

        return (new IspScoreFactor
        {
            Name = "Loaded Loss",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLossWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Packet loss while the line is under load vs the {FormatPct(profile.LoadedLossDownLowPct)} to {FormatPct(profile.LoadedLossDownHighPct)} downstream band for {profile.DisplayName}."
        }, true);
    }

    /// <summary>
    /// Loaded loss degrades on a linear tail rather than the idle-loss exponential:
    /// some loss under full load is expected behavior, so 1.67x the band ceiling
    /// should read "needs work" (~57), not collapse to single digits.
    /// </summary>
    private double ScoreLossBand(double loss, double bandLow, double bandHigh)
    {
        return ScoreCurve.Interpolate(loss,
            (0, 100), (bandLow, 90), (bandHigh, 70),
            (bandHigh * 2, 50), (bandHigh * 3, 32), (bandHigh * 5, 12), (bandHigh * 8, 0));
    }

    private double? LoadedMeanLoss(
        List<List<LatencySample>> lossPool,
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        var loaded = DilateLoadedWindows(loadWindows, directionSelector, oppositeSelector);
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue && !InOutage(s.Time)
                && loaded.Contains(FloorToWindow(s.Time)))
            .Select(s => s.LossPercent!.Value)
            .ToList();
        if (losses.Count < _options.MinLoadedSamples) return null;
        return losses.Average();
    }

    /// <summary>
    /// Window keys that count as loaded in a direction for sample matching: the directly
    /// loaded windows plus up to <see cref="IspHealthOptions.LoadedLeadSeconds"/> before and
    /// <see cref="IspHealthOptions.LoadedTailSeconds"/> after each loaded run. The ramp fills
    /// the queue before throughput crosses the loaded threshold and the drain (plus end-stamped
    /// loss probes) trails it, so without dilation the edges of every event are dropped. Dilation
    /// never crosses into a window loaded in the OPPOSITE direction, so a speed test's download
    /// tail does not bleed into its upload phase. Idle classification is unaffected (this builds a
    /// loaded set only), keeping the baseline a clean uncongested floor.
    /// </summary>
    private HashSet<DateTime> DilateLoadedWindows(
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector,
        Func<LoadWindow, bool> oppositeSelector)
    {
        var leadWindows = (int)Math.Ceiling((double)_options.LoadedLeadSeconds / _options.LoadWindowSeconds);
        var tailWindows = (int)Math.Ceiling((double)_options.LoadedTailSeconds / _options.LoadWindowSeconds);

        var loaded = new HashSet<DateTime>();
        foreach (var (key, w) in loadWindows)
        {
            if (!directionSelector(w)) continue;
            loaded.Add(key);
            for (var i = 1; i <= leadWindows; i++)
            {
                var k = key.AddSeconds(-i * _options.LoadWindowSeconds);
                if (loadWindows.TryGetValue(k, out var nw) && oppositeSelector(nw)) break;
                loaded.Add(k);
            }
            for (var i = 1; i <= tailWindows; i++)
            {
                var k = key.AddSeconds(i * _options.LoadWindowSeconds);
                if (loadWindows.TryGetValue(k, out var nw) && oppositeSelector(nw)) break;
                loaded.Add(k);
            }
        }
        return loaded;
    }

    /// <summary>
    /// Grades one ASN (transit) or one ISP hop: a quality blend (stability, jitter,
    /// loss, congestion) capped by a reach ceiling. Jitter and stability come from
    /// <see cref="AsnSeries.JitterSourceSamples"/> when set (a transit ASN's farther
    /// cluster, to discount false near-hop jitter), otherwise the series itself.
    /// Two reach modes: <paramref name="accessBaselineRtt"/> + <paramref name="internetMedianDeltaMs"/>
    /// is the transit ceiling (distance normalized against the measured internet
    /// context); <paramref name="intraAsnFloorRttMs"/> is the ISP intra-ASN ceiling (a
    /// soft penalty for hops sitting further out than this ISP's nearest hop). Quality
    /// deficits subtract below the ceiling, so congestion and jitter always count.
    /// </summary>
    private IspAsnHealth GradeAsn(
        AsnSeries series,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs,
        double? intraAsnFloorRttMs = null,
        double? jitterOverrideMs = null)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue && !InOutage(s.Time)).Select(s => s.LossPercent!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();

        var medianRtt = SeriesStats.Median(rtts);
        var mad = SeriesStats.Mad(rtts);

        // Jitter and stability are absolve-only across clusters. The nearest cluster's
        // variance can be false (ICMP deprioritization at that hop); a cleaner farther
        // cluster - reached through it - proves the path is steady, so we take the BETTER
        // (lower) of near and far. We never take the worse: a jittery farther cluster is
        // its own problem further along the path and must not downgrade the nearer cluster.
        // An ISP hop instead takes the ISP-wide jitter bound (jitterOverrideMs), which is
        // already capped by the cleanest transit ASN.
        var nearJitter = ScoringJitterOf(series.Samples);
        var rawEffectiveJitter = jitterOverrideMs ?? EffectiveLower(series.Samples, series.JitterSourceSamples, ScoringJitterOf);
        // Don't assimilate on a trivial difference: a witness must sit at least the minimum
        // delta below this series' own reading to pull it down. Within that band it's noise,
        // so keep our own jitter (no absolve, no assimilation flag). Applies to ISP and transit.
        var effectiveJitter = rawEffectiveJitter.HasValue && nearJitter.HasValue
            && rawEffectiveJitter.Value > nearJitter.Value - _options.JitterAssimilationMinDeltaMs
            ? nearJitter
            : rawEffectiveJitter;
        var stabilityRatio = EffectiveLower(series.Samples, series.JitterSourceSamples, StabilityRatioOf);

        // Assimilated when a witness (a farther transit cluster, or - for an ISP hop via
        // the override - a downstream transit/deeper ISP hop) pulled this jitter below the
        // series' own nearest reading.
        var jitterAssimilated = effectiveJitter.HasValue
            && nearJitter.HasValue && effectiveJitter.Value < nearJitter.Value - 0.001;

        if (jitterOverrideMs == null && series.JitterSourceSamples.Count > 0)
        {
            _logger?.LogDebug(
                "ISP Health: AS{Asn} ({Name}) jitter absolve - near {Near} ms, farther cluster {Far} ms, effective {Eff} ms",
                series.AsnNumber, series.AsnName, FormatMsOrNull(ScoringJitterOf(series.Samples)),
                FormatMsOrNull(ScoringJitterOf(series.JitterSourceSamples)), FormatMsOrNull(effectiveJitter));
        }

        int? stabilityScore = stabilityRatio.HasValue
            ? (int)Math.Round(ScoreCurve.Interpolate(stabilityRatio.Value,
                (0.02, 100), (0.10, 80), (0.25, 55), (0.5, 25), (1.0, 0)))
            : null;

        int? jitterScore = effectiveJitter.HasValue
            ? (int)Math.Round(ScoreJitterVsFloor(effectiveJitter.Value, jitterFloorMs))
            : null;

        // Reach ceiling: the best grade this hop's distance allows.
        double? reachDelta = null;
        int? reachCeiling = null;
        if (intraAsnFloorRttMs.HasValue && medianRtt.HasValue)
        {
            // ISP intra-ASN reach: distance from this ISP's nearest hop. A second POP a
            // couple ms out is two sites a real distance apart - nominal, not a fault -
            // so it tops out short of perfect rather than getting dinged hard.
            reachDelta = Math.Max(0, medianRtt.Value - intraAsnFloorRttMs.Value);
            var cIntra = ScoreCurve.Interpolate(reachDelta.Value,
                (0, 100), (1, 93), (2, 85), (4, 70), (8, 50), (16, 35));

            // Item D: lift-only blend toward the internet-relative ceiling. Keeps the intra-ASN
            // distance truth as the floor, but absolves a hop that's modest relative to where the
            // internet actually sits, so a geographically large access network isn't punished for
            // normal in-region distance. Never lowers; partial so genuine distance always shows.
            var ceiling = cIntra;
            if (accessBaselineRtt.HasValue && internetMedianDeltaMs is > 0)
            {
                var netDelta = Math.Max(0, medianRtt.Value - accessBaselineRtt.Value);
                var ratio = netDelta / Math.Max(internetMedianDeltaMs.Value, 2.0);
                var cNet = ScoreCurve.Interpolate(ratio,
                    (0.5, 100), (1.0, 93), (1.5, 90), (2.0, 85), (3.0, 65), (5.0, 40));
                ceiling = cIntra + _options.AccessReachInternetBlendAlpha * Math.Max(0, cNet - cIntra);
            }
            reachCeiling = (int)Math.Round(ceiling);
        }
        else if (accessBaselineRtt.HasValue && medianRtt.HasValue)
        {
            // Transit reach ceiling. The absolute curve applies only top-end gravity
            // (100 needs sub +1 ms; +7-9 ms tops out ~93; far distance alone never grades
            // below the high 80s). The relative curve judges distance against the measured
            // internet context: ratio of this POP's delta to the median internet-target
            // delta. Validated against rural data where a clean 22 ms POP (1.6x internet
            // distance) must stay solid.
            reachDelta = Math.Max(0, medianRtt.Value - accessBaselineRtt.Value);
            var ceiling = ScoreCurve.Interpolate(reachDelta.Value,
                (1, 100), (8, 93), (15, 90), (30, 87), (60, 82));
            if (internetMedianDeltaMs is > 0)
            {
                var ratio = reachDelta.Value / Math.Max(internetMedianDeltaMs.Value, 2.0);
                var relative = ScoreCurve.Interpolate(ratio,
                    (0.5, 100), (1.0, 93), (1.5, 90), (2.0, 85), (3.0, 65), (5.0, 40));
                ceiling = Math.Min(ceiling, relative);
            }
            reachCeiling = (int)Math.Round(ceiling);
        }

        int? lossScore = null;
        if (losses.Count > 0)
        {
            // Forgiving anchors: transit routers often deprioritize ICMP under
            // control-plane policing, so hop loss overstates real forwarding loss
            lossScore = (int)Math.Round(ScoreCurve.Interpolate(losses.Average(),
                (0, 100), (0.1, 95), (0.5, 80), (1, 65), (2, 45), (5, 20), (10, 0)));
        }

        // Attribute congestion to this card by role, not bare ASN: the same ASN can be
        // both the access ISP and a transit provider, so an event is only counted when
        // it fired on one of this role's targets. Events without target info (e.g. unit
        // tests) fall back to ASN matching.
        var roleTargets = series.RoleTargetIds.Count > 0 ? series.RoleTargetIds : series.TargetIds;
        var roleTargetSet = new HashSet<string>(roleTargets);
        // Only confirmed congestion penalizes a network. Self-inflicted bufferbloat,
        // absolved control-plane (ICMP) noise, and unverifiable dead-end elevations are
        // surfaced in the report but never ding the ASN's grade.
        var asnEvents = congestionEvents
            .Where(e => e.Disposition == CongestionDisposition.Confirmed
                && e.AsnNumbers.Contains(series.AsnNumber)
                && (e.TargetIds.Count == 0 || e.TargetIds.Any(t => roleTargetSet.Contains(t))))
            .ToList();
        // Union of the event windows, not the sum - two hops of the same ASN degrading in the
        // same window (e.g. parallel backbone links, or a dead-end hop confirmed by its sibling)
        // are one incident and must not double-count the congestion hours.
        var eventHours = UnionHours(asnEvents);
        var congestionScore = (int)Math.Round(Math.Max(0, 100 - _options.CongestionPenaltyPerHour * eventHours));

        int? overall = null;
        var weighted = new List<(double Score, double Weight)>();
        if (stabilityScore.HasValue) weighted.Add((stabilityScore.Value, _options.AsnLatencyStabilityWeight));
        if (jitterScore.HasValue) weighted.Add((jitterScore.Value, _options.AsnJitterWeight));
        if (lossScore.HasValue) weighted.Add((lossScore.Value, _options.AsnLossWeight));
        weighted.Add((congestionScore, _options.AsnCongestionWeight));
        if (stabilityScore.HasValue || jitterScore.HasValue)
        {
            var totalWeight = weighted.Sum(w => w.Weight);
            var quality = weighted.Sum(w => w.Score * w.Weight) / totalWeight;
            // Quality deficits subtract below the ceiling so congestion, loss, and
            // jitter always move the grade even on distant POPs
            overall = (int)Math.Round(Math.Max(0, (reachCeiling ?? 100) - (100 - quality)));
        }

        return new IspAsnHealth
        {
            AsnNumber = series.AsnNumber,
            AsnName = series.AsnName,
            TargetIds = series.TargetIds,
            MedianRttMs = medianRtt,
            // Displayed RTT: winsorized mean (P99-capped) so sustained elevation shows but a
            // flap can't distort it. Reach (above) stays on the median - that measures distance.
            MeanRttMs = SeriesStats.WinsorizedMean(rtts, _options.RttWinsorPercentile),
            P95RttMs = SeriesStats.Percentile(rtts, 0.95),
            // Raw near-cluster median, informational only. The displayed and scored jitter
            // is the effective (absolve/assimilated) value below.
            MedianJitterMs = jitters.Count > 0 ? SeriesStats.Median(jitters) : null,
            // The effective jitter: absolve-only across clusters (transit) or the ISP-wide
            // bound (ISP). This is what the card shows and what the ISP cap reads, so the
            // displayed value reflects the assimilation rather than the raw near hop.
            P95JitterMs = effectiveJitter,
            RttMadMs = mad,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            ReachDeltaMs = reachDelta,
            LatencyStabilityScore = stabilityScore,
            JitterScore = jitterScore,
            LossScore = lossScore,
            ReachLatencyScore = reachCeiling,
            CongestionScore = congestionScore,
            OverallScore = overall,
            CongestionEventCount = asnEvents.Count,
            JitterAssimilated = jitterAssimilated,
            RawJitterMs = nearJitter
        };
    }

    /// <summary>The path jitter floor: the lowest scoring (P95) jitter across all ISP hops
    /// and transit clusters. Null when no series carries jitter.</summary>
    private double? ComputeJitterFloor(IspHealthInputs inputs)
    {
        var medians = new List<double>();
        void Add(IReadOnlyList<LatencySample> samples)
        {
            var m = ScoringJitterOf(samples);
            if (m.HasValue) medians.Add(m.Value);
        }
        foreach (var s in inputs.IspAsnSeries) Add(s.Samples);
        foreach (var s in inputs.TransitAsnSeries)
        {
            Add(s.Samples);
            if (s.JitterSourceSamples.Count > 0) Add(s.JitterSourceSamples);
        }
        return medians.Count > 0 ? medians.Min() : null;
    }

    /// <summary>
    /// The jitter statistic used for scoring, the ISP/transit cap, and the cards: P95 of
    /// the effective jitter. P95 (not median) because the tail is what the cards show and
    /// what users reason about, and what hurts real-time traffic. The ISP and transit
    /// jitter shown and scored are the same value. Null when none reported jitter.
    /// </summary>
    private static double? ScoringJitterOf(IReadOnlyList<LatencySample> samples)
    {
        var js = samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        return js.Count > 0 ? SeriesStats.Percentile(js, 0.95) : null;
    }

    /// <summary>RTT stability ratio (MAD / median) of a sample set; lower is steadier. Null without RTT.</summary>
    private static double? StabilityRatioOf(IReadOnlyList<LatencySample> samples)
    {
        var rtts = samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var median = SeriesStats.Median(rtts);
        var mad = SeriesStats.Mad(rtts);
        return median is > 0 && mad.HasValue ? mad.Value / median.Value : null;
    }

    /// <summary>
    /// The better (lower) of a metric over the near samples and over the far samples -
    /// absolve-only. A cleaner farther cluster pulls the value down (the near hop's jitter
    /// was false); a worse farther cluster is ignored so it never downgrades the nearer
    /// hop. Far empty means near only.
    /// </summary>
    private static double? EffectiveLower(IReadOnlyList<LatencySample> near, IReadOnlyList<LatencySample> far, Func<IReadOnlyList<LatencySample>, double?> metric)
    {
        var n = metric(near);
        if (far.Count == 0) return n;
        var f = metric(far);
        if (!f.HasValue) return n;
        if (!n.HasValue) return f;
        return Math.Min(n.Value, f.Value);
    }

    /// <summary>
    /// Floor-relative jitter score. A target at the floor is as stable as the line
    /// allows (100). Above it the target is genuinely jittery even if it is only ICMP
    /// deprioritization. Dual-slope: a gentle slope through a dead band just above the
    /// floor (+25-50%), then a steeper drop, so 2x the floor reads as a clear signal.
    /// The high end is absolute - 5+ ms is real jitter no matter how low the floor sits.
    /// </summary>
    private double ScoreJitterVsFloor(double jitterMs, double? floorMs)
    {
        // Item E: when the access technology defines a jitter band, grade straight off it so the
        // medium's inherent jitter (e.g. DOCSIS ~3 ms) reads as normal. The floor is the per-tech
        // ideal - not the measured path floor - so a single quiet sample can't drag the 100-anchor
        // below what the medium really does. Applies to ISP and transit alike (every probe crosses
        // the access medium). Techs with no band (neutral / PPPoE / Other) keep the floor curve.
        if (_profile is { JitterIdealMs: { } ideal, JitterTypicalMs: { } typical, JitterPoorMs: { } poor })
        {
            return ScoreCurve.Interpolate(jitterMs,
                (ideal, 100), (typical, 90), (poor, 25), (2.0 * poor, 0));
        }

        var f = Math.Clamp(floorMs ?? 0.4, _options.JitterFloorMinMs, _options.JitterFloorMaxMs);
        return ScoreCurve.Interpolate(jitterMs,
            (f, 100), (1.25 * f, 96), (1.5 * f, 91), (2.0 * f, 70), (5.0, 22), (12.0, 0));
    }

    /// <summary>
    /// Grades every ISP hop. Each hop's jitter is absolved per-hop, routes-through-gated: a
    /// witness (a transit ASN, another ISP hop, or a monitored destination) may only pull a
    /// hop's jitter down when the hop is in the witness's ancestor set - proven upstream of it
    /// on a shared discovery trace - so a divergent clean transit can never clear a congested
    /// hop it doesn't traverse. When no ancestor data exists (no re-discovery yet) the gate
    /// falls open for transit (transit is always downstream of the ISP) and stays closed for
    /// ISP siblings and destinations. A destination's clean end-to-end jitter is a hard upper
    /// bound on any on-path hop's true jitter, so a smooth path to it absolves an
    /// ICMP-deprioritized hop whose forwarded traffic actually reaches the destination cleanly.
    /// Hops are also scored against the intra-ASN reach floor (distance, not a fault).
    /// </summary>
    private List<IspAsnHealth> GradeIspHops(
        List<AsnSeries> ispHopSeries,
        List<AsnSeries> transitSeries,
        List<IspAsnHealth> transitAsns,
        List<AsnSeries> destinationSeries,
        List<CongestionEvent> congestionEvents,
        double? jitterFloorMs,
        bool hopOrderKnown,
        double? accessBaselineRtt,
        double? internetMedianDeltaMs)
    {
        // Transit witnesses: each transit ASN's ancestor IPs + its effective jitter.
        var transitJitterByAsn = transitAsns
            .Where(a => a.P95JitterMs.HasValue)
            .GroupBy(a => a.AsnNumber)
            .ToDictionary(g => g.Key, g => g.Min(a => a.P95JitterMs!.Value));
        var transitWitnesses = transitSeries
            .Where(s => transitJitterByAsn.ContainsKey(s.AsnNumber))
            .Select(s => (Ancestors: s.AncestorIps, Jitter: transitJitterByAsn[s.AsnNumber]))
            .ToList();

        // Destination witnesses: each monitored endpoint's ancestor IPs + its end-to-end
        // jitter. Always strict (routes-through required) - a destination's clean path says
        // nothing about a hop it doesn't cross, so it never absolves on faith. Only built when
        // ancestry exists; without it (hopOrderKnown false) destinations can never absolve, so
        // we skip computing their jitter entirely.
        var destinationWitnesses = hopOrderKnown
            ? destinationSeries
                .Select(s => (s.AncestorIps, Jitter: ScoringJitterOf(s.Samples)))
                .Where(w => w.Jitter.HasValue)
                .Select(w => (w.AncestorIps, Jitter: w.Jitter!.Value))
                .ToList()
            : new List<(List<string> AncestorIps, double Jitter)>();

        // ISP hop witnesses: each hop series + its own measured jitter.
        var ispHopJitter = ispHopSeries
            .Select(s => (Series: s, Jitter: ScoringJitterOf(s.Samples)))
            .ToList();

        var grades = new List<IspAsnHealth>();
        foreach (var asnGroup in ispHopSeries.GroupBy(s => s.AsnNumber))
        {
            var hops = asnGroup.ToList();
            var floorRtt = hops
                .Select(s => SeriesStats.Median(s.Samples.Where(x => x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList()))
                .Where(m => m.HasValue)
                .Select(m => m!.Value)
                .DefaultIfEmpty()
                .Min();
            double? intraFloor = hops.Any(s => s.Samples.Any(x => x.RttAvgMs.HasValue)) ? floorRtt : null;
            foreach (var hop in hops)
            {
                var measured = ScoringJitterOf(hop.Samples);
                // Transit is always downstream of the ISP: with ancestor data we require a
                // proven routes-through (this hop is in the transit's ancestor set), without
                // it the gate is open. ISP siblings are strict either way - a sibling absolves
                // only a hop in its ancestor set, never on faith.
                var witnesses = transitWitnesses
                    .Where(w => !hopOrderKnown || RoutesThrough(w.Ancestors, hop.HopIps))
                    .Select(w => w.Jitter)
                    .Concat(ispHopJitter
                        .Where(h => hopOrderKnown && !ReferenceEquals(h.Series, hop) && h.Jitter.HasValue
                            && RoutesThrough(h.Series.AncestorIps, hop.HopIps))
                        .Select(h => h.Jitter!.Value))
                    .Concat(destinationWitnesses
                        .Where(w => hopOrderKnown && RoutesThrough(w.AncestorIps, hop.HopIps))
                        .Select(w => w.Jitter))
                    .ToList();
                double? effective = measured;
                if (witnesses.Count > 0)
                    effective = measured.HasValue ? Math.Min(measured.Value, witnesses.Min()) : witnesses.Min();

                var grade = GradeAsn(hop, congestionEvents, jitterFloorMs, accessBaselineRtt, internetMedianDeltaMs,
                    intraAsnFloorRttMs: intraFloor, jitterOverrideMs: effective);
                // Log the graded effective (post sub-0.05 ms assimilation snap in GradeAsn),
                // not the raw witness min, so the log matches what the hop is actually scored on.
                _logger?.LogDebug(
                    "ISP Health: ISP hop {Target} (AS{Asn}) graded {Score} - measured jitter {Jitter} ms, effective {Eff} ms ({Witnesses} routes-through witnesses), reach +{Reach} ms",
                    hop.TargetIds.FirstOrDefault(), hop.AsnNumber, grade.OverallScore,
                    FormatMsOrNull(measured), FormatMsOrNull(grade.P95JitterMs), witnesses.Count, FormatMsOrNull(grade.ReachDeltaMs));
                grades.Add(grade);
            }
        }
        return grades;
    }

    /// <summary>
    /// Whether a witness routes through a hop (and so may absolve it): the hop's IP must be in
    /// the witness's ancestor set - proven upstream of the witness on a shared discovery trace.
    /// </summary>
    private static bool RoutesThrough(List<string> witnessAncestors, List<string> hopIps) =>
        hopIps.Any(ip => witnessAncestors.Contains(ip, StringComparer.OrdinalIgnoreCase));

    /// <summary>Total hours covered by the union of the events' time windows (overlaps counted once).</summary>
    private static double UnionHours(IReadOnlyList<CongestionEvent> events)
    {
        double total = 0;
        DateTime curStart = default, curEnd = default;
        var open = false;
        foreach (var e in events.OrderBy(e => e.Start))
        {
            if (!open) { curStart = e.Start; curEnd = e.End; open = true; }
            else if (e.Start > curEnd) { total += (curEnd - curStart).TotalHours; curStart = e.Start; curEnd = e.End; }
            else if (e.End > curEnd) curEnd = e.End;
        }
        if (open) total += (curEnd - curStart).TotalHours;
        return total;
    }

    /// <summary>
    /// Collapses per-hop ISP grades to one entry per ASN for the Networks on Your Path
    /// card: mean RTT and jitter across the hops, averaged grade, and the union of the
    /// ASN's congestion events.
    /// </summary>
    private static List<IspAsnHealth> AggregateIspAsns(List<IspAsnHealth> hopGrades, List<CongestionEvent> congestionEvents, double assimilationMinDeltaMs)
    {
        var result = new List<IspAsnHealth>();
        foreach (var group in hopGrades.GroupBy(h => h.AsnNumber))
        {
            var hops = group.ToList();
            var targetIds = hops.SelectMany(h => h.TargetIds).Distinct().ToList();
            var targetSet = new HashSet<string>(targetIds);
            var asnEvents = congestionEvents
                .Where(e => e.Disposition == CongestionDisposition.Confirmed
                    && e.AsnNumbers.Contains(group.Key)
                    && (e.TargetIds.Count == 0 || e.TargetIds.Any(t => targetSet.Contains(t))))
                .ToList();
            var means = hops.Select(h => h.MeanRttMs).Where(m => m.HasValue).Select(m => m!.Value).ToList();
            // Each hop's P95JitterMs is its per-hop effective (absolved) jitter; RawJitterMs
            // is its own measured reading. The card shows the mean effective and flags
            // assimilation when that fell below the mean measured.
            var effJitters = hops.Select(h => h.P95JitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            var rawJitters = hops.Select(h => h.RawJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
            double? effMean = effJitters.Count > 0 ? effJitters.Average() : null;
            double? rawMean = rawJitters.Count > 0 ? rawJitters.Average() : null;
            var lossVals = hops.Select(h => h.LossPct).Where(l => l.HasValue).Select(l => l!.Value).ToList();
            var medianRtts = hops.Select(h => h.MedianRttMs).Where(m => m.HasValue).Select(m => m!.Value).ToList();
            var scored = hops.Where(h => h.OverallScore.HasValue).Select(h => h.OverallScore!.Value).ToList();
            result.Add(new IspAsnHealth
            {
                AsnNumber = group.Key,
                AsnName = hops.Select(h => h.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                TargetIds = targetIds,
                MedianRttMs = medianRtts.Count > 0 ? medianRtts.Min() : null,
                MeanRttMs = means.Count > 0 ? means.Average() : null,
                // RTT range across the ISP hops, on the same winsorized mean the hops display.
                MinRttMs = means.Count > 0 ? means.Min() : null,
                MaxRttMs = means.Count > 0 ? means.Max() : null,
                P95JitterMs = effMean,
                LossPct = lossVals.Count > 0 ? lossVals.Average() : null,
                OverallScore = scored.Count > 0 ? (int)Math.Round(scored.Average()) : null,
                CongestionEventCount = asnEvents.Count,
                JitterAssimilated = effMean.HasValue && rawMean.HasValue && effMean.Value < rawMean.Value - assimilationMinDeltaMs,
                RawJitterMs = rawMean
            });
        }
        return result;
    }

    private IspTargetHealth BuildIspTargetHealth(AsnSeries series, string? firstHopTargetId, List<IspAsnHealth> hopGrades, double winsorPercentile)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue && !InOutage(s.Time)).Select(s => s.LossPercent!.Value).ToList();
        var targetId = series.TargetIds.FirstOrDefault() ?? "";
        var grade = hopGrades.FirstOrDefault(g => g.TargetIds.Contains(targetId));
        // Jitter comes from the grade (the effective/absolved value the hop is scored on), so
        // the row matches the grade beside it. Fall back to the hop's own raw P95 when ungraded.
        var rawP95 = jitters.Count > 0 ? SeriesStats.Percentile(jitters, 0.95) : null;
        return new IspTargetHealth
        {
            TargetId = targetId,
            Name = series.AsnName ?? targetId,
            RttMs = SeriesStats.WinsorizedMean(rtts, winsorPercentile),
            P95JitterMs = grade?.P95JitterMs ?? rawP95,
            RawJitterMs = grade?.RawJitterMs ?? rawP95,
            JitterAssimilated = grade?.JitterAssimilated ?? false,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            OverallScore = grade?.OverallScore,
            ReachDeltaMs = grade?.ReachDeltaMs,
            IsGradedHop = targetId == firstHopTargetId
        };
    }

    /// <summary>The ISP Network dimension: averages every ISP hop grade. The per-hop
    /// detail is rendered from <see cref="IspHealthReport.IspTargets"/>, so the dimension
    /// itself carries no factors.</summary>
    private IspScoreDimension BuildIspDimension(double weight, List<IspAsnHealth> hopGrades)
    {
        var scored = hopGrades.Where(h => h.OverallScore.HasValue).Select(h => h.OverallScore!.Value).ToList();
        int? score = scored.Count > 0 ? (int)Math.Round(scored.Average()) : null;
        return new IspScoreDimension { Name = "ISP Network", Score = score, Weight = weight, Factors = new List<IspScoreFactor>() };
    }

    private static IspScoreDimension BuildDimension(string name, double weight, List<IspScoreFactor> factors)
    {
        var scored = factors.Where(f => f.Score.HasValue).ToList();
        int? score = null;
        if (scored.Count > 0)
        {
            var totalWeight = scored.Sum(f => f.Weight);
            score = (int)Math.Round(scored.Sum(f => f.Score!.Value * f.Weight) / totalWeight);
        }
        return new IspScoreDimension { Name = name, Score = score, Weight = weight, Factors = factors };
    }

    private static IspScoreDimension BuildAsnDimension(string name, double weight, List<IspAsnHealth> asns)
    {
        var factors = asns.Select(a => new IspScoreFactor
        {
            Name = string.IsNullOrEmpty(a.AsnName) ? $"AS{a.AsnNumber}" : a.AsnName,
            Score = a.OverallScore,
            Weight = 1.0,
            ValueText = a.MeanRttMs.HasValue ? FormatMsCoarse(a.MeanRttMs.Value) : null,
            Description = a.CongestionEventCount > 0
                ? $"{a.CongestionEventCount} congestion event{(a.CongestionEventCount == 1 ? "" : "s")} in the window."
                : null
        }).ToList();

        var scored = asns.Where(a => a.OverallScore.HasValue).ToList();
        int? score = scored.Count > 0 ? (int)Math.Round(scored.Average(a => a.OverallScore!.Value)) : null;
        return new IspScoreDimension { Name = name, Score = score, Weight = weight, Factors = factors };
    }

    private int CombineDimensions(params IspScoreDimension[] dimensions)
    {
        var scored = dimensions.Where(d => d.Score.HasValue).ToList();
        if (scored.Count == 0) return 0;
        var totalWeight = scored.Sum(d => d.Weight);
        return (int)Math.Round(scored.Sum(d => d.Score!.Value * d.Weight) / totalWeight);
    }

    private List<IspHealthIssue> CollectIssues(
        IspHealthInputs inputs,
        AccessProfile profile,
        IspHealthReport report,
        Dictionary<DateTime, LoadWindow> loadWindows,
        LoadedDeltas loadedDeltas)
    {
        var issues = new List<IspHealthIssue>();

        // Local (LAN/gateway) outages are surfaced in the waterfall but are not internet outages and
        // don't affect the score, so they never appear in this ISP-impact issue.
        var wanOutages = inputs.Outages.Where(o => o.Scope != OutageScope.Local).ToList();
        if (wanOutages.Count > 0)
        {
            var multiple = wanOutages.Count > 1;
            var totalDown = TimeSpan.FromMinutes(wanOutages.Sum(o => o.Duration.TotalMinutes));
            var upstream = wanOutages.Where(o => o.Scope == OutageScope.Upstream && !string.IsNullOrEmpty(o.LastReachableHop)).ToList();
            var allUpstream = wanOutages.All(o => o.Scope == OutageScope.Upstream) && upstream.Count > 0;
            var where = allUpstream
                ? $" The break sat upstream of {string.Join(", ", upstream.Select(o => o.LastReachableHop).Distinct())} - your equipment stayed reachable, so {(multiple ? "these were" : "this was")} an ISP-side fault, not your network."
                : " At least one event took the whole WAN dark, including the first ISP hop.";
            var count = multiple
                ? $"{wanOutages.Count} internet outages totaling {FormatOutageDuration(totalDown)}"
                : $"An internet outage of {FormatOutageDuration(wanOutages[0].Duration)}";
            // Be transparent about the score hit: the outage penalty is applied at the top level
            // and isn't tied to any one factor, so spell it out here or it's invisible. Matches the
            // actual penalty (both exclude Local outages).
            var penalty = (int)Math.Round(OutageScorePenalty(totalDown.TotalMinutes));
            var impact = penalty > 0
                ? $" {(multiple ? "Together they" : "It")} lowered your ISP Health score by {penalty} {(penalty == 1 ? "point" : "points")}."
                : string.Empty;
            var realPhrase = multiple
                ? "so these are real outages, not monitoring gaps"
                : "so this is a real outage, not a monitoring gap";
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = multiple ? "Internet outages in the window" : "Internet outage in the window",
                Description = $"{count} occurred while the Monitoring Agent kept probing ({realPhrase}).{where}{impact}",
                Recommendation = allUpstream
                    ? "No action needed on your side for an upstream outage; it is logged here so you can correlate it with ISP incidents."
                    : "Logged here so you can correlate it with ISP incidents; if the first ISP hop keeps dropping, check your modem/ONT and the line to your ISP.",
                LinkUrl = "#isp-outages",
                LinkText = "The recovery shape is shown on the timeline below."
            });
        }

        if (!report.HasExpectedSpeeds)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Expected ISP speeds not set",
                Description = "Loaded-line analysis is skipped because no ISP speeds are configured.",
                Recommendation = "Set your ISP download and upload speeds in UniFi Network (Settings, Internet, your WAN) so ISP Health can grade behavior under load."
            });
        }

        var (latencyTriggered, lossTriggered) = SqmTriggers(inputs, profile, loadWindows, loadedDeltas);
        if (latencyTriggered || lossTriggered)
        {
            var recommendation = inputs.SmartQueuesEnabled
                ? "Smart Queues is enabled on this WAN but the line still degrades under load; check that its configured rates match what the line actually delivers."
                : "Enable Smart Queues (SQM) on this WAN in UniFi Network (Settings, Internet, your WAN, Smart Queues).";
            if (inputs.CongestionEvents.Count(e => e.Disposition == CongestionDisposition.Confirmed) >= _options.SqmRecurringCongestionEvents)
            {
                recommendation += " This connection also shows a recurring congestion pattern; consider Adaptive SQM, which tracks time-of-day capacity changes automatically.";
            }
            if (latencyTriggered)
            {
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Bufferbloat under load",
                    Description = "Latency rises well beyond the excellent range for this connection type when the line is loaded.",
                    Recommendation = recommendation,
                    LinkUrl = "/sqm",
                    LinkText = "Adaptive SQM"
                });
            }
            if (lossTriggered)
            {
                issues.Add(new IspHealthIssue
                {
                    Severity = IspIssueSeverity.Warning,
                    Title = "Packet loss under load",
                    Description = "Packet loss exceeds the acceptable band for this connection type when the line is loaded.",
                    Recommendation = recommendation,
                    LinkUrl = "/sqm",
                    LinkText = "Adaptive SQM"
                });
            }
        }

        var speedFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Speed vs Plan");
        if (speedFactor?.Score is < 70)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Throughput below plan",
                Description = $"The best WAN speed test ({speedFactor.ValueText}) falls well short of the {FormatMbps(inputs.ExpectedDownloadMbps ?? 0)} / {FormatMbps(inputs.ExpectedUploadMbps ?? 0)} Mbps plan configured in UniFi Network.",
                Recommendation = "If the configured plan speeds are right, raise the shortfall with your ISP. If the plan changed, update the ISP speeds in UniFi Network so grading stays accurate."
            });
        }

        var idleLatencyFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Idle Latency");
        if (idleLatencyFactor?.Score is < 75)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Idle latency above normal",
                Description = $"Baseline first-hop latency of {idleLatencyFactor.ValueText} is above the normal range for {profile.DisplayName}.",
                Recommendation = "Common causes: access layer congestion or overprovisioning by the ISP, CPE inefficiency (try a reboot or firmware update), or a longer-than-expected physical haul to the first hop."
            });
        }

        var idleLossFactor = report.AccessDimension.Factors.FirstOrDefault(f => f.Name == "Packet Loss");
        if (idleLossFactor?.Score is < 70)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Packet loss above acceptable",
                Description = $"Average packet loss of {idleLossFactor.ValueText} exceeds the {FormatPct(profile.IdleLossAcceptablePct)} acceptable ceiling for {profile.DisplayName}.",
                Recommendation = "Persistent loss regardless of load usually points at the physical layer: check optics, connectors, coax fittings, or signal levels, and raise it with your ISP."
            });
        }

        var sharedEvents = inputs.CongestionEvents.Where(e => e.IsShared).ToList();
        if (sharedEvents.Count > 0)
        {
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Info,
                Title = "Shared upstream congestion",
                Description = $"{sharedEvents.Count} congestion event{(sharedEvents.Count == 1 ? "" : "s")} hit multiple networks at once, which usually means a shared upstream or return path is the bottleneck rather than the individual networks shown."
            });
        }

        return issues;
    }

    private (bool Latency, bool Loss) SqmTriggers(
        IspHealthInputs inputs,
        AccessProfile profile,
        Dictionary<DateTime, LoadWindow> loadWindows,
        LoadedDeltas loadedDeltas)
    {
        var bandWidth = profile.LoadedDeltaAcceptableMs - profile.LoadedDeltaExcellentMs;
        var deltaThreshold = profile.LoadedDeltaExcellentMs + _options.SqmDeviationFactor * bandWidth;
        var latency = loadedDeltas.DownMs > deltaThreshold || loadedDeltas.UpMs > deltaThreshold;

        var loss = false;
        if (loadWindows.Count > 0)
        {
            var downLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedDown, w => w.IsLoadedUp);
            var upLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedUp, w => w.IsLoadedDown);
            loss = downLoss > profile.LoadedLossDownHighPct || upLoss > profile.LoadedLossUpHighPct;
        }
        return (latency, loss);
    }

    private DateTime FloorToWindow(DateTime time) =>
        CongestionDetector.FloorTime(time, TimeSpan.FromSeconds(_options.LoadWindowSeconds));

    /// <summary>
    /// The overall-score deduction for outages of the given total downtime, interpolated on the
    /// configured severity curve. Applied at the top level (not inside a factor) so a long
    /// outage isn't diluted by the dimension weights; scored by duration alone, shape-independent.
    /// </summary>
    private double OutageScorePenalty(double totalDowntimeMinutes) =>
        ScoreCurve.Interpolate(totalDowntimeMinutes, _options.OutageSeverityCurve);

    private static string FormatOutageDuration(TimeSpan d) =>
        d.TotalMinutes < 90 ? $"{d.TotalMinutes:0} min" : $"{d.TotalHours:0.#} h";

    private static string FormatMs(double ms) =>
        $"{ms.ToString("0.00", CultureInfo.InvariantCulture)} ms";

    /// <summary>Coarse RTT for dimension summaries: no decimals at or above 10 ms, one below.
    /// Detail lives on the Networks on Your Path cards.</summary>
    private static string FormatMsCoarse(double ms) =>
        ms >= 10 ? $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms" : $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Band references and loaded deltas: one decimal (2.0 ms), not the value's two.</summary>
    private static string FormatMsBand(double ms) =>
        $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Debug-log helper: a millisecond value to two decimals, or "n/a" when null.</summary>
    private static string FormatMsOrNull(double? ms) =>
        ms.HasValue ? ms.Value.ToString("0.00", CultureInfo.InvariantCulture) : "n/a";

    /// <summary>Loaded-latency delta for display: a non-positive delta shows as "0 ms".</summary>
    private static string FormatLoadedDelta(double ms) => ms <= 0 ? "0 ms" : FormatMsBand(ms);

    private static string FormatPct(double pct) =>
        pct == 0 ? "0%" : $"{pct.ToString(pct < 0.1 ? "0.###" : "0.##", CultureInfo.InvariantCulture)}%";

    private static string FormatMbps(double mbps) =>
        mbps.ToString(mbps >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
