namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Decides which pooled loss targets are flat-lined: dark for essentially the whole window while
/// their peers keep measuring. Such a target is blocked or retired rather than losing - it reports a
/// constant 100% that swamps the pooled mean the Packet Loss and Loaded Loss factors are graded on,
/// and drags Investigate's highlight with it. Pure and I/O-free so the one rule that matters here can
/// be tested on its own.
/// </summary>
public static class LossPoolFilter
{
    /// <summary>One pooled series with the identity the scorer's anonymous pool has lost.</summary>
    public record PoolEntry(string TargetId, IReadOnlyList<LatencySample> Samples);

    /// <summary>
    /// Target IDs to drop from the loss pool. The test is RELATIVE, never absolute: a target only
    /// counts as flat-lined while at least one peer is still measuring healthily. A real WAN outage
    /// takes every target to 100% at once, and an absolute rule would delete exactly the evidence of
    /// it and grade the window as clean. For the same reason the last surviving series is never
    /// dropped, and a target with too few samples to judge is left in.
    /// </summary>
    public static HashSet<string> FindFlatlined(IReadOnlyList<PoolEntry> pool, IspHealthOptions options)
    {
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (pool.Count < 2) return excluded;

        var stats = pool
            .Select(e =>
            {
                var losses = e.Samples.Where(s => s.LossPercent.HasValue).Select(s => s.LossPercent!.Value).ToList();
                var darkFraction = losses.Count > 0
                    ? (double)losses.Count(l => l >= options.LossPoolFlatlineLossPct) / losses.Count
                    : 0.0;
                return (Entry: e, Count: losses.Count, DarkFraction: darkFraction, Mean: losses.Count > 0 ? losses.Average() : 0.0);
            })
            .ToList();

        // A peer counts as still measuring when it has enough samples to judge and its mean loss sits
        // in ordinary territory. Without one of these the window is not "one dead target among healthy
        // peers", it is a path-wide event, and nothing is excluded.
        var hasHealthyPeer = stats.Any(s =>
            s.Count >= options.LossPoolFlatlineMinSamples && s.Mean <= options.LossPoolHealthyPeerMaxLossPct);
        if (!hasHealthyPeer) return excluded;

        foreach (var s in stats)
        {
            if (s.Count < options.LossPoolFlatlineMinSamples) continue;
            if (s.DarkFraction < options.LossPoolFlatlineFraction) continue;
            excluded.Add(s.Entry.TargetId);
        }

        // Never empty the pool, however the thresholds land.
        if (excluded.Count >= pool.Count) excluded.Clear();
        return excluded;
    }
}
