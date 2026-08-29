using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// One <see cref="ClientUsageRollupService"/> per site, the default starting with the app and
/// non-default sites reconciled in as they are enabled. Same shape as WanDataUsageRegistry.
/// </summary>
public class ClientUsageRollupRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly ILogger<ClientUsageRollupRegistry> _logger;
    private readonly ConcurrentDictionary<string, ClientUsageRollupService> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public ClientUsageRollupRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        ILogger<ClientUsageRollupRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _siteWorkGate = siteWorkGate;
        _logger = logger;
    }

    private ClientUsageRollupService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<ClientUsageRollupService>(_serviceProvider, s));

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
                _logger.LogWarning(ex, "Per-site client usage rollup reconcile failed");
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
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_siteWorkGate.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            desired.Add(SiteManagementService.DefaultSiteSlug);

        await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.SystemSettings.FindAsync(new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
            if (bool.TryParse(setting?.Value, out var enabled) && enabled)
            {
                var slugs = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && !s.IsDefault)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
                foreach (var slug in slugs.Where(_siteWorkGate.IsSiteOperational))
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

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
        => async () =>
        {
            await StopInstanceAsync(slug, CancellationToken.None);
            _instances.TryRemove(slug, out _);
        };

    private async Task StartInstanceAsync(string slug, CancellationToken ct)
    {
        await _lifecycleLock.WaitAsync(ct);
        try
        {
            if (_running.Contains(slug)) return;
            var instance = GetFor(slug);
            await instance.StartAsync(CancellationToken.None);
            _running.Add(slug);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInstanceAsync(string slug, CancellationToken ct)
    {
        ClientUsageRollupService? instance = null;
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
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop client usage rollup for site {Slug}", slug);
        }
    }
}
