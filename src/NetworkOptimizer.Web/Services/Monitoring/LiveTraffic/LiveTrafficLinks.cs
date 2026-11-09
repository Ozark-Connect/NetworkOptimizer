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
        f.RouteKind is not LiveRouteKind.Lan && IPAddress.TryParse(f.DestinationIp, out var ip) && NetworkUtilities.IsPublicIpAddress(ip)
            ? "/threats?tab=drilldown&ip=" + Uri.EscapeDataString(f.DestinationIp)
            : null;

    /// <summary>The flow's client on Client Performance's Data / Apps tab, or null when the flow carries no client address.</summary>
    public static string? ClientPerformance(LiveFlowRow f) => ClientPerformance(f.ClientIp);

    /// <summary>A client address on Client Performance's Data / Apps tab, or null when there is none.</summary>
    public static string? ClientPerformance(string? clientIp) =>
        string.IsNullOrEmpty(clientIp) ? null : "/client-dashboard?ip=" + Uri.EscapeDataString(clientIp) + "&tab=data";
}
