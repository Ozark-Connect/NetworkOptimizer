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
        // Peak per window, accumulated in one pass. GroupBy allocated an IGrouping and a backing list
        // per window, and ISP Health now holds the rate series fine on long spans - six figures of
        // windows, nearly all of them holding a single sample.
        var peaks = new Dictionary<DateTime, (double Down, double Up)>();
        var maxDown = 0.0;
        var maxUp = 0.0;
        DateTime firstTime = default, lastTime = default;
        for (var i = 0; i < rates.Count; i++)
        {
            var r = rates[i];
            var key = CongestionDetector.FloorTime(r.Time, windowSize);
            var d = r.DownloadBps ?? 0;
            var u = r.UploadBps ?? 0;
            if (peaks.TryGetValue(key, out var cur))
                peaks[key] = (d > cur.Down ? d : cur.Down, u > cur.Up ? u : cur.Up);
            else
                peaks[key] = (d, u);

            // Log inputs gathered here rather than in four more passes over the series.
            if (d > maxDown) maxDown = d;
            if (u > maxUp) maxUp = u;
            if (i == 0 || r.Time < firstTime) firstTime = r.Time;
            if (i == 0 || r.Time > lastTime) lastTime = r.Time;
        }

        foreach (var (key, peak) in peaks)
        {
            var loadedDown = expectedDownBps.HasValue && peak.Down >= options.LoadedThresholdFraction * expectedDownBps.Value;
            var loadedUp = expectedUpBps.HasValue && peak.Up >= options.LoadedThresholdFraction * expectedUpBps.Value;

            if (exclusionWindows != null && IsExcluded(key, windowSize, exclusionWindows))
            {
                // Still drop the window from the analysis, but classify it first so the
                // log reflects how many GENUINELY-LOADED windows were removed - the only
                // exclusions that change the loaded-line result. Idle exclusions are noise.
                result[key] = new LoadWindow(false, false, false);
                excluded++;
                if (loadedDown || loadedUp) excludedLoaded++;
                continue;
            }

            var idle = expectedDownBps.HasValue && expectedUpBps.HasValue
                && peak.Down < options.IdleThresholdFraction * expectedDownBps.Value
                && peak.Up < options.IdleThresholdFraction * expectedUpBps.Value;

            // Recorded twice: DemoteIsolated clears the live flags, never the classified pair.
            result[key] = new LoadWindow(idle, loadedDown, loadedUp, loadedDown, loadedUp);
        }

        // Demote loaded windows that stand alone. A saturating transfer holds across consecutive
        // samples; a counter artifact - a delta spanning a reset - is a single sample with idle
        // neighbors. Magnitude cannot separate them, because bursty access media legitimately read
        // well above plan, so persistence is the discriminator. Runs are counted over samples in time
        // order rather than adjacent window keys: the rate series is coarser than the window size, so
        // consecutive samples are never in adjacent keys.
        DemoteIsolated(result, options);
        if (excluded > 0)
            logger?.LogDebug(
                "ISP Health: excluded {Count} window(s) overlapping SQM probe schedule, {Loaded} of which would have classified as loaded",
                excluded, excludedLoaded);

        // What the classifier actually saw. Guarded, and built from values gathered in the pass above:
        // debug logging is on permanently at the test sites, so an unguarded diagnostic here walked
        // six figures of samples several more times on every compute.
        if (logger?.IsEnabled(LogLevel.Debug) == true)
        {
            var loadedKeys = new List<DateTime>();
            foreach (var (key, w) in result)
                if (w.IsLoadedDown || w.IsLoadedUp) loadedKeys.Add(key);
            loadedKeys.Sort();
            logger.LogDebug(
                "ISP Health: load classify - {Rates} rate sample(s) spanning {First:MM-dd HH:mm:ss} to {Last:MM-dd HH:mm:ss}, max {MaxDown}/{MaxUp} Mbps vs {PlanDown}/{PlanUp} plan, {Loaded} of {Total} window(s) loaded: {Keys}",
                rates.Count, firstTime, lastTime,
                (maxDown / 1e6).ToString("0.#"), (maxUp / 1e6).ToString("0.#"),
                expectedDownloadMbps, expectedUploadMbps,
                loadedKeys.Count, result.Count,
                string.Join(" | ", loadedKeys.Take(8).Select(k => k.ToString("MM-dd HH:mm:ss"))));
        }
        return result;
    }

    /// <summary>
    /// Clears the loaded flags on any run shorter than
    /// <see cref="IspHealthOptions.MinLoadedRunSamples"/>. Each direction is judged on its own - a
    /// download transfer and an upload one rarely coincide - and a gap longer than a couple of sample
    /// intervals breaks a run, so load either side of a monitoring outage is not stitched into one.
    /// </summary>
    private static void DemoteIsolated(Dictionary<DateTime, LoadWindow> windows, IspHealthOptions options)
    {
        if (options.MinLoadedRunSamples <= 1 || windows.Count == 0) return;

        // Sorted once and reused for both directions: at a long window this list runs to six figures,
        // and sorting it twice was pure waste.
        var ordered = windows.Keys.ToList();
        ordered.Sort();
        // Samples arrive on the rate aggregation interval, which is coarser than the window size, so
        // "adjacent" is measured against the observed spacing rather than assumed to be one window.
        var spacing = ordered.Count > 1
            ? TimeSpan.FromSeconds(Math.Max(options.LoadWindowSeconds,
                (ordered[^1] - ordered[0]).TotalSeconds / (ordered.Count - 1)))
            : TimeSpan.FromSeconds(options.LoadWindowSeconds);
        var maxGap = TimeSpan.FromSeconds(spacing.TotalSeconds * 2.5);

        void Sweep(Func<LoadWindow, bool> isLoaded, Func<LoadWindow, LoadWindow> demote)
        {
            var i = 0;
            while (i < ordered.Count)
            {
                if (!isLoaded(windows[ordered[i]])) { i++; continue; }

                // Extend while the next window is loaded AND close enough to belong to the same run,
                // so load either side of a monitoring gap is not stitched into one.
                var start = i;
                var end = i + 1;
                while (end < ordered.Count
                    && isLoaded(windows[ordered[end]])
                    && ordered[end] - ordered[end - 1] <= maxGap)
                    end++;

                // Demote only on POSITIVE evidence of isolation: a neighbor that exists, sits within
                // the sampling gap, and is not loaded. A run at the edge of the series, or one whose
                // neighbors are missing, goes unjudged - absence of evidence is not evidence of a
                // spike, and treating it as one would discard the only sample a short window has.
                if (end - start < options.MinLoadedRunSamples)
                {
                    var quietBefore = start > 0
                        && ordered[start] - ordered[start - 1] <= maxGap
                        && !isLoaded(windows[ordered[start - 1]]);
                    var quietAfter = end < ordered.Count
                        && ordered[end] - ordered[end - 1] <= maxGap
                        && !isLoaded(windows[ordered[end]]);
                    if (quietBefore || quietAfter)
                        for (var j = start; j < end; j++)
                            windows[ordered[j]] = demote(windows[ordered[j]]);
                }

                i = end;
            }
        }

        Sweep(w => w.IsLoadedDown, w => w with { IsLoadedDown = false });
        Sweep(w => w.IsLoadedUp, w => w with { IsLoadedUp = false });
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
