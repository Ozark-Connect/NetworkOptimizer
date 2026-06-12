namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Detects sustained step changes (up or down) in path RTT, the signature of a BGP
/// or transport fabric shift. Compares medians of adjacent fixed windows, requires
/// non-overlapping IQRs to reject noise, and requires the new level to persist for
/// several windows so congestion humps (which revert) are not reported as shifts.
/// Events are informational only and never affect the ISP Health score. Thresholds
/// live in <see cref="IspHealthOptions"/>; defaults are provisional until validated
/// against real shift data (see research/isp-health-spec.md Appendix B).
/// </summary>
public static class StepChangeDetector
{
    public static List<PathShiftEvent> Detect(IReadOnlyList<AsnSeries> allSeries, IspHealthOptions options)
    {
        var events = new List<PathShiftEvent>();
        foreach (var series in allSeries)
        {
            events.AddRange(DetectForSeries(series, options));
        }
        return CorrelateAcrossSeries(events, options);
    }

    /// <summary>Detects step changes within a single series. Exposed for replay against exported data.</summary>
    public static List<PathShiftEvent> DetectForSeries(AsnSeries series, IspHealthOptions options)
    {
        var samples = series.Samples.Where(s => s.RttAvgMs.HasValue).OrderBy(s => s.Time).ToList();
        if (samples.Count == 0) return new List<PathShiftEvent>();

        var windowSize = TimeSpan.FromMinutes(options.StepWindowMinutes);
        var windows = samples
            .GroupBy(s => CongestionDetector.FloorTime(s.Time, windowSize))
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var rtts = g.Select(s => s.RttAvgMs!.Value).ToList();
                return new Window(g.Key, SeriesStats.Median(rtts)!.Value, SeriesStats.Iqr(rtts)!.Value);
            })
            .ToList();

        var events = new List<PathShiftEvent>();
        for (var i = 0; i + 1 < windows.Count; i++)
        {
            // A shift that lands mid-window leaves a transition window straddling both
            // levels, whose wide IQR overlaps the before-window and masks the step
            // (seen in real data). So also try comparing against the window after it,
            // accepting the skipped window only when its median sits between the levels.
            var detected = TryDetectStep(windows, i, afterIndex: i + 1, options)
                ?? TryDetectStep(windows, i, afterIndex: i + 2, options);
            if (detected == null) continue;
            var (afterIdx, before, after) = detected.Value;

            events.Add(new PathShiftEvent
            {
                Time = windows[i + 1].Start,
                AsnNumber = series.AsnNumber,
                AsnName = series.AsnName,
                TargetId = series.TargetIds.Count == 1 ? series.TargetIds[0] : null,
                BeforeMedianMs = before.MedianMs,
                AfterMedianMs = after.MedianMs
            });

            // Skip past the persistence span so one shift is not re-reported per boundary
            i = afterIdx + options.StepPersistenceWindows - 1;
        }
        return events;
    }

    private static (int AfterIdx, Window Before, Window After)? TryDetectStep(
        List<Window> windows, int beforeIndex, int afterIndex, IspHealthOptions options)
    {
        if (afterIndex >= windows.Count) return null;
        var before = windows[beforeIndex];
        var after = windows[afterIndex];
        var delta = after.MedianMs - before.MedianMs;
        var minDelta = Math.Max(options.StepMinDeltaMs, options.StepMinRelativeChange * before.MedianMs);
        if (Math.Abs(delta) < minDelta) return null;
        if (IqrsOverlap(before.Iqr, after.Iqr)) return null;
        if (!IsStableLevel(before, options) || !IsStableLevel(after, options)) return null;
        if (afterIndex == beforeIndex + 2)
        {
            var transition = windows[beforeIndex + 1].MedianMs;
            var inBetween = delta > 0
                ? transition > before.MedianMs && transition < after.MedianMs
                : transition < before.MedianMs && transition > after.MedianMs;
            if (!inBetween) return null;
        }
        if (!EstablishedAtOldLevel(windows, beforeIndex, delta, options)) return null;
        if (!PersistsAtNewLevel(windows, before.MedianMs, delta, afterIndex, options)) return null;
        return (afterIndex, before, after);
    }

    private static bool IqrsOverlap((double Q1, double Q3) a, (double Q1, double Q3) b) =>
        a.Q1 <= b.Q3 && b.Q1 <= a.Q3;

    /// <summary>
    /// A window only anchors a step when its samples sit tightly around the median.
    /// Real routing levels measure tight (sub-ms IQR widths in validation data);
    /// congestion is noisy, so its edges fail this gate instead of reading as steps.
    /// </summary>
    private static bool IsStableLevel(Window window, IspHealthOptions options)
    {
        var width = window.Iqr.Q3 - window.Iqr.Q1;
        return width <= Math.Max(options.StepStableIqrFloorMs, options.StepStableIqrFraction * window.MedianMs);
    }

    /// <summary>
    /// The medians of the N windows up to and including the boundary must all sit on
    /// the old side of the midpoint. Without this, the trailing edge of a brief spike
    /// (which never established a level) would read as a step back down.
    /// </summary>
    private static bool EstablishedAtOldLevel(List<Window> windows, int boundaryIndex, double delta, IspHealthOptions options)
    {
        var midpoint = windows[boundaryIndex].MedianMs + delta / 2.0;
        var firstRequired = boundaryIndex - (options.StepPersistenceWindows - 1);
        if (firstRequired < 0) return false;
        for (var j = firstRequired; j <= boundaryIndex; j++)
        {
            var onOldSide = delta > 0 ? windows[j].MedianMs < midpoint : windows[j].MedianMs > midpoint;
            if (!onOldSide) return false;
        }
        return true;
    }

    /// <summary>
    /// The medians of N windows starting at the after-window must all stay on the new
    /// side of the midpoint between the old and new levels. A congestion hump reverts;
    /// a real shift holds.
    /// </summary>
    private static bool PersistsAtNewLevel(List<Window> windows, double beforeMedian, double delta, int afterIndex, IspHealthOptions options)
    {
        var midpoint = beforeMedian + delta / 2.0;
        var lastRequired = afterIndex + options.StepPersistenceWindows - 1;
        if (lastRequired >= windows.Count) return false;
        for (var j = afterIndex; j <= lastRequired; j++)
        {
            var onNewSide = delta > 0 ? windows[j].MedianMs > midpoint : windows[j].MedianMs < midpoint;
            if (!onNewSide) return false;
        }
        return true;
    }

    /// <summary>
    /// Groups shifts that occur at the same boundary (within one window) across series
    /// and annotates each with how many targets moved together. Correlated shifts are
    /// a stronger indication of a routing change than a single-target step.
    /// </summary>
    public static List<PathShiftEvent> CorrelateAcrossSeries(List<PathShiftEvent> events, IspHealthOptions options)
    {
        if (events.Count <= 1) return events;
        var tolerance = TimeSpan.FromMinutes(options.StepWindowMinutes);
        return events
            .Select(evt =>
            {
                var correlated = events.Count(other =>
                    (other.Time - evt.Time).Duration() <= tolerance && other.Direction == evt.Direction);
                return new PathShiftEvent
                {
                    Time = evt.Time,
                    AsnNumber = evt.AsnNumber,
                    AsnName = evt.AsnName,
                    TargetId = evt.TargetId,
                    BeforeMedianMs = evt.BeforeMedianMs,
                    AfterMedianMs = evt.AfterMedianMs,
                    CorrelatedTargetCount = correlated
                };
            })
            .OrderBy(e => e.Time)
            .ToList();
    }

    private sealed record Window(DateTime Start, double MedianMs, (double Q1, double Q3) Iqr);
}
