namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// The propagation rule that separates a forwarding bottleneck from ICMP deprioritization, shared by
/// the localizer (monitored hops) and Path Localization (every hop of a reactive trace): an elevation
/// at a hop is real only when it persists at every hop after it, through to the end of the path.
///
/// Transit routers rate-limit and deprioritize the replies they generate themselves, so a hop's own
/// loss or delay is not forwarding evidence until the hops beyond it carry it too. A clean hop
/// between two elevated ones breaks the run, which keeps a noisy near hop from being blamed for a
/// deeper bottleneck.
/// </summary>
public static class ElevatedRun
{
    /// <summary>
    /// Index of the shallowest hop in the unbroken run of elevated hops that ends the path, or null
    /// when the deepest hop that says anything is clean (no run ends the path).
    /// </summary>
    /// <param name="count">Hops on the path, nearest first.</param>
    /// <param name="elevated">
    /// Whether the hop at an index is elevated. Null for a hop that said nothing (a <c>*</c> in a
    /// trace), which neither breaks nor extends the run.
    /// </param>
    public static int? Onset(int count, Func<int, bool?> elevated)
    {
        int? onset = null;
        for (var i = count - 1; i >= 0; i--)
        {
            var e = elevated(i);
            if (e == null) continue;
            if (!e.Value) break;
            onset = i;
        }
        return onset;
    }
}
