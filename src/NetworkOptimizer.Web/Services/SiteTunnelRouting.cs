using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Site-aware device reachability through the agent tunnel. A single per-site
/// flag (stored in the site's own database) says whether this server reaches
/// the site's device endpoints - SSH to the gateway and devices, cable modem /
/// ONT / cellular hotspot status pages - through the site's agent tunnel
/// instead of directly. When enabled, <see cref="RouteAsync"/> rewrites a
/// host:port to a loopback endpoint from <see cref="AgentTunnelProxyService"/>;
/// the agent dials the real target inside the site's network, and the caller's
/// transport (SSH.NET, HttpClient) is unaware of the proxying. The default site is normally this
/// server's own network and routes directly; it can route via tunnel too, but only once it is
/// explicitly configured for its agent to cover it (the off-site-server case).
/// </summary>
public class SiteTunnelRouting
{
    /// <summary>Per-site setting key: reach this site's devices through its agent tunnel.</summary>
    public const string DevicesViaAgentKey = "devices.via_agent";

    // The flag is consulted per SSH command / modem poll; cache it briefly so
    // hot paths don't hit SQLite on every invocation.
    private static readonly TimeSpan FlagCacheExpiry = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly SiteAgentCoverage _agentCoverage;
    private readonly ILogger<SiteTunnelRouting> _logger;
    private readonly ConcurrentDictionary<string, (bool Enabled, DateTime At)> _flags = new();

    public SiteTunnelRouting(IServiceProvider serviceProvider, SiteAgentCoverage agentCoverage, ILogger<SiteTunnelRouting> logger)
    {
        _serviceProvider = serviceProvider;
        _agentCoverage = agentCoverage;
        _logger = logger;
    }

    /// <summary>
    /// Forget the cached flag for a site. Called when the flag is cleared out from under the cache
    /// - removing a site's last agent - so routing stops within the request rather than after the
    /// cache expires.
    /// </summary>
    public void Invalidate(string slug) => _flags.TryRemove(slug, out _);

    /// <summary>Whether the site's devices are configured to be reached through its agent tunnel.</summary>
    public async Task<bool> IsViaAgentAsync(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        // The default site answers no without touching the database unless it has been handed to
        // its agent - this is consulted per SSH command and per modem poll on every install.
        if (slug == SiteManagementService.DefaultSiteSlug && !_agentCoverage.Covers(slug))
            return false;
        if (_flags.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < FlagCacheExpiry)
            return cached.Enabled;
        try
        {
            using var scope = _serviceProvider.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(slug);
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
            var setting = await db.SystemSettings.FindAsync(DevicesViaAgentKey);
            var enabled = bool.TryParse(setting?.Value, out var value) && value;
            _flags[slug] = (enabled, DateTime.UtcNow);
            return enabled;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whether an on-site agent tunnel is currently connected for this site.
    /// Distinct from <see cref="IsViaAgentAsync"/>, which only says the site is
    /// <em>configured</em> to route via its agent: an agent-backed site whose
    /// agent has dropped is via-agent but not online, so agent-dependent actions
    /// (WAN/LAN speed tests, SSH, modem/ONT) can't run until it reconnects.
    /// </summary>
    public bool IsAgentOnline(string slug)
    {
        if (string.IsNullOrEmpty(slug)) return false;
        if (slug == SiteManagementService.DefaultSiteSlug && !_agentCoverage.Covers(slug))
            return false;
        var registry = _serviceProvider.GetService<AgentTunnelRegistry>();
        return registry != null && registry.GetForSite(slug).Count > 0;
    }

    /// <summary>
    /// The endpoint a caller should dial to reach {host}:{port} inside the given
    /// site: the original pair when the site is reached directly, or a loopback
    /// tunnel-proxy endpoint when the site's devices are routed via its agent.
    /// </summary>
    public async Task<(string Host, int Port)> RouteAsync(string slug, string host, int port)
    {
        if (!await IsViaAgentAsync(slug)) return (host, port);
        var proxy = _serviceProvider.GetService<AgentTunnelProxyService>();
        if (proxy == null) return (host, port);
        var localPort = proxy.GetOrCreateEndpoint(slug, host, port);
        _logger.LogDebug("Endpoint {Host}:{Port} (site {Slug}) routed via agent tunnel (127.0.0.1:{LocalPort})",
            host, port, slug, localPort);
        return ("127.0.0.1", localPort);
    }
}
