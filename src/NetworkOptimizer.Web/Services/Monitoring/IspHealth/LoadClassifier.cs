namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Classifies WAN throughput windows as idle or loaded relative to the expected
/// (UniFi-configured) ISP speeds, so latency and loss samples can be judged against
/// idle versus loaded expectations. A window can be loaded in both directions at
/// once; idle requires both directions quiet and both expected speeds known.
/// </summary>
public static class LoadClassifier
{
    public static Dictionary<DateTime, LoadWindow> Classify(
        IReadOnlyList<ThroughputSample> rates,
        double? expectedDownloadMbps,
        double? expectedUploadMbps,
        IspHealthOptions options,
        IReadOnlyList<(DateTime Start, DateTime End)>? exclusionWindows = null,
        ILogger? logger = null)
    {
        var result = new Dictionary<DateTime, LoadWindow>();
        if (rates.Count == 0) return result;

        var expectedDownBps = expectedDownloadMbps * 1_000_000;
        var expectedUpBps = expectedUploadMbps * 1_000_000;
        if (expectedDownBps is null && expectedUpBps is null) return result;

        var windowSize = TimeSpan.FromSeconds(options.LoadWindowSeconds);
        var excluded = 0;
        var excludedLoaded = 0;
        foreach (var group in rates.GroupBy(r => CongestionDetector.FloorTime(r.Time, windowSize)))
        {
            var down = group.Max(r => r.DownloadBps ?? 0);
            var up = group.Max(r => r.UploadBps ?? 0);

            var loadedDown = expectedDownBps.HasValue && down >= options.LoadedThresholdFraction * expectedDownBps.Value;
            var loadedUp = expectedUpBps.HasValue && up >= options.LoadedThresholdFraction * expectedUpBps.Value;

            if (exclusionWindows != null && IsExcluded(group.Key, windowSize, exclusionWindows))
            {
                // Still drop the window from the analysis, but classify it first so the
                // log reflects how many GENUINELY-LOADED windows were removed - the only
                // exclusions that change the loaded-line result. Idle exclusions are noise.
                result[group.Key] = new LoadWindow(false, false, false);
                excluded++;
                if (loadedDown || loadedUp) excludedLoaded++;
                continue;
            }

            var idle = expectedDownBps.HasValue && expectedUpBps.HasValue
                && down < options.IdleThresholdFraction * expectedDownBps.Value
                && up < options.IdleThresholdFraction * expectedUpBps.Value;

            result[group.Key] = new LoadWindow(idle, loadedDown, loadedUp);
        }
        if (excluded > 0)
            logger?.LogDebug(
                "ISP Health: excluded {Count} window(s) overlapping SQM probe schedule, {Loaded} of which would have classified as loaded",
                excluded, excludedLoaded);
        return result;
    }

    private static bool IsExcluded(DateTime windowStart, TimeSpan windowSize, IReadOnlyList<(DateTime Start, DateTime End)> exclusions)
    {
        var windowEnd = windowStart + windowSize;
        foreach (var (exStart, exEnd) in exclusions)
        {
            if (windowStart < exEnd && windowEnd > exStart) return true;
        }
        return false;
    }
}
