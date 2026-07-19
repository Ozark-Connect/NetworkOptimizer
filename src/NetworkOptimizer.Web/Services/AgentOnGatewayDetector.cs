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
/// Callers are NEVER blocked: the answer comes from a cache (stale is fine for
/// UI gating), and a miss or expiry triggers a background refresh with its own
/// timeout. Sites are queried on UI paths - first page paint must not wait on
/// console round-trips through an agent tunnel that may be mid-reconnect.
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
    private readonly ConcurrentDictionary<string, byte> _refreshing = new();

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
    /// gateway. Answers instantly from cache (a stale answer is served while a
    /// background refresh runs); false for the default site or until a first
    /// refresh completes. Never blocks on console round-trips.
    /// </summary>
    public Task<bool> IsAgentOnGatewayAsync(string siteSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug)
            return Task.FromResult(false);

        var hasCached = _cache.TryGetValue(siteSlug, out var cached);
        if (!hasCached || DateTime.UtcNow - cached.At >= CacheTtl)
            StartBackgroundRefresh(siteSlug);

        return Task.FromResult(hasCached && cached.OnGateway);
    }

    /// <summary>One in-flight refresh per site; result lands in the cache for the next caller.</summary>
    private void StartBackgroundRefresh(string siteSlug)
    {
        if (!_refreshing.TryAdd(siteSlug, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(RefreshTimeout);
                await RefreshAsync(siteSlug, cts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Agent-on-gateway detection failed for site {Slug}", siteSlug);
            }
            finally
            {
                _refreshing.TryRemove(siteSlug, out _);
            }
        });
    }

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
