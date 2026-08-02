using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site InfluxDB clients (decision D1: bucket-per-site in one org). The
/// registry owns every instance, including the default site's: scoped
/// resolution of MonitoringInfluxClient forwards to the current site's client,
/// so chart endpoints and pages read the right site's buckets transparently;
/// singleton consumers (collection agent, modem monitors, ISP health) inject
/// the registry and pin GetDefault(). Same ownership pattern as
/// SiteConnectionRegistry: the registry disposes what it creates.
/// </summary>
public class MonitoringInfluxRegistry : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MonitoringInfluxRegistry> _logger;
    private readonly ConcurrentDictionary<string, MonitoringInfluxClient> _clients = new();

    public MonitoringInfluxRegistry(IServiceProvider serviceProvider, ILogger<MonitoringInfluxRegistry> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    /// <summary>The Influx client for a site, created on first use.</summary>
    public MonitoringInfluxClient GetFor(string slug) =>
        _clients.GetOrAdd(slug, s => ActivatorUtilities.CreateInstance<MonitoringInfluxClient>(
            _serviceProvider,
            // Empty slug = the default client, configured from the main
            // database with the unprefixed bucket names.
            s == SiteManagementService.DefaultSiteSlug ? "" : s));

    /// <summary>The default site's client.</summary>
    public MonitoringInfluxClient GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);

    /// <summary>
    /// Rebuilds every already-created client from current settings. Needed when the
    /// shared connection (main's URL/org/token) changes: non-default clients derive
    /// their connection from main at configure time, so reconfiguring only the default
    /// client would leave existing site clients on the stale connection until restart.
    /// Clients not yet created pick up the fresh settings on first use.
    ///
    /// Best-effort per client: reconfiguring reads that site's own database, so one site
    /// whose database is unreadable must not abort the caller. This runs at the end of
    /// InfluxDB provisioning, after the buckets and token already exist, and letting it
    /// throw reported a failure for work that had in fact succeeded.
    /// </summary>
    public async Task ReconfigureAllAsync(CancellationToken ct = default)
    {
        foreach (var (slug, client) in _clients)
        {
            try
            {
                await client.ReconfigureAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reconfigure the InfluxDB client for site {Slug}", slug);
            }
        }
    }

    /// <summary>
    /// Drops a removed site's client. Without this the registry keeps an entry pointing at a
    /// database file that no longer exists, and every later ReconfigureAllAsync trips over it.
    /// </summary>
    public async Task RemoveAsync(string slug)
    {
        if (_clients.TryRemove(slug, out var client))
            await client.DisposeOwnedAsync();
    }

    public async ValueTask DisposeAsync()
    {
        // The clients' own DisposeAsync is a no-op (they're scope-forwarded and must
        // survive request/circuit scope disposal); the registry owns real teardown.
        foreach (var client in _clients.Values)
            await client.DisposeOwnedAsync();
        _clients.Clear();
        GC.SuppressFinalize(this);
    }
}
