namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>Where a target sits for the gate: the access layer, a path target that can trigger, or neither.</summary>
public enum GateRole
{
    /// <summary>Transit and other monitored hops: evidence, never a trigger or a control.</summary>
    Other,
    /// <summary>An AccessIsp target or the WAN L2 neighbor.</summary>
    Access,
    /// <summary>An Internet or Custom target: can trigger, or serve as a clean control.</summary>
    Path
}

/// <summary>One target of a WAN context as the gate sees it.</summary>
public sealed record GateTarget(string TargetId, GateRole Role, bool Degraded, bool Clean, double LossPct, double RttRiseMs);

/// <summary>The gate's answer for one WAN context.</summary>
/// <param name="Passed">Whether an episode should open.</param>
/// <param name="BlockedBy">Why not, for the debug log; null when it passed.</param>
/// <param name="Degraded">Degraded path targets, worst first (loss, then RTT rise).</param>
/// <param name="CleanControls">Clean path targets on the same context.</param>
public sealed record GateResult(bool Passed, string? BlockedBy, IReadOnlyList<GateTarget> Degraded, IReadOnlyList<GateTarget> CleanControls);

/// <summary>
/// Decides whether a degradation is path-specific - the only kind a reactive trace can localize.
/// Evaluated per WAN context; every condition must hold (spec 3.3):
/// <list type="number">
/// <item>The access layer is clean: no AccessIsp target or L2 neighbor is degraded.</item>
/// <item>At least one other Internet/Custom target is clean, and fewer than half of them are degraded.
/// More than that reads as site-wide, which the outage and loss pool already own.</item>
/// <item>The WAN is not saturated: loaded latency is the user's own link, which Adaptive SQM and
/// Loaded Latency cover.</item>
/// <item>No outage or dark window is open.</item>
/// </list>
/// </summary>
public static class PathEpisodeGate
{
    /// <param name="targets">The context's enabled targets.</param>
    /// <param name="utilization">Median WAN utilization (fraction of plan, worst direction) over the
    /// degradation, or null when unknown. Unknown load never blocks: it could not be shown to be the cause.</param>
    /// <param name="loadHighFraction">The localizer's self-inflicted threshold.</param>
    /// <param name="outageOpen">Whether an outage or dark window is open for the site.</param>
    public static GateResult Evaluate(IReadOnlyList<GateTarget> targets, double? utilization, double loadHighFraction, bool outageOpen)
    {
        var paths = targets.Where(t => t.Role == GateRole.Path).ToList();
        var degraded = paths.Where(t => t.Degraded)
            .OrderByDescending(t => t.LossPct).ThenByDescending(t => t.RttRiseMs)
            .ToList();
        var clean = paths.Where(t => t.Clean).ToList();

        string? blocked =
            degraded.Count == 0 ? "no path target is degraded"
            : outageOpen ? "an outage or dark window is open"
            : targets.Any(t => t.Role == GateRole.Access && t.Degraded) ? "the access layer is degraded"
            : clean.Count == 0 ? "no clean control on the same WAN"
            : degraded.Count * 2 >= paths.Count ? "half or more of the path targets are degraded (site-wide)"
            : utilization is double u && u >= loadHighFraction ? $"the WAN is loaded ({u * 100:0}% of plan)"
            : null;

        return new GateResult(blocked == null, blocked, degraded, clean);
    }
}
