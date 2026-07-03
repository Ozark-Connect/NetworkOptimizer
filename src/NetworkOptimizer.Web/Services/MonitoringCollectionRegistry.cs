using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site <see cref="MonitoringCollectionAgent"/> instances and their
/// lifecycles. The default site's instance starts with the app, exactly as the
/// old single hosted service did; non-default instances start and stop on a
/// reconcile cadence against the site registry, so adding, enabling, or
/// disabling a site takes effect without a restart. Scoped resolution of
/// MonitoringCollectionAgent forwards to the current site's instance (the
/// Monitoring page's SNMP status panel reads whichever site it is showing).
/// Same ownership pattern as SiteConnectionRegistry / MonitoringInfluxRegistry.
/// </summary>
public class MonitoringCollectionRegistry : BackgroundService
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<MonitoringCollectionRegistry> _logger;
    private readonly ConcurrentDictionary<string, MonitoringCollectionAgent> _instances = new(StringComparer.OrdinalIgnoreCase);
    // Slugs whose instance's collection loops are currently running. Guarded by
    // _lifecycleLock so a reconcile pass and shutdown never race a start/stop.
    private readonly HashSet<string> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public MonitoringCollectionRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<MonitoringCollectionRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// The collection agent for a site, created on first use. Creation does not
    /// start the collection loops - only the reconcile pass does that, so viewing
    /// a disabled site's Monitoring page never kicks off polling for it.
    /// </summary>
    public MonitoringCollectionAgent GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
            ActivatorUtilities.CreateInstance<MonitoringCollectionAgent>(_serviceProvider, s));

    /// <summary>The default site's collection agent.</summary>
    public MonitoringCollectionAgent GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The default site collects from startup, unconditionally - identical to the
        // pre-multi-site behavior where the agent itself was the hosted service.
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
                _logger.LogWarning(ex, "Per-site collection reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop the reconcile loop first so it can't restart an instance mid-shutdown,
        // then stop every running site instance.
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
            // CancellationToken.None: the token passed here becomes linked into the
            // instance's stopping token, and this one only guards startup.
            await instance.StartAsync(CancellationToken.None);
            _running.Add(slug);
            if (slug != SiteManagementService.DefaultSiteSlug)
                _logger.LogInformation("Started monitoring collection for site {Slug}", slug);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopInstanceAsync(string slug, CancellationToken ct)
    {
        MonitoringCollectionAgent? instance = null;
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
            _logger.LogInformation("Stopped monitoring collection for site {Slug}", slug);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop monitoring collection for site {Slug}", slug);
        }
    }
}
