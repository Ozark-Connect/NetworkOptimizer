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
        var (speedVsPlan, bestSpeedTest) = ScoreSpeedVsPlan(inputs);
        var idleLatency = ScoreIdleLatency(idleBaseline, profile);
        var idleLoss = ScoreIdleLoss(inputs.LossPoolSeries, profile);
        var (loadedLatency, hasLoadedLatency) = ScoreLoadedLatency(inputs.FirstHopSeries, loadWindows, idleBaseline, profile);
        var (loadedLoss, hasLoadedLoss) = ScoreLoadedLoss(inputs.LossPoolSeries, loadWindows, profile);

        var accessFactors = new List<IspScoreFactor> { speedVsPlan, idleLatency, idleLoss, loadedLatency, loadedLoss };
        var accessDimension = BuildDimension("Access Layer", _options.AccessWeight, accessFactors);

        var accessMedianRtt = SeriesStats.Median(
            inputs.FirstHopSeries.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList());
        var transitAsns = inputs.TransitAsnSeries.Select(s => GradeAsn(s, inputs.CongestionEvents, accessMedianRtt)).ToList();
        var ispAsns = inputs.IspAsnSeries.Select(s => GradeAsn(s, inputs.CongestionEvents, accessBaselineRtt: null)).ToList();
        var transitDimension = BuildAsnDimension("Transit Health", _options.TransitWeight, transitAsns);
        var ispAsnDimension = BuildAsnDimension("ISP Network", _options.IspAsnWeight, ispAsns);

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
            CongestionEvents = inputs.CongestionEvents,
            PathShifts = inputs.PathShifts,
            HasExpectedSpeeds = hasExpectedSpeeds,
            HasLoadedSamples = hasLoadedLatency || hasLoadedLoss,
            ExpectedDownloadMbps = inputs.ExpectedDownloadMbps,
            ExpectedUploadMbps = inputs.ExpectedUploadMbps,
            ExpectedSpeedSource = inputs.ExpectedSpeedSource,
            MeasuredDownloadMbps = bestSpeedTest?.DownloadMbps,
            MeasuredUploadMbps = bestSpeedTest?.UploadMbps,
            SpeedTestTime = bestSpeedTest?.Time
        };
        report.Issues.AddRange(CollectIssues(inputs, profile, report, loadWindows, idleBaseline));
        return report;
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
    /// Grades demonstrated WAN throughput against the configured plan speeds. Uses
    /// the best test in the window (per-direction max) so a congested test slot does
    /// not double-penalize what the loaded factors already measure; chronic
    /// underdelivery still shows because no test reaches the plan. Falls back to the
    /// most recent test within SpeedTestFallbackDays when none ran inside the window.
    /// </summary>
    private (IspScoreFactor Factor, SpeedTestSample? Best) ScoreSpeedVsPlan(IspHealthInputs inputs)
    {
        if (!inputs.ExpectedDownloadMbps.HasValue && !inputs.ExpectedUploadMbps.HasValue)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Set your ISP speeds in UniFi Network to grade throughput against your plan."
            }, null);
        }

        var inWindow = inputs.WanSpeedTests.Where(t => t.Time >= inputs.WindowStart && t.Time <= inputs.WindowEnd).ToList();
        var stale = false;
        if (inWindow.Count == 0)
        {
            var fallbackStart = inputs.WindowEnd.AddDays(-_options.SpeedTestFallbackDays);
            var latest = inputs.WanSpeedTests.Where(t => t.Time >= fallbackStart).OrderByDescending(t => t.Time).FirstOrDefault();
            if (latest == null)
            {
                return (new IspScoreFactor
                {
                    Name = "Speed vs Plan",
                    Weight = _options.SpeedVsPlanWeight,
                    Description = "No recent WAN speed test. Run one (or enable scheduled WAN tests) to grade throughput against your plan."
                }, null);
            }
            inWindow.Add(latest);
            stale = true;
        }

        var bestDown = inWindow.Max(t => t.DownloadMbps);
        var bestUp = inWindow.Max(t => t.UploadMbps);
        var bestTest = inWindow.OrderByDescending(t => t.DownloadMbps + t.UploadMbps).First();

        var scores = new List<double>();
        if (inputs.ExpectedDownloadMbps is > 0) scores.Add(ScoreSpeedRatio(bestDown / inputs.ExpectedDownloadMbps.Value));
        if (inputs.ExpectedUploadMbps is > 0) scores.Add(ScoreSpeedRatio(bestUp / inputs.ExpectedUploadMbps.Value));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Speed vs Plan",
                Weight = _options.SpeedVsPlanWeight,
                Description = "Expected ISP speeds are configured as zero; cannot grade throughput."
            }, null);
        }

        var staleNote = stale ? $" Latest test is older than the {_options.ScoreWindowHours} h window." : "";
        return (new IspScoreFactor
        {
            Name = "Speed vs Plan",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.SpeedVsPlanWeight,
            ValueText = $"{FormatMbps(bestDown)} / {FormatMbps(bestUp)} Mbps",
            Description = $"Best WAN speed test vs the {FormatMbps(inputs.ExpectedDownloadMbps ?? 0)} / {FormatMbps(inputs.ExpectedUploadMbps ?? 0)} Mbps plan configured in UniFi Network.{staleNote}"
        }, new SpeedTestSample(bestTest.Time, bestDown, bestUp));
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
            (profile.IdleRttNormalLowMs, 88),
            (mid, 75),
            (profile.IdleRttNormalHighMs, 62),
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

    private (IspScoreFactor Factor, bool HasData) ScoreLoadedLatency(
        IReadOnlyList<LatencySample> firstHop,
        Dictionary<DateTime, LoadWindow> loadWindows,
        double? idleBaseline,
        AccessProfile profile)
    {
        if (idleBaseline == null || loadWindows.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Latency",
                Weight = _options.LoadedLatencyWeight,
                Description = "Loaded latency needs expected ISP speeds and load on the line."
            }, false);
        }

        var downDelta = LoadedMedianDelta(firstHop, loadWindows, idleBaseline.Value, w => w.IsLoadedDown);
        var upDelta = LoadedMedianDelta(firstHop, loadWindows, idleBaseline.Value, w => w.IsLoadedUp);

        var scores = new List<double>();
        if (downDelta.HasValue) scores.Add(ScoreLoadedDelta(downDelta.Value, profile));
        if (upDelta.HasValue) scores.Add(ScoreLoadedDelta(upDelta.Value, profile));
        if (scores.Count == 0)
        {
            return (new IspScoreFactor
            {
                Name = "Loaded Latency",
                Weight = _options.LoadedLatencyWeight,
                Description = "The line was never under sustained load during the window."
            }, false);
        }

        var parts = new List<string>();
        if (downDelta.HasValue) parts.Add($"+{FormatMs(downDelta.Value)} down");
        if (upDelta.HasValue) parts.Add($"+{FormatMs(upDelta.Value)} up");

        return (new IspScoreFactor
        {
            Name = "Loaded Latency",
            Score = (int)Math.Round(scores.Average()),
            Weight = _options.LoadedLatencyWeight,
            ValueText = string.Join(", ", parts),
            Description = $"Latency increase under load vs +{FormatMs(profile.LoadedDeltaExcellentMs)} excellent and +{FormatMs(profile.LoadedDeltaAcceptableMs)} acceptable for {profile.DisplayName}."
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

    private double? LoadedMedianDelta(
        IReadOnlyList<LatencySample> firstHop,
        Dictionary<DateTime, LoadWindow> loadWindows,
        double idleBaseline,
        Func<LoadWindow, bool> directionSelector)
    {
        var rtts = firstHop
            .Where(s => s.RttAvgMs.HasValue
                && loadWindows.TryGetValue(FloorToWindow(s.Time), out var w)
                && directionSelector(w))
            .Select(s => s.RttAvgMs!.Value)
            .ToList();
        if (rtts.Count < _options.MinLoadedSamples) return null;
        return SeriesStats.Median(rtts)!.Value - idleBaseline;
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

    private double ScoreLossBand(double loss, double bandLow, double bandHigh)
    {
        return loss <= bandHigh
            ? ScoreCurve.Interpolate(loss, (0, 100), (bandLow, 90), (bandHigh, 70))
            : ScoreCurve.ExponentialFalloff(loss, bandHigh, 70);
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
    /// Grades one ASN. <paramref name="accessBaselineRtt"/> is the median RTT of the
    /// first clean ISP hop; when provided (transit ASNs), the grade includes reach
    /// latency, the access-normalized distance to that network. ISP ASNs pass null
    /// since their delta over the access layer is near zero by definition.
    /// </summary>
    private IspAsnHealth GradeAsn(AsnSeries series, List<CongestionEvent> congestionEvents, double? accessBaselineRtt)
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
            var relative = ScoreCurve.Interpolate(medianJitter.Value / medianRtt.Value,
                (0.05, 100), (0.15, 75), (0.30, 45), (0.60, 0));
            var absolute = ScoreCurve.Interpolate(medianJitter.Value,
                (0.5, 100), (2, 75), (5, 45), (15, 0));
            jitterScore = (int)Math.Round(Math.Max(relative, absolute));
        }

        double? reachDelta = null;
        int? reachScore = null;
        if (accessBaselineRtt.HasValue && medianRtt.HasValue)
        {
            reachDelta = Math.Max(0, medianRtt.Value - accessBaselineRtt.Value);
            reachScore = (int)Math.Round(ScoreCurve.Interpolate(reachDelta.Value,
                (1, 100), (7, 90), (10, 75), (15, 55), (25, 30), (40, 0)));
        }

        var asnEvents = congestionEvents.Where(e => e.AsnNumbers.Contains(series.AsnNumber)).ToList();
        var eventHours = asnEvents.Sum(e => e.Duration.TotalHours);
        var congestionScore = (int)Math.Round(Math.Max(0, 100 - _options.CongestionPenaltyPerHour * eventHours));

        int? overall = null;
        var weighted = new List<(double Score, double Weight)>();
        if (stabilityScore.HasValue) weighted.Add((stabilityScore.Value, _options.AsnLatencyStabilityWeight));
        if (jitterScore.HasValue) weighted.Add((jitterScore.Value, _options.AsnJitterWeight));
        if (reachScore.HasValue) weighted.Add((reachScore.Value, _options.AsnReachWeight));
        weighted.Add((congestionScore, _options.AsnCongestionWeight));
        if (stabilityScore.HasValue || jitterScore.HasValue)
        {
            var totalWeight = weighted.Sum(w => w.Weight);
            overall = (int)Math.Round(weighted.Sum(w => w.Score * w.Weight) / totalWeight);
        }

        return new IspAsnHealth
        {
            AsnNumber = series.AsnNumber,
            AsnName = series.AsnName,
            TargetIds = series.TargetIds,
            MedianRttMs = medianRtt,
            P95RttMs = SeriesStats.Percentile(rtts, 0.95),
            MedianJitterMs = medianJitter,
            P95JitterMs = jitters.Count > 0 ? SeriesStats.Percentile(jitters, 0.95) : null,
            RttMadMs = mad,
            LossPct = losses.Count > 0 ? losses.Average() : null,
            ReachDeltaMs = reachDelta,
            LatencyStabilityScore = stabilityScore,
            JitterScore = jitterScore,
            ReachLatencyScore = reachScore,
            CongestionScore = congestionScore,
            OverallScore = overall,
            CongestionEventCount = asnEvents.Count
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

    private static IspScoreDimension BuildAsnDimension(string name, double weight, List<IspAsnHealth> asns)
    {
        var factors = asns.Select(a => new IspScoreFactor
        {
            Name = string.IsNullOrEmpty(a.AsnName) ? $"AS{a.AsnNumber}" : a.AsnName,
            Score = a.OverallScore,
            Weight = 1.0,
            ValueText = a.MedianRttMs.HasValue ? FormatMs(a.MedianRttMs.Value) : null,
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
        double? idleBaseline)
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

        var sqmTriggered = SqmRecommendationTriggered(inputs, profile, loadWindows, idleBaseline);
        if (sqmTriggered)
        {
            var recommendation = inputs.SmartQueuesEnabled
                ? "Smart Queues is enabled on this WAN but the line still degrades under load; check that its configured rates match what the line actually delivers."
                : "Enable Smart Queues (SQM) on this WAN in UniFi Network (Settings, Internet, your WAN, Smart Queues).";
            if (inputs.CongestionEvents.Count >= _options.SqmRecurringCongestionEvents)
            {
                recommendation += " This connection also shows a recurring congestion pattern; consider Adaptive SQM, which tracks time-of-day capacity changes automatically.";
            }
            issues.Add(new IspHealthIssue
            {
                Severity = IspIssueSeverity.Warning,
                Title = "Bufferbloat under load",
                Description = "Latency or packet loss rises well beyond the excellent range for this connection type when the line is loaded.",
                Recommendation = recommendation,
                LinkUrl = "/sqm",
                LinkText = "Adaptive SQM"
            });
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

    private bool SqmRecommendationTriggered(
        IspHealthInputs inputs,
        AccessProfile profile,
        Dictionary<DateTime, LoadWindow> loadWindows,
        double? idleBaseline)
    {
        if (idleBaseline == null || loadWindows.Count == 0) return false;

        var bandWidth = profile.LoadedDeltaAcceptableMs - profile.LoadedDeltaExcellentMs;
        var deltaThreshold = profile.LoadedDeltaExcellentMs + _options.SqmDeviationFactor * bandWidth;

        var downDelta = LoadedMedianDelta(inputs.FirstHopSeries, loadWindows, idleBaseline.Value, w => w.IsLoadedDown);
        var upDelta = LoadedMedianDelta(inputs.FirstHopSeries, loadWindows, idleBaseline.Value, w => w.IsLoadedUp);
        if (downDelta > deltaThreshold || upDelta > deltaThreshold) return true;

        var downLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedDown);
        var upLoss = LoadedMeanLoss(inputs.LossPoolSeries, loadWindows, w => w.IsLoadedUp);
        return downLoss > profile.LoadedLossDownHighPct || upLoss > profile.LoadedLossUpHighPct;
    }

    private DateTime FloorToWindow(DateTime time) =>
        CongestionDetector.FloorTime(time, TimeSpan.FromMinutes(_options.AggregateWindowMinutes));

    private static string FormatMs(double ms) =>
        ms >= 10 ? $"{ms.ToString("0", CultureInfo.InvariantCulture)} ms" : $"{ms.ToString("0.0", CultureInfo.InvariantCulture)} ms";

    private static string FormatPct(double pct) =>
        $"{pct.ToString(pct < 0.1 ? "0.000" : "0.00", CultureInfo.InvariantCulture)}%";

    private static string FormatMbps(double mbps) =>
        mbps.ToString(mbps >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture);
}
