using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which WAN a policy route pins a site's probing to, and which box that route names.
/// <para>
/// Only load-balancing sites need it. There an unpinned probe measures no single WAN, so its
/// targets are unattributable - unless the operator has already steered the probing box down one
/// WAN on the gateway. The route names a MAC, so it identifies the box as well as the WAN: the
/// vantage it justifies is bound to whoever the route actually pins, never to a guess.
/// </para>
/// </summary>
public static class PinnedProbeContextBuilder
{
    /// <summary>A box that probes for this site, and the LAN address the gateway knows it by.</summary>
    /// <param name="AgentId">The agent, or null for the Network Optimizer server itself.</param>
    /// <param name="LanIp">The address to look up in the client cache.</param>
    public sealed record ProbeHost(int? AgentId, string? LanIp);

    /// <summary>What a policy route pins, and which of this site's probing boxes it names.</summary>
    /// <param name="WanInterface">Normalized WAN key the targets get stamped with.</param>
    /// <param name="ContextName">Name for the vantage, taken from the WAN so it reads like the others.</param>
    /// <param name="AgentId">
    /// The agent whose probes the route steers, or null when it is the server's. This is what the
    /// vantage binds to - a vantage naming no agent on an agent-collected site would have its
    /// targets pushed to nobody.
    /// </param>
    /// <param name="KillSwitchEnabled">False means a failover re-routes while the stamp stays put.</param>
    public sealed record Plan(string WanInterface, string ContextName, int? AgentId, bool KillSwitchEnabled);

    /// <summary>
    /// The vantage to create for whichever probing box a route pins, or null when none is pinned.
    /// Pure, so the whole decision is testable without a console: callers fetch the routes,
    /// clients and networks, and enumerate their probing boxes.
    /// </summary>
    /// <param name="routes">The site's traffic routes.</param>
    /// <param name="networks">The site's networks, for turning a route's network id into a WAN.</param>
    /// <param name="clients">The site's clients, for turning each box's address into a MAC.</param>
    /// <param name="hosts">Every box that probes for this site - the server and any agents.</param>
    public static Plan? Build(
        IEnumerable<UniFiTrafficRouteResponse> routes,
        IEnumerable<NetworkInfo> networks,
        IEnumerable<UniFiClientResponse> clients,
        IEnumerable<ProbeHost> hosts)
    {
        var routeList = routes as IList<UniFiTrafficRouteResponse> ?? routes.ToList();
        var clientList = clients as IList<UniFiClientResponse> ?? clients.ToList();
        var networkList = networks as IList<NetworkInfo> ?? networks.ToList();

        foreach (var host in hosts)
        {
            var mac = MacForAddress(clientList, host.LanIp);
            if (mac == null) continue;

            var pin = TrafficRouteWanPinning.ResolvePin(routeList, mac);
            if (pin == null) continue;

            // The route names a network id; only a WAN one says anything. A route onto a LAN or a
            // VPN network is a different feature entirely.
            var network = networkList.FirstOrDefault(n =>
                string.Equals(n.Id, pin.NetworkId, StringComparison.OrdinalIgnoreCase));
            if (network == null || !network.IsWan || string.IsNullOrEmpty(network.WanNetworkgroup)) continue;

            var key = GatewayWanHelper.WanInterfaceKeyFromKey(network.WanNetworkgroup);
            var label = string.IsNullOrWhiteSpace(network.Name) ? key : network.Name;
            return new Plan(key, label, host.AgentId, pin.KillSwitchEnabled);
        }

        return null;
    }

    /// <summary>
    /// The MAC the client cache holds for an address. LastIp is deliberately not consulted: it is
    /// stale by definition and would hand back whichever device used to hold the address.
    /// </summary>
    /// <param name="clients">The site's clients.</param>
    /// <param name="ip">The address to look up.</param>
    public static string? MacForAddress(IEnumerable<UniFiClientResponse> clients, string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        var wanted = ip.Trim();
        var matches = clients
            .Where(c => !string.IsNullOrEmpty(c.Mac)
                && (string.Equals(c.Ip, wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(c.FixedIp, wanted, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Mac)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Exactly one or nothing. Two devices claiming an address is a site problem, and picking
        // one of them would pin the targets to whichever WAN the wrong box uses.
        return matches.Count == 1 ? matches[0] : null;
    }
}
