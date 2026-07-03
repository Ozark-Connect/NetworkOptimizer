using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Per-site speed test services, created lazily as a bundle per slug: the
/// site's network path analyzer (topology from that site's console), the
/// topology snapshot capturer, and the client speed test service (results in
/// that site's database, enrichment against that site's network). Scoped
/// resolution of ClientSpeedTestService / INetworkPathAnalyzer-family types
/// forwards to the current site's bundle where registered; singleton consumers
/// inject this registry and pin GetDefault(). Same ownership pattern as
/// SiteConnectionRegistry.
/// </summary>
public class SpeedTestServiceRegistry
{
    /// <summary>One site's speed test service bundle.</summary>
    public sealed record SiteSpeedTestServices(
        NetworkPathAnalyzer PathAnalyzer,
        TopologySnapshotService Snapshots,
        ClientSpeedTestService ClientSpeedTest,
        GatewayWanSpeedTestService GatewayWan,
        UwnSpeedTestService Uwn);

    private readonly IServiceProvider _serviceProvider;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly ConcurrentDictionary<string, SiteSpeedTestServices> _instances = new();

    public SpeedTestServiceRegistry(IServiceProvider serviceProvider, SiteConnectionRegistry siteConnections)
    {
        _serviceProvider = serviceProvider;
        _siteConnections = siteConnections;
    }

    /// <summary>The speed test bundle for a site, created on first use.</summary>
    public SiteSpeedTestServices GetFor(string slug) =>
        _instances.GetOrAdd(slug, s =>
        {
            var connection = _siteConnections.GetFor(s);
            // Each site's path analyzer gets its own MemoryCache: the analyzer
            // caches topology under fixed keys, so a shared cache would leak
            // one site's topology into another site's path analysis.
            var pathAnalyzer = new NetworkPathAnalyzer(
                connection,
                new MemoryCache(new MemoryCacheOptions()),
                _serviceProvider.GetRequiredService<ILoggerFactory>());
            var snapshots = ActivatorUtilities.CreateInstance<TopologySnapshotService>(
                _serviceProvider, connection, pathAnalyzer);
            var clientSpeedTest = ActivatorUtilities.CreateInstance<ClientSpeedTestService>(
                _serviceProvider, s, pathAnalyzer, snapshots);
            var gatewayWan = ActivatorUtilities.CreateInstance<GatewayWanSpeedTestService>(
                _serviceProvider, s, (INetworkPathAnalyzer)pathAnalyzer);
            // Non-default UWN instances serve result CRUD only; running the local
            // binary is refused for them (it measures this server's own WAN).
            var uwn = ActivatorUtilities.CreateInstance<UwnSpeedTestService>(
                _serviceProvider, s, (INetworkPathAnalyzer)pathAnalyzer);
            return new SiteSpeedTestServices(pathAnalyzer, snapshots, clientSpeedTest, gatewayWan, uwn);
        });

    /// <summary>The default site's speed test bundle.</summary>
    public SiteSpeedTestServices GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
