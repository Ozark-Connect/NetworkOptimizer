using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Per-site AP Agent deployment services, and the loop that supervises them.
///
/// Same shape as ModemMonitorRegistry: one instance per site, created on first use, activated by a
/// reconcile pass so an enabled site's access points are supervised without anyone opening its
/// pages first.
/// </summary>
public sealed class ApAgentRegistry : BackgroundService, ISiteScopedRegistry
{
    /// <summary>
    /// How often the site's access points are re-checked. The health poll is a single small HTTP
    /// request per AP, so this is the authoritative trigger and can afford to be frequent; the
    /// expensive part, a redeploy, is gated by the per-AP backoff behind it.
    /// </summary>
    private static readonly TimeSpan SuperviseInterval = TimeSpan.FromMinutes(2);

    /// <summary>Let the consoles connect before the first pass rather than racing startup.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly ILogger<ApAgentRegistry> _logger;
    private readonly ConcurrentDictionary<string, IApAgentDeploymentService> _instances = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the registry.</summary>
    public ApAgentRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        ILogger<ApAgentRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _siteWorkGate = siteWorkGate;
        _logger = logger;
    }

    /// <summary>The AP Agent service for a site, created on first use.</summary>
    public IApAgentDeploymentService GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            var siteSsh = _serviceProvider.GetRequiredService<UniFiSshRegistry>().GetFor(s);
            return ActivatorUtilities.CreateInstance<ApAgentDeploymentService>(_serviceProvider, siteSsh, s);
        });

    /// <summary>The default site's AP Agent service.</summary>
    public IApAgentDeploymentService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <summary>
    /// The owned instance. The registry hands out the gated interface so nothing depends on the
    /// implementation (architecture test A2), but supervision and disposal are its own business and
    /// deliberately absent from the caller-facing interface.
    /// </summary>
    private static ApAgentDeploymentService Owned(IApAgentDeploymentService service)
        => (ApAgentDeploymentService)service;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(stoppingToken);

                foreach (var service in _instances.Values)
                    await Owned(service).SuperviseAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AP Agent supervision pass failed");
            }

            try { await Task.Delay(SuperviseInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        foreach (var service in _instances.Values)
            Owned(service).DisposeOwned();
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
        => _instances.TryRemove(slug, out var service)
            ? () =>
            {
                Owned(service).DisposeOwned();
                return ValueTask.CompletedTask;
            }
            : null;

    private async Task ReconcileAsync(CancellationToken ct)
    {
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var db = await _mainDbFactory.CreateDbContextAsync(ct))
        {
            var setting = await db.SystemSettings.FindAsync(
                new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
            if (bool.TryParse(setting?.Value, out var multiSite) && multiSite)
            {
                var slugs = await db.Sites.AsNoTracking()
                    .Where(s => s.Enabled && !s.IsDefault)
                    .Select(s => s.Slug)
                    .ToListAsync(ct);
                foreach (var slug in slugs.Where(_siteWorkGate.IsSiteOperational))
                    enabled.Add(slug);
            }
        }

        if (_siteWorkGate.IsSiteOperational(SiteManagementService.DefaultSiteSlug))
            enabled.Add(SiteManagementService.DefaultSiteSlug);

        foreach (var slug in enabled)
            Owned(GetFor(slug)).Active = true;

        foreach (var (slug, service) in _instances)
        {
            if (!enabled.Contains(slug))
                Owned(service).Active = false;
        }
    }
}
