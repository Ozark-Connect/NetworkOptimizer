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
/// Consumers gate the speed-test surfaces on this: today an on-gateway agent
/// never hosts the LAN speed-test listener or the WAN test binary. When
/// speed-test-capable gateway installs arrive for higher-end gateways, that
/// gating should move to a per-agent capability flag - this detector only
/// answers "is it on the gateway", not "what can it do".
/// </summary>
public class AgentOnGatewayDetector
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly AgentEnrollmentService _enrollment;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly ILogger<AgentOnGatewayDetector> _logger;
    private readonly ConcurrentDictionary<string, (bool OnGateway, DateTime At)> _cache = new();

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
    /// gateway. False for the default site, sites without an online agent, or
    /// when the site's console isn't connected and no cached answer exists.
    /// </summary>
    public async Task<bool> IsAgentOnGatewayAsync(string siteSlug, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(siteSlug) || siteSlug == SiteManagementService.DefaultSiteSlug)
            return false;

        if (_cache.TryGetValue(siteSlug, out var cached) && DateTime.UtcNow - cached.At < CacheTtl)
            return cached.OnGateway;

        var onGateway = false;
        try
        {
            var agentIp = await _enrollment.GetOnlineAgentLanIpAsync(siteSlug);
            if (!string.IsNullOrEmpty(agentIp))
            {
                var connection = _siteConnections.GetFor(siteSlug);
                if (!connection.IsConnected || connection.Client == null)
                {
                    // Console momentarily down (it reconnects through the same agent
                    // tunnel) - keep the last answer instead of flapping to false.
                    return cached.At != default ? cached.OnGateway : false;
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

                onGateway = gatewayIps.Contains(agentIp!, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Agent-on-gateway detection failed for site {Slug}", siteSlug);
        }

        _cache[siteSlug] = (onGateway, DateTime.UtcNow);
        return onGateway;
    }
}
