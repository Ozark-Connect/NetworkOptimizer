using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="ChannelMemoryCollectionService"/> instances and their
/// lifecycles. The default site's collector starts with the app; non-default collectors
/// start and stop on a reconcile cadence against the site registry. Each collector reads
/// its site's channel history/neighbor sightings and polls its site's console. Same
/// ownership pattern as MonitoringCollectionRegistry / WanDataUsageRegistry.
/// </summary>
public class ChannelMemoryRegistry : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<ChannelMemoryRegistry> _logger;
    private readonly ConcurrentDictionary<string, ChannelMemoryCollectionService> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public ChannelMemoryRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<ChannelMemoryRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    public ChannelMemoryCollectionService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<ChannelMemoryCollectionService>(_serviceProvider, s));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await StartInstanceAsync(SiteManagementService.DefaultSiteSlug, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Per-site channel memory reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        List<string> running;
        await _lifecycleLock.WaitAsync(cancellationToken);
        try { running = _running.ToList(); }
        finally { _lifecycleLock.Release(); }

        foreach (var slug in running)
            await StopInstanceAsync(slug, cancellationToken);
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SiteManagementService.DefaultSiteSlug
        };

        await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.SystemSettings.FindAsync(
                new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
            if (bool.TryParse(setting?.Value, out var enabled) && enabled)
            {
                var slugs = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && !s.IsDefault)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
                foreach (var slug in slugs)
                    desired.Add(slug);
            }
        }

        foreach (var slug in desired)
            await StartInstanceAsync(slug, ct);

        List<string> toStop;
        await _lifecycleLock.WaitAsync(ct);
        try { toStop = _running.Where(s => !desired.Contains(s)).ToList(); }
        finally { _lifecycleLock.Release(); }

        foreach (var slug in toStop)
            await StopInstanceAsync(slug, ct);
    }

    private async Task StartInstanceAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_running.Contains(slug)) return;
            var instance = GetFor(slug);
            await instance.StartAsync(CancellationToken.None);
            _running.Add(slug);
            if (slug != SiteManagementService.DefaultSiteSlug)
                _logger.LogInformation("Started channel memory collection for site {Slug}", slug);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInstanceAsync(string slug, CancellationToken ct)
    {
        ChannelMemoryCollectionService? instance = null;
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (!_running.Remove(slug)) return;
            _instances.TryGetValue(slug, out instance);
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (instance == null) return;
        try
        {
            await instance.StopAsync(ct);
            _logger.LogInformation("Stopped channel memory collection for site {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop channel memory collection for site {Slug}", slug);
        }
    }
}
