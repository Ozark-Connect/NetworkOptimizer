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

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _stores.TryRemove(slug, out _);
        _history.TryRemove(slug, out _);
        return null;
    }
}
