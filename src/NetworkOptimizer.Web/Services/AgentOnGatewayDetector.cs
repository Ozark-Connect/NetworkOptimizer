using System.Collections.Concurrent;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Detects whether a site's on-site agent runs on the UniFi gateway itself
/// rather than a separate box. The agent's hello reports the IPv4 of its
/// default-route interface; on a gateway that is the WAN address, which is
/// exactly the "ip" UniFi Network reports for the gateway device (the LAN-side
/// gateway IP is matched too, in case the agent's detection lands there
/// instead). Detection is IP correlation only - no agent-side flag - so it
/// works with any agent version.
///
/// Repeat callers are never blocked: the answer comes from a cache (stale is
/// fine for UI gating), and expiry triggers a background refresh with its own
/// timeout. The FIRST query for a site awaits one bounded refresh instead of
/// defaulting to false - a false answer paints the full speed-test surfaces
/// (dead targets pointing at the gateway) on a gateway-agent site, and pages
/// without polling would hold that until a reload. After that one await the
/// cache always has an entry, so UI paths answer instantly.
///
/// Consumers gate the speed-test surfaces on this: today an on-gateway agent
/// never hosts the LAN speed-test listener or the WAN test binary. When
/// speed-test-capable gateway installs arrive, that gating should move to a
/// per-agent capability flag - this detector only answers "is it on the
/// gateway", not "what can it do".
/// </summary>
public class AgentOnGatewayDetector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(10);

    private readonly AgentEnrollmentService _enrollment;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly ILogger<AgentOnGatewayDetector> _logger;
    private readonly ConcurrentDictionary<string, (bool OnGateway, DateTime At)> _cache = new();
    private readonly ConcurrentDictionary<string, Task> _refreshing = new();

    public AgentOnGatewayDetector(
        AgentEnrollmentService enrollment,
        SiteConnectionRegistry siteConnections,
        ILogger<AgentOnGatewayDetector> logger)
    {
        _enrollment = enrollment;
        _siteConnections = siteConnections;
        _logger = logger;
    }

    /// <summary>
    /// Whether the site's online agent appears to run on the site's UniFi
    /// gateway. Answers instantly from cache once a site has ever been resolved
    /// (a stale answer is served while a background refresh runs); the first
    /// query for a site awaits one refresh (bounded by the refresh timeout) so
    /// speed-test surfaces never paint from a made-up false. False for the
    /// default site.
    /// </summary>
    public async Task<bool> IsAgentOnGatewayAsync(string siteSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug)
            return false;

        var hasCached = _cache.TryGetValue(siteSlug, out var cached);
        if (hasCached && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.OnGateway;

        var refresh = StartOrJoinRefresh(siteSlug);
        if (hasCached)
            return cached.OnGateway;

        try
        {
            await refresh.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Caller gave up (page disposed) - the refresh itself continues.
        }
        return _cache.TryGetValue(siteSlug, out var fresh) && fresh.OnGateway;
    }

    /// <summary>One in-flight refresh per site; result lands in the cache, and first-time callers await the returned task.</summary>
    private Task StartOrJoinRefresh(string siteSlug) =>
        _refreshing.GetOrAdd(siteSlug, slug => Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                await RefreshAsync(slug, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Agent-on-gateway detection failed for site {Slug}", slug);
            }
            finally
            {
                _refreshing.TryRemove(slug, out _);
            }
        }));

    private async Task RefreshAsync(string siteSlug, CancellationToken ct)
    {
        var agentIp = await _enrollment.GetOnlineAgentLanIpAsync(siteSlug);
        if (string.IsNullOrEmpty(agentIp))
        {
            _cache[siteSlug] = (false, DateTime.UtcNow);
            return;
        }

        var connection = _siteConnections.GetFor(siteSlug);
        if (!connection.IsConnected || connection.Client == null)
        {
            // Console momentarily down (it reconnects through the same agent
            // tunnel) - keep the last answer instead of flapping to false, but
            // re-stamp it so expiry doesn't re-trigger a refresh every call.
            var last = _cache.TryGetValue(siteSlug, out var cached) && cached.OnGateway;
            _cache[siteSlug] = (last, DateTime.UtcNow);
            return;
        }

        var devices = await connection.Client.GetDevicesAsync(ct) ?? new();
        var gatewayIps = devices
            .Where(d => d.DeviceType == DeviceType.Gateway && !string.IsNullOrEmpty(d.Ip))
            .Select(d => d.Ip!)
            .ToList();
        try
        {
            var lanIp = await Monitoring.SnmpDeviceRules.ResolveGatewayLanIpAsync(connection.Client, ct);
            if (!string.IsNullOrEmpty(lanIp))
                gatewayIps.Add(lanIp!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Gateway LAN IP resolution failed for site {Slug} during on-gateway detection", siteSlug);
        }

        _cache[siteSlug] = (gatewayIps.Contains(agentIp!, StringComparer.OrdinalIgnoreCase), DateTime.UtcNow);
    }
}
