using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.UniFi;

/// <summary>
/// Which WAN a policy-based route pins a device's traffic to, if any.
/// <para>
/// Only useful on a load-balancing site, where an unpinned probe measures no single WAN and the
/// route is the one thing that says otherwise. Best effort throughout: no match means the probing
/// box stays unpinned, which is already the truthful answer, so nothing here needs to guess.
/// </para>
/// </summary>
public static class TrafficRouteWanPinning
{
    /// <summary>Matches every destination - the only target that pins a device's traffic in general.</summary>
    private const string InternetTarget = "INTERNET";
    private const string ClientDevice = "CLIENT";
    private const string AllClientsDevice = "ALL_CLIENTS";

    /// <summary>What a route pins, and whether that survives the WAN going down.</summary>
    /// <param name="NetworkId">The network the traffic leaves by.</param>
    /// <param name="KillSwitchEnabled">
    /// False means a failover re-routes the traffic while the readings keep this WAN's name.
    /// </param>
    /// <param name="Description">The route's name, for logs and anything shown to the operator.</param>
    public sealed record Pin(string NetworkId, bool KillSwitchEnabled, string? Description);

    /// <summary>
    /// The route that pins this device's traffic wholesale, or null when none does. A route
    /// qualifies only when it is enabled, matches every destination, names a network, and either
    /// names this MAC or applies to all clients.
    /// </summary>
    /// <param name="routes">The site's traffic routes.</param>
    /// <param name="deviceMac">The probing box's MAC.</param>
    public static Pin? ResolvePin(IEnumerable<UniFiTrafficRouteResponse> routes, string? deviceMac)
    {
        if (string.IsNullOrWhiteSpace(deviceMac)) return null;
        var mac = NormalizeMac(deviceMac);

        // A MAC named explicitly beats a blanket all-clients route: both pin the same traffic, but
        // the explicit one is the operator saying something about THIS box.
        var matches = routes
            .Where(r => r != null && r.Enabled && NamesEveryDestination(r) && !string.IsNullOrEmpty(r.NetworkId))
            .Select(r => (Route: r, Targets: r.TargetDevices ?? new List<UniFiTrafficRouteTargetDevice>()))
            .ToList();

        var explicitMatch = matches.FirstOrDefault(m => m.Targets.Any(d =>
            string.Equals(d.Type, ClientDevice, StringComparison.OrdinalIgnoreCase)
            && NormalizeMac(d.ClientMac) == mac));
        var chosen = explicitMatch.Route
            ?? matches.FirstOrDefault(m => m.Targets.Any(d =>
                string.Equals(d.Type, AllClientsDevice, StringComparison.OrdinalIgnoreCase))).Route;

        return chosen == null
            ? null
            : new Pin(chosen.NetworkId!, chosen.KillSwitchEnabled, chosen.Description);
    }

    /// <summary>
    /// Whether the route steers every destination. An IP, domain or region route steers a slice of
    /// the device's traffic, which says nothing about where a probe to somewhere else leaves.
    /// </summary>
    private static bool NamesEveryDestination(UniFiTrafficRouteResponse route) =>
        string.Equals(route.MatchingTarget, InternetTarget, StringComparison.OrdinalIgnoreCase);

    /// <summary>Lowercased, separators dropped, so 00:11:22 and 00-11-22 compare equal.</summary>
    private static string NormalizeMac(string? mac) =>
        string.IsNullOrEmpty(mac)
            ? string.Empty
            : new string(mac.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
