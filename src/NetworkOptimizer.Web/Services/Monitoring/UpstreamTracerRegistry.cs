using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Per-site <see cref="UpstreamTracerService"/> instances, each with isolated discovery
/// state stored in its own site database - mirroring MonitoringCollectionRegistry. A
/// site's tracer runs the WAN-IP / L2-neighbor detection on that site's gateway (via its
/// gateway SSH, tunnelled through the agent for secondary sites) and the traceroute from
/// that site's "server" vantage: the local server on the default site, or the on-site
/// agent (running the same LocalProbeExecutor over the tunnel) on a secondary site, so the
/// path originates on the site's own network with first-hop logic identical to home.
/// </summary>
public class UpstreamTracerRegistry
{
    private readonly SiteConnectionRegistry _connections;
    private readonly GatewaySshRegistry _gatewaySsh;
    private readonly IspHealth.IspHealthRegistry _ispHealth;
    private readonly LocalProbeExecutor _localProbe;
    private readonly AgentProbeService _agentProbe;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly AsnResolutionService _asnResolution;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NetworkOptimizer.Audit.Services.IeeeOuiDatabase _ouiDb;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, UpstreamTracerService> _instances = new();

    public UpstreamTracerRegistry(
        SiteConnectionRegistry connections,
        GatewaySshRegistry gatewaySsh,
        IspHealth.IspHealthRegistry ispHealth,
        LocalProbeExecutor localProbe,
        AgentProbeService agentProbe,
        SiteDbContextFactory siteDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        IServiceScopeFactory scopeFactory,
        NetworkOptimizer.Audit.Services.IeeeOuiDatabase ouiDb,
        ILoggerFactory loggerFactory)
    {
        _connections = connections;
        _gatewaySsh = gatewaySsh;
        _ispHealth = ispHealth;
        _localProbe = localProbe;
        _agentProbe = agentProbe;
        _siteDbFactory = siteDbFactory;
        _dbFactory = dbFactory;
        _asnResolution = asnResolution;
        _scopeFactory = scopeFactory;
        _ouiDb = ouiDb;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The tracer for a site, created on first use.</summary>
    public UpstreamTracerService GetFor(string slug) => _instances.GetOrAdd(slug, s =>
    {
        var isDefault = s == SiteManagementService.DefaultSiteSlug;
        // Default site traces from the local server; a secondary site traces from its
        // on-site agent (analogous to the NO Server on the home LAN).
        IProbeExecutor traceExecutor = isDefault
            ? _localProbe
            : new AgentProbeExecutor(_agentProbe, s, _loggerFactory.CreateLogger<AgentProbeExecutor>());
        return new UpstreamTracerService(
            s,
            isDefault,
            _connections.GetFor(s),
            _gatewaySsh.GetFor(s),
            _ispHealth.GetFor(s),
            traceExecutor,
            _siteDbFactory,
            _dbFactory,
            _asnResolution,
            _scopeFactory,
            _ouiDb,
            _loggerFactory.CreateLogger<UpstreamTracerService>());
    });

    /// <summary>The default site's tracer.</summary>
    public UpstreamTracerService GetDefault() => GetFor(SiteManagementService.DefaultSiteSlug);
}
