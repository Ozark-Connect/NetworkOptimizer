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
    /// <summary>Each site's custom app rules once read from its database; absent until then.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<CustomAppRule>> _customApps = new();

    public LiveTrafficStore GetFor(string slug) => _stores.GetOrAdd(slug, s =>
    {
        var store = new LiveTrafficStore();
        if (_customApps.TryGetValue(s, out var rules)) store.SetCustomApps(rules);
        return store;
    });

    public FlowHistoryCache HistoryFor(string slug) => _history.GetOrAdd(slug, s =>
    {
        var cache = new FlowHistoryCache();
        if (_customApps.TryGetValue(s, out var rules)) cache.SetCustomApps(rules);
        return cache;
    });

    /// <summary>Whether the site's custom apps have been read since start (or since the site was evicted).</summary>
    public bool CustomAppsLoaded(string slug) => _customApps.ContainsKey(slug);

    /// <summary>The site's custom apps, in list order; empty before they are loaded.</summary>
    public IReadOnlyList<CustomAppRule> CustomAppsFor(string slug) =>
        _customApps.TryGetValue(slug, out var rules) ? rules : Array.Empty<CustomAppRule>();

    /// <summary>Replaces the site's custom apps: the next view of Live and History applies them.</summary>
    public void SetCustomApps(string slug, IReadOnlyList<CustomAppRule> rules)
    {
        _customApps[slug] = rules;
        GetFor(slug).SetCustomApps(rules);
        HistoryFor(slug).SetCustomApps(rules);
        foreach (var (key, cache) in _clientHistory)
            if (key.Slug == slug) cache.SetCustomApps(rules);
    }

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
        var cache = _clientHistory.GetOrAdd((slug, mac), _ =>
        {
            var created = new FlowHistoryCache();
            if (_customApps.TryGetValue(slug, out var rules)) created.SetCustomApps(rules);
            return created;
        });
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
        _customApps.TryRemove(slug, out _);
        return null;
    }
}
