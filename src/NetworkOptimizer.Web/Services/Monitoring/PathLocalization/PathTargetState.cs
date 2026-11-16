namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>A target's health against its own baseline, worst last, as the Path Map draws it.</summary>
public enum PathTargetHealth
{
    /// <summary>No baseline yet, or not heard from recently.</summary>
    Unknown = 0,
    /// <summary>The latest window is clean.</summary>
    Normal = 1,
    /// <summary>The latest window is over baseline, not yet sustained.</summary>
    Elevated = 2,
    /// <summary>Over baseline for enough windows in a row (<see cref="TargetDegradation.Degraded"/>).</summary>
    Degraded = 3,
    /// <summary>The latest window lost half its probes or more.</summary>
    Unreachable = 4,
}

/// <summary>A target's state, copied out of its <see cref="TargetDegradation"/> under the watcher's lock.</summary>
/// <param name="Health">The state, by the Path Localization thresholds.</param>
/// <param name="BaselineRttMs">The target's usual RTT (24 h median of window medians).</param>
/// <param name="RttMs">The latest window's median RTT.</param>
/// <param name="LossPct">The latest window's loss.</param>
/// <param name="ChangedAt">When the target last went bad or recovered: degraded since, else cleared at.</param>
/// <param name="WindowEnd">When the latest closed window ended.</param>
public sealed record PathTargetState(PathTargetHealth Health, double? BaselineRttMs, double? RttMs, double? LossPct,
    DateTime? ChangedAt, DateTime? WindowEnd)
{
    /// <summary>How far the latest RTT sits over the baseline, in ms; null when either is unknown.</summary>
    public double? RiseMs => RttMs is double r && BaselineRttMs is double b ? r - b : null;

    /// <summary>The state as of now. Caller holds the watcher's lock.</summary>
    internal static PathTargetState From(TargetDegradation t, DateTime now)
    {
        var last = t.LastWindow;
        var end = last == null ? (DateTime?)null : last.Start + t.WindowLength;
        var changed = t.Degraded ? t.DegradedSince : t.ClearedAt;
        PathTargetHealth health;
        if (!t.IsFresh(now) || last == null) health = PathTargetHealth.Unknown;
        else if (last.Probes > 0 && last.LossPct >= TargetDegradation.OutageLossPct) health = PathTargetHealth.Unreachable;
        else if (!t.HasBaseline) health = PathTargetHealth.Unknown;
        else if (t.Degraded) health = PathTargetHealth.Degraded;
        else if (last.Judged && last.Signal != DegradationSignal.None) health = PathTargetHealth.Elevated;
        else health = PathTargetHealth.Normal;
        return new PathTargetState(health, t.BaselineRttMs, last?.MedianRttMs, last?.Probes > 0 ? last.LossPct : null, changed, end);
    }

    /// <summary>
    /// The worst a target was over a past window, from its closed evaluation windows: Unreachable for a
    /// window that lost half its probes, Degraded for a run of <paramref name="degradedRun"/> windows
    /// over baseline, Elevated for one, Normal when every judged window was clean.
    /// </summary>
    public static PathTargetState FromWindows(IReadOnlyList<DegradationWindow> windows, int degradedRun, double? baselineRttMs)
    {
        if (windows.Count == 0) return new PathTargetState(PathTargetHealth.Unknown, baselineRttMs, null, null, null, null);
        var worst = windows.OrderByDescending(w => w.LossPct).First();
        var health = PathTargetHealth.Unknown;
        if (windows.Any(w => w.Probes > 0 && w.LossPct >= TargetDegradation.OutageLossPct)) health = PathTargetHealth.Unreachable;
        else
        {
            int run = 0, longest = 0;
            foreach (var w in windows)
            {
                run = w.Judged && w.Signal != DegradationSignal.None ? run + 1 : 0;
                longest = Math.Max(longest, run);
            }
            if (longest >= Math.Max(1, degradedRun)) health = PathTargetHealth.Degraded;
            else if (longest > 0) health = PathTargetHealth.Elevated;
            else if (windows.Any(w => w.Judged)) health = PathTargetHealth.Normal;
        }
        var flagged = windows.Where(w => w.Judged && w.Signal != DegradationSignal.None && w.MedianRttMs.HasValue).ToList();
        var rtt = flagged.Count > 0 ? flagged.Max(w => w.MedianRttMs) : windows.LastOrDefault(w => w.MedianRttMs.HasValue)?.MedianRttMs;
        return new PathTargetState(health, baselineRttMs, rtt, worst.Probes > 0 ? worst.LossPct : null, null, null);
    }
}
