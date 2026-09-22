using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Auditing;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.Monitoring.HealthChecks;

/// <summary>
/// Owns one <see cref="HealthCheckRunner"/> per operational site and keeps it running. Same
/// reconcile shape as <see cref="MonitoringCollectionRegistry"/>: the default site starts with the
/// app, other sites start and stop as they are enabled and licensed.
/// </summary>
public class HealthCheckRegistry : BackgroundService, ISiteScopedRegistry
{
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly ILogger<HealthCheckRegistry> _logger;
    private readonly ConcurrentDictionary<string, HealthCheckRunner> _instances = new(StringComparer.OrdinalIgnoreCase);

    public HealthCheckRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        Licensing.LicenseStateService licenseState,
        ILogger<HealthCheckRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _licenseState = licenseState;
        _logger = logger;
    }

    /// <summary>The runner for a site, created on first use. Creation does not start it.</summary>
    public HealthCheckRunner GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            var sp = _serviceProvider;
            var bus = new SiteAlertEventBus(sp.GetRequiredService<IAlertEventBus>(), s);
            return new HealthCheckRunner(
                s,
                sp.GetRequiredService<SiteDbContextFactory>(),
                sp.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>(),
                sp.GetRequiredService<GatewaySshRegistry>().GetFor(s),
                sp.GetRequiredService<UniFiSshRegistry>().GetFor(s),
                sp.GetRequiredService<SiteConnectionRegistry>().GetFor(s),
                sp.GetRequiredService<MonitoringInfluxRegistry>().GetFor(s),
                bus,
                sp.GetRequiredService<RolloutSuppressionRegistry>(),
                sp.GetRequiredService<IAuditLogger>(),
                sp.GetRequiredService<ILogger<HealthCheckRunner>>());
        });

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        if (!_instances.TryRemove(slug, out var runner)) return null;
        return () => new ValueTask(runner.StopAsync());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                _logger.LogDebug(ex, "Health check reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        foreach (var runner in _instances.Values)
            await runner.StopAsync();
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_licenseState.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
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
                foreach (var slug in slugs.Where(_licenseState.IsSiteOperational))
                    desired.Add(slug);
            }
        }

        foreach (var slug in desired)
            GetFor(slug).Start();

        foreach (var (slug, runner) in _instances)
        {
            if (!desired.Contains(slug))
                await runner.StopAsync();
        }
    }
}
