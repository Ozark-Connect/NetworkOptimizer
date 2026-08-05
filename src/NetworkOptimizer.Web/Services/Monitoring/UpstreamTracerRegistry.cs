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
public class UpstreamTracerRegistry : ISiteScopedRegistry
{
    private readonly SiteConnectionRegistry _connections;
    private readonly GatewaySshRegistry _gatewaySsh;
    private readonly IspHealth.IspHealthRegistry _ispHealth;
    private readonly LocalProbeExecutor _localProbe;
    private readonly AgentProbeService _agentProbe;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly AsnResolutionService _asnResolution;
    private readonly SiteAgentCoverage _agentCoverage;
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
        SiteAgentCoverage agentCoverage,
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
        _agentCoverage = agentCoverage;
        _scopeFactory = scopeFactory;
        _ouiDb = ouiDb;
        _loggerFactory = loggerFactory;
    }

    /// <summary>The tracer for a site, created on first use.</summary>
    public UpstreamTracerService GetFor(string slug) => _instances.GetOrAdd(slug, s =>
    {
        var isDefault = s == SiteManagementService.DefaultSiteSlug;
        // A secondary site always traces from its on-site agent (analogous to the NO Server on the
        // home LAN) - tracing it from here would attribute this server's path to that site. The
        // default site traces locally unless it is configured for its agent to cover it, which is
        // the off-site-server case.
        //
        // Resolved per run rather than baked in here: this registry caches one tracer per site for
        // the life of the process, so a flag changed afterwards would otherwise never be seen.
        var agentExecutor = new AgentProbeExecutor(_agentProbe, s, _loggerFactory.CreateLogger<AgentProbeExecutor>());
        Func<IProbeExecutor> traceExecutor = () =>
            !isDefault || _agentCoverage.AgentOwnsPathMeasurement(s)
                ? agentExecutor
                : _localProbe;
        return new UpstreamTracerService(
            s,
            isDefault,
            _connections.GetFor(s),
            _gatewaySsh.GetFor(s),
            _ispHealth.GetFor(s),
            _ispHealth,
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

    /// <summary>
    /// A tracer that discovers ONE WAN context's upstream: it traces the WAN the context names
    /// rather than the configured primary, binds every probe the way that context's targets are
    /// probed, and stamps what it commits with both the WAN and the context.
    ///
    /// Deliberately not cached, unlike the per-site tracers above. A context's agent or bind can
    /// be changed in the card at any moment, and a cached instance would keep tracing the old
    /// one for the life of the process; nothing polls a context tracer's state either, since the
    /// re-discovery service starts, awaits, and commits each run in turn.
    /// </summary>
    /// <param name="slug">Site the context belongs to.</param>
    /// <param name="context">The context to discover for; it must already name a WAN.</param>
    public UpstreamTracerService GetForContext(string slug, WanContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(context.WanInterface))
            throw new ArgumentException("A WAN context can only be discovered once it names the WAN it measures.", nameof(context));

        var isDefault = slug == SiteManagementService.DefaultSiteSlug;
        // The context's own agent runs its probes when it has one - that agent is the thing
        // sitting behind the WAN being measured. With no agent, the context is a source-IP one
        // the gateway policy-routes, so it runs from the same vantage the site's primary uses.
        var executor = context.AgentId is int agentId
            ? new AgentProbeExecutor(_agentProbe, slug, _loggerFactory.CreateLogger<AgentProbeExecutor>(), agentId)
            : new AgentProbeExecutor(_agentProbe, slug, _loggerFactory.CreateLogger<AgentProbeExecutor>());
        Func<IProbeExecutor> traceExecutor = context.AgentId != null
            ? () => executor
            : () => !isDefault || _agentCoverage.AgentOwnsPathMeasurement(slug) ? executor : _localProbe;

        return new UpstreamTracerService(
            slug,
            isDefault,
            _connections.GetFor(slug),
            _gatewaySsh.GetFor(slug),
            _ispHealth.GetFor(slug),
            _ispHealth,
            traceExecutor,
            _siteDbFactory,
            _dbFactory,
            _asnResolution,
            _scopeFactory,
            _ouiDb,
            _loggerFactory.CreateLogger<UpstreamTracerService>(),
            new UpstreamTracerService.WanProbeBinding(
                context.Id,
                context.WanInterface!,
                context.InterfaceName ?? context.ProbeSourceIp));
    }

    /// <inheritdoc />
    public Func<ValueTask>? EvictSite(string slug)
    {
        _instances.TryRemove(slug, out _);
        return null;
    }
}
