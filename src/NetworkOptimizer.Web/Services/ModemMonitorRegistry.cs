using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.CellularModemProviders;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Owns all per-site modem monitor instances (cable modem; external ONT and
/// cellular join as they split per site) and their lifecycles. Instances carry
/// their own poll timers from construction, so the reconcile loop only has to
/// make sure an instance EXISTS for every enabled site and flip its Active
/// flag: the default site's monitors always run (identical to the old
/// singletons), non-default monitors poll only while their site is enabled,
/// and a disabled site's instance goes quiet without a restart. Scoped
/// resolution forwards to the current site's bundle so the Monitoring page,
/// stats panels, and chart endpoints show the site being viewed.
/// </summary>
public class ModemMonitorRegistry : BackgroundService
{
    /// <summary>One site's modem monitors.</summary>
    public sealed record SiteModemMonitors(
        CableModemMonitorService CableModem,
        OntMonitorService Ont,
        CellularModemService Cellular);

    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceProvider _serviceProvider;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly ILogger<ModemMonitorRegistry> _logger;
    private readonly ConcurrentDictionary<string, SiteModemMonitors> _instances = new(StringComparer.OrdinalIgnoreCase);

    public ModemMonitorRegistry(
        IServiceProvider serviceProvider,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        ILogger<ModemMonitorRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _mainDbFactory = mainDbFactory;
        _logger = logger;
    }

    /// <summary>
    /// The modem monitors for a site, created on first use. Creation starts the
    /// poll timers, but non-default instances stay inactive until the reconcile
    /// pass confirms their site is enabled.
    /// </summary>
    public SiteModemMonitors GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            // The cellular provider set is built per site: the qmicli provider
            // carries the site's device SSH service (which applies tunnel routing
            // itself); the HTTP and per-modem-SSH providers are context-driven,
            // so fresh instances per site are cheap and uniform.
            var siteSsh = _serviceProvider.GetRequiredService<UniFiSshRegistry>().GetFor(s);
            var cellularProviders = new List<ICellularModemProvider>
            {
                ActivatorUtilities.CreateInstance<QmicliModemProvider>(_serviceProvider, siteSsh),
                ActivatorUtilities.CreateInstance<NetgearNighthawkHotspotProvider>(_serviceProvider),
                ActivatorUtilities.CreateInstance<QuectelAtModemProvider>(_serviceProvider),
            };
            return new SiteModemMonitors(
                ActivatorUtilities.CreateInstance<CableModemMonitorService>(_serviceProvider, s),
                ActivatorUtilities.CreateInstance<OntMonitorService>(_serviceProvider, s),
                ActivatorUtilities.CreateInstance<CellularModemService>(
                    _serviceProvider, s, siteSsh, (IEnumerable<ICellularModemProvider>)cellularProviders));
        });

    /// <summary>The default site's modem monitors.</summary>
    public SiteModemMonitors GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The default site's monitors run from startup, matching the old eager
        // singleton resolution that started the poll timers at app launch.
        GetDefault();

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
                _logger.LogWarning(ex, "Per-site modem monitor reconcile failed");
            }

            try { await Task.Delay(ReconcileInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        foreach (var bundle in _instances.Values)
        {
            bundle.CableModem.Dispose();
            bundle.Ont.Dispose();
            bundle.Cellular.Dispose();
        }
    }

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
                foreach (var slug in slugs)
                    enabled.Add(slug);
            }
        }

        // Ensure instances exist for enabled sites so their modems poll without
        // anyone visiting the site's pages first.
        foreach (var slug in enabled)
            SetActive(GetFor(slug), true);

        // Instances for sites that got disabled (or removed) go quiet but stick
        // around for status reads; re-enabling flips them back on.
        foreach (var (slug, bundle) in _instances)
        {
            if (slug == SiteManagementService.DefaultSiteSlug) continue;
            if (!enabled.Contains(slug))
                SetActive(bundle, false);
        }
    }

    private void SetActive(SiteModemMonitors bundle, bool active)
    {
        if (bundle.CableModem.Active != active)
        {
            bundle.CableModem.Active = active;
            _logger.LogInformation("Cable modem monitoring {State} for a site", active ? "activated" : "deactivated");
        }
        if (bundle.Ont.Active != active)
        {
            bundle.Ont.Active = active;
            _logger.LogInformation("External ONT monitoring {State} for a site", active ? "activated" : "deactivated");
        }
        if (bundle.Cellular.Active != active)
        {
            bundle.Cellular.Active = active;
            _logger.LogInformation("Cellular modem monitoring {State} for a site", active ? "activated" : "deactivated");
        }
    }
}
