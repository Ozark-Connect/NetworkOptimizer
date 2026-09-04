using System.Net;
using System.Net.Sockets;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Picks which of a site's agents hosts the LAN speed test clients are sent to - the one
/// selection behind the client-facing target, the Settings "auto" hint, and the path
/// analysis anchor, so a site with several agents cannot resolve differently in each.
/// </summary>
/// <remarks>
/// Among reachable agents with a known LAN IP, most recently seen first: the first that states
/// it serves a speed test wins, on its word alone (a gateway agent that says so counts). Failing
/// that, a managed site takes the first agent that predates the announcement and is not on the
/// gateway - it is the site's only possible host, so the old binary is assumed capable. The
/// default site never assumes: it hosts its own speed test, so an agent is chosen there only
/// when it says it serves one.
/// </remarks>
public class SiteSpeedTestHostSelector
{
    /// <summary>The agent chosen to host the speed test, with the address and port clients use.</summary>
    public sealed record SpeedTestHost(int AgentId, string LanIp, int Port);

    /// <summary>
    /// The outcome for a site. <see cref="AgentReachable"/> is false when the site has no
    /// reachable agent at all; <see cref="AnyOnGateway"/> says whether an agent that was passed
    /// over sits on the UniFi gateway, which is what the pages explain when there is no host.
    /// </summary>
    public sealed record Selection(SpeedTestHost? Host, bool AgentReachable, bool AnyOnGateway)
    {
        public static readonly Selection None = new(null, false, false);
    }

    private readonly AgentEnrollmentService _enrollment;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly AgentOnGatewayDetector _onGatewayDetector;

    public SiteSpeedTestHostSelector(
        AgentEnrollmentService enrollment,
        AgentTunnelRegistry tunnelRegistry,
        AgentOnGatewayDetector onGatewayDetector)
    {
        _enrollment = enrollment;
        _tunnelRegistry = tunnelRegistry;
        _onGatewayDetector = onGatewayDetector;
    }

    /// <summary>Selects the site's speed test host. Coverage-gated like every agent target: the
    /// default site answers nothing unless configured for its agent to cover it.</summary>
    public async Task<Selection> SelectAsync(string siteSlug, CancellationToken ct = default)
    {
        var agents = await _enrollment.GetReachableAgentsAsync(siteSlug);
        if (agents.Count == 0)
            return Selection.None;

        var live = _tunnelRegistry.GetForSite(siteSlug).ToDictionary(c => c.AgentId);
        var isDefault = siteSlug == SiteManagementService.DefaultSiteSlug;

        foreach (var agent in agents)
        {
            if (live.TryGetValue(agent.Id, out var c) && c.ServesSpeedTest == true)
                return new Selection(HostFor(agent, c), true, false);
        }

        var anyOnGateway = false;
        SpeedTestHost? assumed = null;
        foreach (var agent in agents)
        {
            live.TryGetValue(agent.Id, out var c);
            var onGateway = c?.OnGateway ?? await _onGatewayDetector.IsAgentOnGatewayAsync(
                siteSlug, agent.Id, c?.HostAddresses ?? new[] { agent.LanIp! }, ct);
            anyOnGateway |= onGateway;
            if (assumed == null && !isDefault && c?.ServesSpeedTest != false && !onGateway)
                assumed = HostFor(agent, c);
        }
        return new Selection(assumed, true, anyOnGateway);
    }

    /// <summary>
    /// The address and port clients use for this agent. A gateway agent reports its WAN address as
    /// its LAN IP, which no site client can use as a LAN target, so a LAN IP that is not RFC1918
    /// IPv4 gives way to the first such address the agent's host holds (IPv4 only: the URL is
    /// composed bare, and CGNAT or ULA space is no more reachable to a LAN client than the WAN).
    /// </summary>
    private static SpeedTestHost HostFor(SiteAgent agent, AgentTunnelConnection? connection)
    {
        var lanIp = agent.LanIp!;
        if (connection != null && !IsLanAddress(lanIp))
            lanIp = connection.HostAddresses.FirstOrDefault(IsLanAddress) ?? lanIp;
        var port = connection is { SpeedTestPort: > 0 } ? connection.SpeedTestPort : SiteSpeedTestTargetResolver.AgentOpenSpeedTestPort;
        return new SpeedTestHost(agent.Id, lanIp, port);
    }

    private static bool IsLanAddress(string address) =>
        IPAddress.TryParse(address, out var ip)
        && ip.AddressFamily == AddressFamily.InterNetwork
        && NetworkUtilities.IsRfc1918(ip);
}
