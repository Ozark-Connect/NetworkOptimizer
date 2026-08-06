using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Turns a policy route that pins the probing box onto one WAN into the WAN context that says so.
/// <para>
/// Only load-balancing sites need it. There an unpinned probe measures no single WAN, so its
/// targets read as unattributable - unless the operator has already steered that box down one WAN
/// on the gateway, in which case the attribution exists and only we were missing it. Every step
/// can decline, and declining costs nothing: the targets stay unpinned, which is still true.
/// </para>
/// </summary>
public static class PinnedProbeContextBuilder
{
    /// <summary>
    /// The WAN a probing box is pinned to, and what the context describing it should say. No probe
    /// source: the gateway is already steering this box by MAC, so the context has nothing to bind
    /// and exists only to say which WAN the readings belong to.
    /// </summary>
    /// <param name="WanInterface">Normalized WAN key the targets get stamped with.</param>
    /// <param name="ContextName">Name for the context, taken from the WAN so it reads like the others.</param>
    /// <param name="KillSwitchEnabled">False means a failover re-routes while the stamp stays put.</param>
    public sealed record Plan(string WanInterface, string ContextName, bool KillSwitchEnabled);

    /// <summary>
    /// The context to create for a probing box, or null when nothing can be said. Pure so the
    /// whole decision is testable without a console: callers fetch the routes, clients and
    /// networks, and this decides.
    /// </summary>
    /// <param name="routes">The site's traffic routes.</param>
    /// <param name="networks">The site's networks, for turning a route's network id into a WAN.</param>
    /// <param name="probeIp">The probing box's LAN address.</param>
    /// <param name="macForIp">That address's MAC, from the client cache, or null when unknown.</param>
    public static Plan? Build(
        IEnumerable<UniFiTrafficRouteResponse> routes,
        IEnumerable<NetworkInfo> networks,
        string? probeIp,
        string? macForIp)
    {
        if (string.IsNullOrWhiteSpace(probeIp) || string.IsNullOrWhiteSpace(macForIp)) return null;

        var pin = TrafficRouteWanPinning.ResolvePin(routes, macForIp);
        if (pin == null) return null;

        // The route names a network id; only a WAN one tells us anything. A route onto a LAN or a
        // VPN network is a different feature entirely.
        var network = networks.FirstOrDefault(n =>
            string.Equals(n.Id, pin.NetworkId, StringComparison.OrdinalIgnoreCase));
        if (network == null || !network.IsWan || string.IsNullOrEmpty(network.WanNetworkgroup)) return null;

        var key = GatewayWanHelper.WanInterfaceKeyFromKey(network.WanNetworkgroup);
        var label = string.IsNullOrWhiteSpace(network.Name) ? key : network.Name;
        return new Plan(key, label, pin.KillSwitchEnabled);
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
