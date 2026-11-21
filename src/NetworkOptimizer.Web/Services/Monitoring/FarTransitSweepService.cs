using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Runs <see cref="FarTransit.SweepAsync"/> for every enabled site every <see cref="TickInterval"/>.
/// The cadence is short so a transit target that is new or re-enabled is judged within about
/// <see cref="FarTransit.MinFloorPoints"/> aggregate windows, before it skews the transit figures for long.
/// One small Influx read per site per tick.
/// </summary>
public sealed class FarTransitSweepService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(10);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly ILogger<FarTransitSweepService> _logger;

    public FarTransitSweepService(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        MonitoringInfluxRegistry influxRegistry,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        ILogger<FarTransitSweepService> logger)
    {
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _influxRegistry = influxRegistry;
        _siteWorkGate = siteWorkGate;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Past startup, when the site registries and Influx clients have settled.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Far transit sweep failed");
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<(string Slug, bool IsDefault)> sites;
        try
        {
            await using var mainDb = await _dbFactory.CreateDbContextAsync(ct);
            sites = (await mainDb.Sites.AsNoTracking().Where(s => s.Enabled)
                    .Select(s => new { s.Slug, s.IsDefault }).ToListAsync(ct))
                .Select(s => (s.Slug, s.IsDefault))
                .ToList();
        }
        catch { sites = new(); }
        // Pre-multisite installs have no Sites rows; fall back to the default site.
        if (sites.Count == 0)
            sites = new() { (SiteManagementService.DefaultSiteSlug, true) };

        foreach (var (slug, isDefault) in sites)
        {
            if (ct.IsCancellationRequested) return;
            if (!_siteWorkGate.IsSiteOperational(slug)) continue;
            try
            {
                await using var db = isDefault
                    ? await _dbFactory.CreateDbContextAsync(ct)
                    : _siteDbFactory.CreateForSite(slug, isDefault: false);
                var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
                if (settings == null || !settings.Enabled) continue;
                await FarTransit.SweepAsync(db, _influxRegistry.GetFor(slug), _logger, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Could not judge far transit targets for site {Slug} this tick", slug);
            }
        }
    }
}
