using System.Globalization;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Pure scoring engine for ISP Health. Takes pre-assembled inputs (latency series,
/// throughput, detected events) plus an access technology profile and produces the
/// full report. No I/O; fully unit-testable. Formulas and anchor points are
/// documented in research/isp-health-spec.md (local-only) and must stay in sync.
/// </summary>
public class IspHealthScorer
{
    private readonly IspHealthOptions _options;

    public IspHealthScorer(IspHealthOptions options)
    {
        _options = options;
    }

    public IspHealthReport Score(IspHealthInputs inputs, AccessProfile profile)
    {
        var loadWindows = LoadClassifier.Classify(inputs.WanRates, inputs.ExpectedDownloadMbps, inputs.ExpectedUploadMbps, _options);
        var hasExpectedSpeeds = inputs.ExpectedDownloadMbps.HasValue || inputs.ExpectedUploadMbps.HasValue;

        var idleBaseline = ComputeIdleBaseline(inputs.FirstHopSeries, loadWindows);
        var (speedVsPlan, bestSpeedTest, typicalDownMbps, typicalUpMbps) = ScoreSpeedVsPlan(inputs);
        var idleLatency = ScoreIdleLatency(idleBaseline, profile);
        var idleLoss = ScoreIdleLoss(inputs.LossPoolSeries, profile);
        var loadedDeltas = ResolveLoadedDeltas(inputs, loadWindows);
        var (loadedLatency, hasLoadedLatency) = ScoreLoadedLatency(loadedDeltas, profile);
        var (loadedLoss, hasLoadedLoss) = ScoreLoadedLoss(inputs.LossPoolSeries, loadWindows, profile);

        var accessFactors = new List<IspScoreFactor> { speedVsPlan, idleLatency, idleLoss, loadedLatency, loadedLoss };
        var accessDimension = BuildDimension("Access Layer", _options.AccessWeight, accessFactors);

        var accessMedianRtt = SeriesStats.Median(
            inputs.FirstHopSeries.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList());
        var transitAsns = inputs.TransitAsnSeries.Select(s => GradeAsn(s, inputs.CongestionEvents, accessMedianRtt, inputs.InternetMedianDeltaMs)).ToList();
        var ispAsns = inputs.IspAsnSeries.Select(s => GradeAsn(s, inputs.CongestionEvents, accessBaselineRtt: null, internetMedianDeltaMs: null)).ToList();
        var transitDimension = BuildAsnDimension("Transit Health", _options.TransitWeight, transitAsns);
        var ispAsnDimension = BuildAsnDimension("ISP Network", _options.IspAsnWeight, ispAsns, gradedOnBestHop: true);

        var overall = CombineDimensions(accessDimension, transitDimension, ispAsnDimension);

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
            IspTargets = inputs.IspTargetSeries.Select(s => BuildIspTargetHealth(s, inputs.FirstHopTargetId)).ToList(),
            CongestionEvents = inputs.CongestionEvents,
            PathShifts = inputs.PathShifts,
            HasExpectedSpeeds = hasExpectedSpeeds,
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
    internal record LoadedDeltas(double? DownMs, double? UpMs, bool FromSpeedTests);

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
            // Worst loaded delta across the public access hops (each vs its own idle
            // baseline). Access congestion can land on any access hop, not just the
            // nearest, and probe timing means a given hop may miss a brief spike - so the
            // worst hop carries the signal. Falls back to the first hop when no per-hop
            // set was supplied (e.g. unit tests).
            var hops = inputs.AccessHopSeries.Count > 0
                ? inputs.AccessHopSeries
                : new List<List<LatencySample>> { inputs.FirstHopSeries };
            down = WorstLoadedDelta(hops, loadWindows, w => w.IsLoadedDown);
            up = WorstLoadedDelta(hops, loadWindows, w => w.IsLoadedUp);
        }

        var fromSpeedTests = false;
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
                fromSpeedTests = true;
            }
            if (up == null && upDeltas.Count > 0)
            {
                up = SeriesStats.Median(upDeltas);
                fromSpeedTests = true;
            }
        }
        return new LoadedDeltas(down, up, fromSpeedTests);
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
        if (idleRtts.Count > 0) return SeriesStats.Median(idleRtts);

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
            (profile.IdleRttNormalLowMs, 92),
            (mid, 84),
            (profile.IdleRttNormalHighMs, 75),
            (profile.IdleRttPoorMs, 25),
            (profile.IdleRttPoorMs * 2, 0));

        return new IspScoreFactor
        {
            Name = "Idle Latency",
            Score = (int)Math.Round(score),
            Weight = _options.IdleLatencyWeight,
            ValueText = FormatMs(idleBaseline.Value),
            Description = $"Idle latency to the first ISP hop vs the {FormatMs(profile.IdleRttNormalLowMs)} to {FormatMs(profile.IdleRttNormalHighMs)} normal band for {profile.DisplayName}."
        };
    }

    private IspScoreFactor ScoreIdleLoss(List<List<LatencySample>> lossPool, AccessProfile profile)
    {
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue)
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
        var score = meanLoss <= profile.IdleLossAcceptablePct
            ? ScoreCurve.Interpolate(meanLoss, (0, 100), (profile.IdleLossIdealPct, 95), (profile.IdleLossAcceptablePct, 70))
            : ScoreCurve.ExponentialFalloff(meanLoss, profile.IdleLossAcceptablePct, 70);

        return new IspScoreFactor
        {
            Name = "Packet Loss",
            Score = (int)Math.Round(score),
            Weight = _options.IdleLossWeight,
            ValueText = FormatPct(meanLoss),
            Description = $"Average loss across ISP, transit, and anycast DNS targets vs the {FormatPct(profile.IdleLossAcceptablePct)} acceptable ceiling for {profile.DisplayName}."
        };
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
        // it as +0 ms rather than a confusing "+-0.1".
        var parts = new List<string>();
        if (deltas.DownMs.HasValue) parts.Add($"+{FormatLoadedDelta(deltas.DownMs.Value)} down");
        if (deltas.UpMs.HasValue) parts.Add($"+{FormatLoadedDelta(deltas.UpMs.Value)} up");
        var source = deltas.FromSpeedTests ? " Measured by WAN speed tests." : "";

        return (new IspScoreFactor
        {
            Name = "Loaded Latency",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLatencyWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Latency increase under load vs +{FormatMs(profile.LoadedDeltaExcellentMs)} excellent and +{FormatMs(profile.LoadedDeltaAcceptableMs)} acceptable for {profile.DisplayName}.{source}"
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
    /// The worst (largest) loaded delta across the supplied access hops. Each hop is
    /// measured against its own idle baseline, so a hop with a higher idle RTT is not
    /// penalised; the maximum is taken because the access link is shared and any hop that
    /// captured the under-load spike is reporting the real signal.
    /// </summary>
    private double? WorstLoadedDelta(
        IReadOnlyList<IReadOnlyList<LatencySample>> hops,
        Dictionary<DateTime, LoadWindow> loadWindows,
        Func<LoadWindow, bool> directionSelector)
    {
        double? worst = null;
        foreach (var hop in hops)
        {
            var baseline = ComputeIdleBaseline(hop, loadWindows);
            if (baseline == null) continue;
            var delta = LoadedDelta(hop, loadWindows, baseline.Value, directionSelector);
            if (delta == null) continue;
            if (worst == null || delta.Value > worst.Value) worst = delta.Value;
        }
        return worst;
    }

    private double? LoadedDelta(
        IReadOnlyList<LatencySample> hop,
        Dictionary<DateTime, LoadWindow> loadWindows,
        double idleBaseline,
        Func<LoadWindow, bool> directionSelector)
    {
        var rtts = hop
            .Where(s => s.RttAvgMs.HasValue
                && loadWindows.TryGetValue(FloorToWindow(s.Time), out var w)
                && directionSelector(w))
            .Select(s => s.RttAvgMs!.Value)
            .ToList();
        if (rtts.Count < _options.MinLoadedSamples) return null;
        // p95 (worst-case under load), not median: brief bufferbloat/congestion spikes are
        // exactly what loaded latency must catch, and a median over a long loaded period
        // washes them out.
        return SeriesStats.Percentile(rtts, 0.95)!.Value - idleBaseline;
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

        var downLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedDown);
        var upLoss = LoadedMeanLoss(lossPool, loadWindows, w => w.IsLoadedUp);

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

        var parts = new List<string>();
        if (downLoss.HasValue) parts.Add($"{FormatPct(downLoss.Value)} down");
        if (upLoss.HasValue) parts.Add($"{FormatPct(upLoss.Value)} up");

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
        Func<LoadWindow, bool> directionSelector)
    {
        var losses = lossPool.SelectMany(series => series)
            .Where(s => s.LossPercent.HasValue
                && loadWindows.TryGetValue(FloorToWindow(s.Time), out var w)
                && directionSelector(w))
            .Select(s => s.LossPercent!.Value)
            .ToList();
        if (losses.Count < _options.MinLoadedSamples) return null;
        return losses.Average();
    }

    /// <summary>
    /// Grades one ASN: a quality blend (stability, jitter, loss, congestion) capped
    /// by a reach ceiling for transit ASNs. The ceiling normalizes distance against
    /// the measured internet-target delta so rural networks are judged by rural
    /// geography (a 22 ms POP when the internet sits 14 ms out is solid) while a
    /// metro POP far beyond a 2 ms internet context grades poorly. Quality deficits
    /// subtract below the ceiling, so congestion always counts. ISP ASNs pass null
    /// baselines: no ceiling, quality only.
    /// </summary>
    private IspAsnHealth GradeAsn(AsnSeries series, List<CongestionEvent> congestionEvents, double? accessBaselineRtt, double? internetMedianDeltaMs)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue).Select(s => s.LossPercent!.Value).ToList();

        var medianRtt = SeriesStats.Median(rtts);
        var mad = SeriesStats.Mad(rtts);
        var medianJitter = jitters.Count > 0 ? SeriesStats.Median(jitters) : null;

        int? stabilityScore = null;
        if (medianRtt is > 0 && mad.HasValue)
        {
            var ratio = mad.Value / medianRtt.Value;
            stabilityScore = (int)Math.Round(ScoreCurve.Interpolate(ratio,
                (0.02, 100), (0.10, 80), (0.25, 55), (0.5, 25), (1.0, 0)));
        }

        int? jitterScore = null;
        if (medianJitter.HasValue && medianRtt is > 0)
        {
            // Anchors tightened ~20% so meaningful jitter costs a touch more
            var relative = ScoreCurve.Interpolate(medianJitter.Value / medianRtt.Value,
                (0.04, 100), (0.12, 75), (0.25, 45), (0.50, 0));
            var absolute = ScoreCurve.Interpolate(medianJitter.Value,
                (0.4, 100), (1.5, 75), (4, 45), (12, 0));
            jitterScore = (int)Math.Round(Math.Max(relative, absolute));
        }

        // Reach ceiling: the best grade this ASN's distance allows. The absolute
        // curve applies only top-end gravity (100 needs sub +1 ms; +7-9 ms tops out
        // ~93; far distance alone never grades below the high 80s). The relative
        // curve judges distance against the measured internet context: ratio of this
        // POP's delta to the median internet-target delta. Validated against rural
        // data where a clean 22 ms POP (1.6x internet distance) must stay solid.
        double? reachDelta = null;
        int? reachCeiling = null;
        if (accessBaselineRtt.HasValue && medianRtt.HasValue)
        {
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
        var asnEvents = congestionEvents
            .Where(e => e.AsnNumbers.Contains(series.AsnNumber)
                && (e.TargetIds.Count == 0 || e.TargetIds.Any(t => roleTargetSet.Contains(t))))
            .ToList();
        var eventHours = asnEvents.Sum(e => e.Duration.TotalHours);
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
            // Display: mean across the full nearest cluster (set on the grading series);
            // falls back to the graded samples' mean when not provided
            MeanRttMs = series.NearestClusterMeanRttMs ?? (rtts.Count > 0 ? rtts.Average() : null),
            P95RttMs = SeriesStats.Percentile(rtts, 0.95),
            MedianJitterMs = medianJitter,
            P95JitterMs = jitters.Count > 0 ? SeriesStats.Percentile(jitters, 0.95) : null,
            RttMadMs = mad,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            ReachDeltaMs = reachDelta,
            LatencyStabilityScore = stabilityScore,
            JitterScore = jitterScore,
            LossScore = lossScore,
            ReachLatencyScore = reachCeiling,
            CongestionScore = congestionScore,
            OverallScore = overall,
            CongestionEventCount = asnEvents.Count
        };
    }

    private static IspTargetHealth BuildIspTargetHealth(AsnSeries series, string? firstHopTargetId)
    {
        var rtts = series.Samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
        var jitters = series.Samples.Select(s => s.EffectiveJitterMs).Where(j => j.HasValue).Select(j => j!.Value).ToList();
        var losses = series.Samples.Where(s => s.LossPercent.HasValue).Select(s => s.LossPercent!.Value).ToList();
        var targetId = series.TargetIds.FirstOrDefault() ?? "";
        return new IspTargetHealth
        {
            TargetId = targetId,
            Name = series.AsnName ?? targetId,
            MedianRttMs = SeriesStats.Median(rtts),
            P95JitterMs = jitters.Count > 0 ? SeriesStats.Percentile(jitters, 0.95) : null,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            IsGradedHop = targetId == firstHopTargetId
        };
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

    private static IspScoreDimension BuildAsnDimension(string name, double weight, List<IspAsnHealth> asns, bool gradedOnBestHop = false)
    {
        // The ISP Network grades on the lowest (best) hop, so label its RTT "Best" to
        // distinguish it from the mean RTT shown on the Networks on Your Path card
        var rttPrefix = gradedOnBestHop ? "Best " : "";
        var factors = asns.Select(a => new IspScoreFactor
        {
            Name = string.IsNullOrEmpty(a.AsnName) ? $"AS{a.AsnNumber}" : a.AsnName,
            Score = a.OverallScore,
            Weight = 1.0,
            ValueText = a.MedianRttMs.HasValue ? $"{rttPrefix}{FormatMs(a.MedianRttMs.Value)}" : null,
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
            if (inputs.CongestionEvents.Count >= _options.SqmRecurringCongestionEvents)
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
            var downLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedDown);
            var upLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedUp);
            loss = downLoss > profile.LoadedLossDownHighPct || upLoss > profile.LoadedLossUpHighPct;
        }
        return (latency, loss);
    }

    private DateTime FloorToWindow(DateTime time) =>
        CongestionDetector.FloorTime(time, TimeSpan.FromSeconds(_options.LoadWindowSeconds));

    private static string FormatMs(double ms) =>
        ms >= 10 ? $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms" : $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    /// <summary>Loaded-latency delta for display: a non-positive delta shows as "0 ms".</summary>
    private static string FormatLoadedDelta(double ms) => ms <= 0 ? "0 ms" : FormatMs(ms);

    private static string FormatPct(double pct) =>
        pct == 0 ? "0%" : $"{pct.ToString(pct < 0.1 ? "0.000" : "0.00", CultureInfo.InvariantCulture)}%";

    private static string FormatMbps(double mbps) =>
        mbps.ToString(mbps >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
