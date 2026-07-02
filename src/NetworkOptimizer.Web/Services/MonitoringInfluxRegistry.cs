using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site InfluxDB clients (decision D1: bucket-per-site in one org). The
/// default site keeps the DI singleton <see cref="MonitoringInfluxClient"/>
/// configured from the main database; non-default sites get lazily created
/// clients configured from their own site database, whose MonitoringSettings
/// carry slug-prefixed bucket names. Same ownership pattern as
/// SiteConnectionRegistry: the registry creates and disposes what it creates.
/// </summary>
public class MonitoringInfluxRegistry : IAsyncDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, MonitoringInfluxClient> _clients = new();

    public MonitoringInfluxRegistry(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>The Influx client for a site, created on first use.</summary>
    public MonitoringInfluxClient GetFor(string slug) =>
        slug == SiteManagementService.DefaultSiteSlug
            ? GetDefault()
            : _clients.GetOrAdd(slug, s =>
                ActivatorUtilities.CreateInstance<MonitoringInfluxClient>(_serviceProvider, s));

    /// <summary>The default site's client (the existing DI singleton).</summary>
    public MonitoringInfluxClient GetDefault() =>
        _serviceProvider.GetRequiredService<MonitoringInfluxClient>();

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients.Values)
            await client.DisposeAsync();
        _clients.Clear();
        GC.SuppressFinalize(this);
    }
}
