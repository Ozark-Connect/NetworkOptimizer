using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>
/// One <see cref="LiveTrafficStore"/> and one <see cref="FlowHistoryCache"/> per site. The tunnel
/// records into the owning site's store; the scoped <see cref="ILiveTrafficService"/> reads the site
/// in context. In-memory only, so eviction needs no teardown.
/// </summary>
public sealed class LiveTrafficRegistry : ISiteScopedRegistry
{
    private readonly ConcurrentDictionary<string, LiveTrafficStore> _stores = new();
    private readonly ConcurrentDictionary<string, FlowHistoryCache> _history = new();

    public LiveTrafficStore GetFor(string slug) => _stores.GetOrAdd(slug, _ => new LiveTrafficStore());

    public FlowHistoryCache HistoryFor(string slug) => _history.GetOrAdd(slug, _ => new FlowHistoryCache());

    /// <summary>One client's History, read with the console's MAC filters, while the site's own is not loaded.</summary>
    private readonly ConcurrentDictionary<(string Slug, string Mac), FlowHistoryCache> _clientHistory = new();

    /// <summary>
    /// A client's own History cache, touched for the viewer: the site's whole hour can be many pages,
    /// one client's records are usually one. Caches nobody asked for within the viewer timeout go.
    /// </summary>
    public FlowHistoryCache ClientHistoryFor(string slug, string mac, string viewerId, DateTime now)
    {
        foreach (var (key, idle) in _clientHistory)
            if (idle.Idle(now)) _clientHistory.TryRemove(key, out _);
        var cache = _clientHistory.GetOrAdd((slug, mac), _ => new FlowHistoryCache());
        cache.Touch(viewerId, now);
        return cache;
    }

    /// <summary>A viewer left: drops it from the site's client caches, and any cache it was the last viewer of.</summary>
    public void ReleaseClientHistory(string slug, string viewerId, DateTime now)
    {
        foreach (var (key, cache) in _clientHistory)
            if (key.Slug == slug && cache.Release(viewerId, now)) _clientHistory.TryRemove(key, out _);
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _stores.TryRemove(slug, out _);
        _history.TryRemove(slug, out _);
        foreach (var key in _clientHistory.Keys.Where(k => k.Slug == slug).ToList())
            _clientHistory.TryRemove(key, out _);
        return null;
    }
}
