using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// One <see cref="PathEpisodeWatcher"/> per site: target ids repeat across sites, and every site's
/// episodes live in its own database. Both latency write paths feed the owning site's watcher.
/// </summary>
public sealed class PathEpisodeRegistry : ISiteScopedRegistry
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, PathEpisodeWatcher> _instances = new();

    public PathEpisodeRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The watcher for a site, created on first use.</summary>
    public PathEpisodeWatcher GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<PathEpisodeWatcher>(_serviceProvider, s));

    /// <summary>Every site's watcher created so far, for the runner.</summary>
    public IReadOnlyCollection<PathEpisodeWatcher> All => _instances.Values.ToList();

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }
}
