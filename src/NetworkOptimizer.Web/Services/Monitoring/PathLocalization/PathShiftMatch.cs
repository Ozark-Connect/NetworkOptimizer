using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>One degraded target of an episode, as <see cref="PathShiftMatch"/> reads it.</summary>
public sealed record EpisodeTargetLevels(string TargetId, double? BaselineRttMs, double? PeakRttMs, double? PeakLossPct);

/// <summary>
/// Whether a Path shift explains an episode. A sustained RTT step trips the degradation trigger the same
/// way congestion does, and the report already shows the step as a Path shift, so such an episode is that
/// shift's trace, not a second event. Every degraded target must sit on an up-step's new level: baseline at
/// the shift's before-level, peak at its after-level, and loss under the loss floor. A rise past the step,
/// or real loss, is more than a route change and stays congestion.
/// </summary>
public static class PathShiftMatch
{
    /// <summary>The shift that explains the episode, or null when any degraded target is not on one.</summary>
    /// <param name="openedAt">When the degradation began (the episode's open time), UTC.</param>
    public static PathShiftEvent? Explaining(DateTime openedAt, IReadOnlyList<EpisodeTargetLevels> degraded,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        if (degraded.Count == 0 || shifts.Count == 0) return null;
        PathShiftEvent? first = null;
        foreach (var target in degraded)
        {
            var shift = ShiftFor(target, openedAt, shifts, options);
            if (shift == null) return null;
            first ??= shift;
        }
        return first;
    }

    /// <summary>Each explained episode's shift, by episode id.</summary>
    public static Dictionary<int, PathShiftEvent> Match(IEnumerable<PathEpisodeView> episodes,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        var matched = new Dictionary<int, PathShiftEvent>();
        foreach (var e in episodes)
        {
            var levels = e.Degraded.Select(t => new EpisodeTargetLevels(t.TargetId, t.BaselineRttMs, t.PeakRttMs, t.PeakLossPct)).ToList();
            if (Explaining(e.OpenedAt, levels, shifts, options) is { } shift)
                matched[e.Id] = shift;
        }
        return matched;
    }

    private static PathShiftEvent? ShiftFor(EpisodeTargetLevels target, DateTime openedAt,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        if (target.BaselineRttMs is not double baseline || target.PeakRttMs is not double peak) return null;
        if ((target.PeakLossPct ?? 0) >= options.PathLossMinPct) return null;

        // The step is placed on a step-window boundary, so the degradation can begin up to a window ahead
        // of it. The latest shift on the target is the level it sits at; an earlier one was superseded.
        var latest = openedAt + TimeSpan.FromMinutes(options.StepWindowMinutes);
        var shift = shifts
            .Where(s => !s.IsUnreachable && s.Time <= latest && Covers(s, target.TargetId))
            .MaxBy(s => s.Time);
        if (shift == null) return null;

        var (before, after) = Levels(shift, target.TargetId);
        var step = after - before;
        if (step <= 0) return null;
        var tolerance = Math.Max(options.PathRttMinDeltaMs / 2, step * 0.25);
        return Math.Abs(baseline - before) <= tolerance && peak <= after + tolerance && peak >= before + step / 2
            ? shift
            : null;
    }

    private static bool Covers(PathShiftEvent s, string targetId) =>
        string.Equals(s.TargetId, targetId, StringComparison.OrdinalIgnoreCase)
        || s.TargetIds.Contains(targetId, StringComparer.OrdinalIgnoreCase)
        || s.Members.Any(m => m.TargetIds.Contains(targetId, StringComparer.OrdinalIgnoreCase));

    /// <summary>The target's own levels: a correlated shift quotes its representative, so a member's are its own.</summary>
    private static (double Before, double After) Levels(PathShiftEvent s, string targetId) =>
        s.Members.FirstOrDefault(m => m.TargetIds.Contains(targetId, StringComparer.OrdinalIgnoreCase)) is { } member
            ? (member.BeforeMedianMs, member.AfterMedianMs)
            : (s.BeforeMedianMs, s.AfterMedianMs);
}
