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
/// </summary>
public class WanSummaryCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<WanSummary>? _snapshot;
    private DateTime _expiry;

    /// <summary>
    /// Returns the cached structural WAN list, rebuilding via <paramref name="build"/>
    /// when stale. Concurrent callers that arrive during a rebuild block on the gate and
    /// reuse the freshly built result (double-checked) rather than each hitting the
    /// console. A throwing <paramref name="build"/> (e.g. console unreachable) is
    /// propagated and never cached, so a transient blip does not poison the cache for a
    /// full TTL.
    /// </summary>
    public async Task<IReadOnlyList<WanSummary>> GetOrBuildAsync(
        Func<CancellationToken, Task<List<WanSummary>>> build, TimeSpan ttl, CancellationToken ct)
    {
        if (_snapshot != null && DateTime.UtcNow < _expiry)
            return _snapshot;

        await _gate.WaitAsync(ct);
        try
        {
            if (_snapshot != null && DateTime.UtcNow < _expiry)
                return _snapshot;

            var built = await build(ct);
            _snapshot = built;
            _expiry = DateTime.UtcNow.Add(ttl);
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }
}
