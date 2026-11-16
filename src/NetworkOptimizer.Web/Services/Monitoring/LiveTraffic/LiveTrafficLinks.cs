using System.Net;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Web.Services.Monitoring.LiveTraffic;

/// <summary>Links out of Live Traffic to the rest of the app.</summary>
public static class LiveTrafficLinks
{
    /// <summary>
    /// The Threat Intelligence drill-down for a flow's remote address, or null when it is not a public
    /// address (a LAN peer or the gateway has nothing to drill into).
    /// </summary>
    public static string? ThreatsDrilldown(LiveFlowRow f) =>
        f.RouteKind is not LiveRouteKind.Lan ? ThreatsDrilldown(f.DestinationIp) : null;

    /// <summary>The Threat Intelligence drill-down for an address, or null when it is not a public address.</summary>
    public static string? ThreatsDrilldown(string? address) =>
        IPAddress.TryParse(address, out var ip) && NetworkUtilities.IsPublicIpAddress(ip)
            ? "/threats?tab=drilldown&ip=" + Uri.EscapeDataString(address!)
            : null;

    /// <summary>
    /// Attributes that put Live Traffic's destination card (the address with a copy button, Threats,
    /// Trace) on an element on any page: splat them with <c>@attributes</c>. Null when the address is
    /// a private one, which has no Threats page and is not worth a trace link. A hostname gets the
    /// card with Trace alone.
    /// </summary>
    /// <param name="address">The address or hostname shown.</param>
    /// <param name="name">A name shown above the address, when it differs from it.</param>
    /// <param name="threatsLink">False where the element already sits on, or links to, the Threats page.</param>
    public static Dictionary<string, object>? DestinationCard(string? address, string? name = null, bool threatsLink = true)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        if (IPAddress.TryParse(address, out var ip))
        {
            if (!NetworkUtilities.IsPublicIpAddress(ip)) return null;
        }
        // Not an address: only a host name can be traced, not an owner or provider name.
        else if (Uri.CheckHostName(address) != UriHostNameType.Dns || !address.Contains('.'))
        {
            return null;
        }
        var card = new Dictionary<string, object> { ["data-dest-ip"] = address };
        if (!string.IsNullOrEmpty(name) && name != address) card["data-dest-name"] = name;
        if (threatsLink && ThreatsDrilldown(address) is { } threats) card["data-dest-link"] = threats;
        return card;
    }

    /// <summary>The flow's client: see <see cref="Client(string?, string?)"/>.</summary>
    public static string? Client(LiveFlowRow f) => Client(f.ClientIp, f.ClientDeviceMac);

    /// <summary>
    /// A client's page: Device Stats for a network device (the gateway or a UniFi device), else
    /// Client Performance's Data / Apps tab. Null when there is no client address.
    /// </summary>
    public static string? Client(string? clientIp, string? deviceMac) =>
        deviceMac != null ? MonitoringLinks.DeviceStats(deviceMac, MonitoringLinks.LiveAtToken)
        : string.IsNullOrEmpty(clientIp) ? null
        : "/client-dashboard?ip=" + Uri.EscapeDataString(clientIp) + "&tab=data";

    /// <summary>
    /// The far end's page when it is one of the site's own (a client, a UniFi device, the gateway):
    /// Device Stats or Client Performance, as for a client. Null for anything else.
    /// </summary>
    public static string? LanPeer(LiveFlowRow f) =>
        f.RemoteDeviceMac != null ? Client(f.DestinationIp, f.RemoteDeviceMac)
        : f.RemoteClientMac != null ? Client(f.DestinationIp, null)
        : null;

    /// <summary>Live Traffic with Name This App open, prefilled from the seed.</summary>
    public static string NameApp(CustomAppSeed seed) => "/monitoring/live-traffic?nameApp=" + seed.Encode();

    /// <summary>The query key <see cref="NameApp"/> uses.</summary>
    public const string NameAppKey = "nameApp";
}
