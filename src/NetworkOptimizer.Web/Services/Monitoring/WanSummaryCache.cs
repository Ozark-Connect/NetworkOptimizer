using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Process-wide cache for the structural WAN summary list (interfaces, IPs, link
/// speeds, primary flag). This data is near-static - it only changes on reconfig or
/// failover - so it is shared across all request scopes instead of being rebuilt per
/// request.
///
/// The flow-map <c>/live</c> and <c>/snapshot</c> endpoints run on a fresh DI scope
/// per fetch, so a per-instance cache on the scoped <see cref="MonitoringPathView"/>
/// never engaged and every poll triggered a fresh <c>stat/device</c> round-trip to the
/// console (~1/sec with multiple maps open). Caching the structure on this singleton
/// collapses that to one build per TTL regardless of poll count. Live per-tick
/// throughput rates are layered on separately from the in-memory stats cache, so this
/// cache does not stale the rates the maps actually animate.
///
/// The cache is keyed by site slug: each site has its own gateway/WAN structure, so a
/// single shared slot would serve whichever site built first to every other site until
/// the TTL expired - a cross-site bleed on the LAN flow map's WAN clouds.
/// </summary>
public class WanSummaryCache
{
    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public IReadOnlyList<WanSummary>? Snapshot;
        public DateTime Expiry;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the cached structural WAN list for a site, rebuilding via
    /// <paramref name="build"/> when stale. Concurrent callers for the same site that
    /// arrive during a rebuild block on that site's gate and reuse the freshly built
    /// result (double-checked) rather than each hitting the console. A throwing
    /// <paramref name="build"/> (e.g. console unreachable) is propagated and never
    /// cached, so a transient blip does not poison the cache for a full TTL.
    /// </summary>
    public async Task<IReadOnlyList<WanSummary>> GetOrBuildAsync(
        string siteSlug,
        Func<CancellationToken, Task<List<WanSummary>>> build, TimeSpan ttl, CancellationToken ct)
    {
        var entry = _entries.GetOrAdd(siteSlug ?? string.Empty, _ => new Entry());

        if (entry.Snapshot != null && DateTime.UtcNow < entry.Expiry)
            return entry.Snapshot;

        await entry.Gate.WaitAsync(ct);
        try
        {
            if (entry.Snapshot != null && DateTime.UtcNow < entry.Expiry)
                return entry.Snapshot;

            var built = await build(ct);
            entry.Snapshot = built;
            entry.Expiry = DateTime.UtcNow.Add(ttl);
            return entry.Snapshot;
        }
        finally
        {
            entry.Gate.Release();
        }
    }
}
