namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// Drives every site's open Path Localization episodes: runs the rounds that are due, closes episodes
/// whose targets cleared, takes the recovery rounds, and prunes episodes past their retention.
/// </summary>
public sealed class PathEpisodeRunner : BackgroundService
{
    private static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(6);

    private readonly PathEpisodeRegistry _registry;
    private readonly ILogger<PathEpisodeRunner> _logger;
    private DateTime _lastPrune = DateTime.MinValue;
    // A site's tick still in flight: the next tick skips that site rather than wait on it.
    private readonly Dictionary<PathEpisodeWatcher, Task> _inFlight = new();

    public PathEpisodeRunner(PathEpisodeRegistry registry, ILogger<PathEpisodeRunner> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var prune = DateTime.UtcNow - _lastPrune >= PruneEvery;
                if (prune) _lastPrune = DateTime.UtcNow;
                // One site's slow traces must not hold up another's rounds, so no tick waits on a site.
                foreach (var done in _inFlight.Where(kv => kv.Value.IsCompleted).Select(kv => kv.Key).ToList())
                    _inFlight.Remove(done);
                foreach (var watcher in _registry.All.Where(w => !_inFlight.ContainsKey(w)))
                    _inFlight[watcher] = TickAsync(watcher, prune, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        try
        {
            await Task.WhenAll(_inFlight.Values);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task TickAsync(PathEpisodeWatcher watcher, bool prune, CancellationToken ct)
    {
        try
        {
            await watcher.TickAsync(ct);
            if (prune) await watcher.PruneAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Path Localization tick failed");
        }
    }
}
