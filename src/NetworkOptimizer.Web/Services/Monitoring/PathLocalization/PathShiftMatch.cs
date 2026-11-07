using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>One degraded target of an episode, as <see cref="PathShiftMatch"/> reads it.</summary>
/// <param name="Name">Display name, for the Path shift built from a step-only episode.</param>
public sealed record EpisodeTargetLevels(string TargetId, double? BaselineRttMs, double? PeakRttMs, double? PeakLossPct,
    double? BaselineJitterMs = null, double? PeakJitterMs = null, string? Name = null, int? AsnNumber = null);

/// <summary>
/// Whether a Path shift explains an episode. A sustained RTT step trips the degradation trigger the same
/// way congestion does, and the report already shows the step as a Path shift, so such an episode is that
/// shift's trace, not a second event. Every degraded target must sit on an up-step's new level: baseline at
/// the shift's before-level, peak at its after-level, and loss under the loss floor. A rise past the step,
/// or real loss, is more than that shift explains.
///
/// An episode the step detector has no shift for is still a route change when every degraded target rose
/// on RTT alone, with loss under the floor and jitter near its baseline (<see cref="IsStep"/>): queues widen
/// jitter, a longer route does not. It is reported as a Path shift built from the episode's own levels.
/// </summary>
public static class PathShiftMatch
{
    /// <summary>The shift that explains the episode, or null when any degraded target is not on one.</summary>
    /// <param name="openedAt">When the degradation began (the episode's open time), UTC.</param>
    public static PathShiftEvent? Explaining(DateTime openedAt, IReadOnlyList<EpisodeTargetLevels> degraded,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        if (degraded.Count == 0) return null;
        return Detected(openedAt, degraded, shifts, options) ?? StepShift(openedAt, degraded, options);
    }

    /// <summary>Each explained episode's shift, by episode id.</summary>
    public static Dictionary<int, PathShiftEvent> Match(IEnumerable<PathEpisodeView> episodes,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        var matched = new Dictionary<int, PathShiftEvent>();
        foreach (var e in episodes)
        {
            var levels = e.Degraded.Select(t => new EpisodeTargetLevels(t.TargetId, t.BaselineRttMs, t.PeakRttMs, t.PeakLossPct,
                t.BaselineJitterMs, t.PeakJitterMs, t.Name)).ToList();
            if (Explaining(e.OpenedAt, levels, shifts, options) is { } shift)
                matched[e.Id] = shift;
        }
        return matched;
    }

    /// <summary>
    /// A degraded target that rose on RTT alone: loss under the floor, and its worst window's jitter within
    /// <see cref="IspHealthOptions.PathStepJitterFactor"/> of baseline or
    /// <see cref="IspHealthOptions.PathStepJitterMinDeltaMs"/> over it. Without both jitter readings it is
    /// not judged a step.
    /// </summary>
    public static bool IsStep(EpisodeTargetLevels t, IspHealthOptions options) =>
        t.BaselineRttMs is double baseline && t.PeakRttMs is double peak && peak > baseline
        && (t.PeakLossPct ?? 0) < options.PathLossMinPct
        && t.BaselineJitterMs is double baseJitter && t.PeakJitterMs is double peakJitter
        && peakJitter <= Math.Max(baseJitter * options.PathStepJitterFactor, baseJitter + options.PathStepJitterMinDeltaMs);

    /// <summary>
    /// The Path shift a step-only episode stands for: an up-step at the episode's open, each degraded target
    /// moving from its baseline to its peak. Named, like a correlated shift, by the lowest before-level.
    /// Null unless every degraded target <see cref="IsStep"/>.
    /// </summary>
    public static PathShiftEvent? StepShift(DateTime openedAt, IReadOnlyList<EpisodeTargetLevels> degraded, IspHealthOptions options)
    {
        if (degraded.Count == 0 || !degraded.All(t => IsStep(t, options))) return null;
        var ordered = degraded.OrderBy(t => t.BaselineRttMs).ToList();
        var lead = ordered[0];
        return new PathShiftEvent
        {
            Time = openedAt,
            AsnNumber = lead.AsnNumber,
            AsnName = lead.Name,
            TargetId = lead.TargetId,
            TargetIds = ordered.Select(t => t.TargetId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            BeforeMedianMs = lead.BaselineRttMs!.Value,
            AfterMedianMs = lead.PeakRttMs!.Value,
            CorrelatedTargetCount = ordered.Count,
            IsDestination = true,
            Members = ordered.Count > 1
                ? ordered.Select(t => new PathShiftMember(t.Name ?? t.TargetId, t.BaselineRttMs!.Value, t.PeakRttMs!.Value,
                    new List<string> { t.TargetId })).ToList()
                : new List<PathShiftMember>()
        };
    }

    /// <summary>The step detector's shift that explains the episode, or null.</summary>
    private static PathShiftEvent? Detected(DateTime openedAt, IReadOnlyList<EpisodeTargetLevels> degraded,
        IReadOnlyList<PathShiftEvent> shifts, IspHealthOptions options)
    {
        if (shifts.Count == 0) return null;
        PathShiftEvent? first = null;
        foreach (var target in degraded)
        {
            var shift = ShiftFor(target, openedAt, shifts, options);
            if (shift == null) return null;
            first ??= shift;
        }
        return first;
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
