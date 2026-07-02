using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Detects flaky WAN-path monitoring targets (issue #849): an AccessIsp / Transit / InternetService
/// target whose packet loss is notably above the peer baseline, consistently, over the retained
/// window - usually ICMP-deprioritized routers. Relative-to-peers so it auto-calibrates per
/// connection (congested DOCSIS at 0.5% across the board doesn't false-positive; a transit router
/// at 10% against a 0.2% baseline does).
///
/// Computed on demand (Live View banner + Network Performance panel initial loads) - never polled.
/// Reuses the existing per-target loss query; adds no Influx measurements.
/// </summary>
public class FlakyTargetService
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<FlakyTargetService> _logger;

    public FlakyTargetService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        MonitoringInfluxRegistry influxRegistry,
        ILogger<FlakyTargetService> logger)
    {
        _dbFactory = dbFactory;
        _influx = influxRegistry.GetDefault();
        _logger = logger;
    }

    /// <summary>Lookback window; we use all available data up to this, never require it.</summary>
    private const int LookbackHours = 48;
    /// <summary>Bin size. Fine enough (loss arrives every 10-15 s) that we can help within ~30 min of monitoring.</summary>
    private const int BinMinutes = 10;
    /// <summary>A target is "over" in a bin when its loss is at least this multiple of the peer median.</summary>
    private const double LossMultiplier = 4.0;
    /// <summary>...and at least this absolute loss, so trivial loss on a near-zero baseline isn't flagged.</summary>
    private const double LossAbsoluteFloorPct = 3.0;
    /// <summary>A bin is a shared-loss event (excluded) when at least this fraction of the pool loses heavily in it.</summary>
    private const double SharedLossPoolFraction = 0.5;
    /// <summary>Loss at or above this in a bin counts as "losing heavily" for the shared-loss test.</summary>
    private const double SharedLossHeavyPct = 5.0;
    /// <summary>Minimum surviving bins before we'll judge a target. 3 x 10 min = help from ~30 min in, not 6 h.</summary>
    private const int MinTargetBins = 3;

    /// <summary>One flagged target plus the evidence behind the call.</summary>
    public record FlakyTarget(
        string TargetId,
        int Id,
        string Name,
        MonitoringTargetType Type,
        double LossPct,
        double BaselinePct,
        int OverBins,
        int TotalBins)
    {
        public string Evidence => $"{LossPct:0.0}% loss vs {BaselinePct:0.0}% peer median";
    }

    /// <summary>
    /// Returns the flaky targets, worst loss first. Empty when there isn't enough data yet,
    /// Influx isn't configured, or nothing is flaky.
    /// </summary>
    public async Task<IReadOnlyList<FlakyTarget>> DetectAsync(CancellationToken ct = default)
    {
        List<MonitoringTarget> pool;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            pool = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit
                    || t.TargetType == MonitoringTargetType.InternetService))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Flaky-target detect: failed to load pool");
            return System.Array.Empty<FlakyTarget>();
        }
        if (pool.Count < 2) return System.Array.Empty<FlakyTarget>();

        var byId = pool.ToDictionary(t => t.TargetId, t => t);
        var to = DateTime.UtcNow;
        var from = to.AddHours(-LookbackHours);
        var binSize = TimeSpan.FromMinutes(BinMinutes);

        // Per-target binned loss (the aggregateWindow does the binning). Pull all three
        // WAN-path types and keep only enabled pool targets.
        var lossByTarget = new Dictionary<string, Dictionary<DateTime, double>>();
        foreach (var type in new[] { MonitoringTargetType.AccessIsp, MonitoringTargetType.Transit, MonitoringTargetType.InternetService })
        {
            Dictionary<string, List<MonitoringInfluxClient.LatencySeriesPoint>> series;
            try
            {
                series = await _influx.QueryLatencyDetailByTargetTypeAsync(type, from, to, binSize, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Flaky-target detect: loss query failed for {Type}", type);
                return System.Array.Empty<FlakyTarget>();
            }
            foreach (var (targetId, points) in series)
            {
                if (!byId.ContainsKey(targetId)) continue;
                var bins = lossByTarget.TryGetValue(targetId, out var existing) ? existing : lossByTarget[targetId] = new();
                foreach (var p in points)
                {
                    if (!p.LossPercent.HasValue) continue;
                    bins[FloorToBin(p.Time, binSize)] = p.LossPercent.Value;
                }
            }
        }
        if (lossByTarget.Count < 2) return System.Array.Empty<FlakyTarget>();

        return Analyze(lossByTarget, byId, _logger);
    }

    /// <summary>
    /// Pure analysis (no I/O): given per-target binned loss, flag the flaky ones. A target is flaky
    /// when its TRIMMED-MEAN surviving-bin loss (drop the single highest + lowest bin, average the
    /// rest) is at or above the threshold (>= <see cref="LossMultiplier"/>x the peer-median loss AND
    /// >= <see cref="LossAbsoluteFloorPct"/>). Trimmed mean is spike-robust like a median (a single
    /// 100% bin on an otherwise-clean target trims away) but, unlike a small-sample median, it does
    /// not flap when one clean bin rolls in - the median of ~6 bursty bins can swing across the floor
    /// (e.g. 2.86% -> 4.5%), the trimmed mean stays put (~4%). Bins where >= half the pool loses
    /// heavily are excluded first as path-wide events, and a target needs <see cref="MinTargetBins"/>
    /// surviving bins to be judged. The peer baseline still uses a plain median across the pool.
    /// </summary>
    internal static IReadOnlyList<FlakyTarget> Analyze(
        Dictionary<string, Dictionary<DateTime, double>> lossByTarget,
        IReadOnlyDictionary<string, MonitoringTarget> byId,
        ILogger? logger = null)
    {
        // Shared-loss exclusion: drop bins where at least half the reporting pool is losing heavily
        // - a path-wide event (outage/congestion), not one flaky target. Cheap proxy for the real
        // outage detector: no dark-fraction gating, just "are most targets hurting this bin?".
        var allBins = lossByTarget.Values.SelectMany(b => b.Keys).ToHashSet();
        var excludedBins = new HashSet<DateTime>();
        foreach (var bin in allBins)
        {
            var reporting = lossByTarget.Values.Where(b => b.ContainsKey(bin)).Select(b => b[bin]).ToList();
            if (reporting.Count == 0) continue;
            var heavy = reporting.Count(l => l >= SharedLossHeavyPct);
            if ((double)heavy / reporting.Count >= SharedLossPoolFraction)
                excludedBins.Add(bin);
        }

        var survivingBins = allBins.Count - excludedBins.Count;
        if (survivingBins < MinTargetBins)
        {
            logger?.LogDebug("Flaky-target detect: only {Bins} surviving bins (< {Min}); not enough data yet", survivingBins, MinTargetBins);
            return System.Array.Empty<FlakyTarget>();
        }

        // Peer baseline: median of every surviving (target, bin) loss value. Median, not mean, so the
        // flaky targets we're hunting don't inflate the baseline and mask each other.
        var allLosses = new List<double>();
        foreach (var bins in lossByTarget.Values)
            foreach (var (bin, loss) in bins)
                if (!excludedBins.Contains(bin)) allLosses.Add(loss);
        if (allLosses.Count == 0) return System.Array.Empty<FlakyTarget>();
        var baseline = Median(allLosses);
        var threshold = Math.Max(LossMultiplier * baseline, LossAbsoluteFloorPct);

        var flaky = new List<FlakyTarget>();
        foreach (var (targetId, bins) in lossByTarget)
        {
            var survivors = bins.Where(kv => !excludedBins.Contains(kv.Key)).Select(kv => kv.Value).ToList();
            if (survivors.Count < MinTargetBins) continue;
            var metric = TrimmedMean(survivors);
            if (metric < threshold) continue;

            if (!byId.TryGetValue(targetId, out var t)) continue;
            var over = survivors.Count(l => l >= threshold);
            flaky.Add(new FlakyTarget(targetId, t.Id, string.IsNullOrEmpty(t.Name) ? t.Address : t.Name,
                t.TargetType, metric, baseline, over, survivors.Count));
        }

        logger?.LogDebug("Flaky-target detect: {Count} flagged, baseline {Base:0.00}%, threshold {Thr:0.00}%, {Bins} surviving bins",
            flaky.Count, baseline, threshold, survivingBins);
        return flaky.OrderByDescending(f => f.LossPct).ToList();
    }

    private static DateTime FloorToBin(DateTime t, TimeSpan bin) =>
        new DateTime(t.Ticks - (t.Ticks % bin.Ticks), DateTimeKind.Utc);

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var n = sorted.Count;
        if (n == 0) return 0;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[n / 2 - 1] + sorted[n / 2]) / 2.0;
    }

    /// <summary>
    /// Mean after dropping the single lowest and single highest value. Spike-robust (one bad bin
    /// trims away) without the small-sample jumpiness of a median. Callers gate on Count &gt;= 3, so
    /// at least one value always remains.
    /// </summary>
    private static double TrimmedMean(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count < 3) return sorted.Count == 0 ? 0 : sorted.Average();
        var core = sorted.GetRange(1, sorted.Count - 2);
        return core.Average();
    }
}
