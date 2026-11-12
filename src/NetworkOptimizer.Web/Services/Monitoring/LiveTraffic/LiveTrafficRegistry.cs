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

    /// <summary>Each site's Live Traffic opt-out once read from its settings: true when turned off.</summary>
    private readonly ConcurrentDictionary<string, bool> _turnedOff = new();

    public LiveTrafficStore GetFor(string slug) => _stores.GetOrAdd(slug, s =>
    {
        var store = new LiveTrafficStore();
        if (_customApps.TryGetValue(s, out var rules)) store.SetCustomApps(rules);
        if (_turnedOff.TryGetValue(s, out var off)) store.SetTurnedOff(off);
        return store;
    });

    /// <summary>The site's setting key: unset or anything but "false" means Live Traffic is on.</summary>
    public const string EnabledKey = "liveTraffic.enabled";

    /// <summary>
    /// Reads the site's opt-out once, before anything grants a lease. A failed read counts as off,
    /// so a site that opted out is never captured on a database error; the next call reads again.
    /// </summary>
    public async Task<bool> IsTurnedOffAsync(ISystemSettingsService settings, string slug)
    {
        if (_turnedOff.TryGetValue(slug, out var known)) return known;
        string? value;
        try { value = await settings.GetAsync(slug, EnabledKey); }
        catch { return true; }
        // A save that landed while the read ran wins: the read saw the setting from before it.
        if (!_turnedOff.TryAdd(slug, value == "false")) return _turnedOff[slug];
        var off = _turnedOff[slug];
        GetFor(slug).SetTurnedOff(off);
        return off;
    }

    /// <summary>Applies the opt-out to the site's store; off drops what it holds at once.</summary>
    public void SetTurnedOff(string slug, bool off)
    {
        _turnedOff[slug] = off;
        GetFor(slug).SetTurnedOff(off);
    }

    public FlowHistoryCache HistoryFor(string slug) => _history.GetOrAdd(slug, s =>
    {
        var cache = new FlowHistoryCache();
        if (_customApps.TryGetValue(s, out var rules)) cache.SetCustomApps(rules);
        return cache;
    });

    private readonly ConcurrentDictionary<string, DateTime> _customAppsFailedAt = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _customAppsWrite = new();
    private static readonly TimeSpan CustomAppsRetry = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The site's custom apps still need reading from its database: not loaded yet, and no failed read
    /// in the last minute (a failing read must not cost every Live Traffic refresh a database call).
    /// </summary>
    public bool CustomAppsLoadDue(string slug, DateTime now) =>
        !_customApps.ContainsKey(slug)
        && (!_customAppsFailedAt.TryGetValue(slug, out var failed) || now - failed >= CustomAppsRetry);

    /// <summary>
    /// The rules a first read found. A write that published while the read ran wins: the read saw the
    /// table from before it.
    /// </summary>
    public void LoadCustomApps(string slug, IReadOnlyList<CustomAppRule> rules)
    {
        if (!_customApps.TryAdd(slug, rules)) return;
        _customAppsFailedAt.TryRemove(slug, out _);
        GetFor(slug).SetCustomApps(rules);
        HistoryFor(slug).SetCustomApps(rules);
        foreach (var (key, cache) in _clientHistory)
            if (key.Slug == slug) cache.SetCustomApps(rules);
    }

    public void CustomAppsLoadFailed(string slug, DateTime now) => _customAppsFailedAt[slug] = now;

    /// <summary>One writer per site: each write renumbers the whole list and publishes what it saved.</summary>
    public SemaphoreSlim CustomAppsWriteLock(string slug) => _customAppsWrite.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));

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
        _customAppsFailedAt.TryRemove(slug, out _);
        _turnedOff.TryRemove(slug, out _);
        return null;
    }
}
