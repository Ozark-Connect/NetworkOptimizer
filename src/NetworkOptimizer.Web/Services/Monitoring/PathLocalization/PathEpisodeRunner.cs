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
                // Sites tick in parallel: one site's slow traces must not hold up another's rounds.
                await Task.WhenAll(_registry.All.Select(w => TickAsync(w, prune, stoppingToken)));
                if (prune) _lastPrune = DateTime.UtcNow;
            }
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
