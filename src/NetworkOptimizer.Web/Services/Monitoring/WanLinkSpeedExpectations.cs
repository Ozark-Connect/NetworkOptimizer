using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which gateway ports are WAN ports, and what speed each is expected to negotiate. Shared by the
/// link speed alert and the Config Optimizer history check so both judge a port the same way.
/// </summary>
public static class WanLinkSpeedExpectations
{
    /// <summary>
    /// How far back the usual speed is taken from. Long enough that a few days at a degraded speed
    /// still read against the old one, short enough that a deliberate permanent change (a slower
    /// ISP device) stops alerting on its own once the old speed ages out.
    /// </summary>
    public static readonly TimeSpan BaselineWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Plan speed above which the port is expected to negotiate at least 1 Gbps. Only the 1 Gbps
    /// tier is inferred from the plan: anything faster depends on the port and the ISP device, and
    /// a 2 Gbps plan on a 1 Gbps port is a hardware limit, not a negotiation fault.
    /// </summary>
    private const double GigabitPlanThresholdMbps = 100;

    /// <summary>The WAN profile a gateway port serves, or null when none matches.</summary>
    /// <param name="deviceMac">Gateway MAC.</param>
    /// <param name="ifName">Port's kernel interface name, e.g. "eth4".</param>
    /// <param name="networkName">Port's UniFi network name, e.g. "wan2".</param>
    /// <param name="profiles">The site's WAN profiles.</param>
    public static WanProfile? MatchWan(string deviceMac, string ifName, string? networkName, IEnumerable<WanProfile> profiles)
    {
        WanProfile? byGroup = null;
        foreach (var p in profiles)
        {
            if (!string.IsNullOrEmpty(p.GatewayMac) && !string.Equals(p.GatewayMac, deviceMac, StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(BaseInterface(p.DataPathInterface), ifName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(BaseInterface(p.CounterInterface), ifName, StringComparison.OrdinalIgnoreCase))
                return p;
            if (byGroup == null && !string.IsNullOrEmpty(networkName)
                && string.Equals(p.WanNetworkgroup, networkName, StringComparison.OrdinalIgnoreCase))
                byGroup = p;
        }
        return byGroup;
    }

    /// <summary>Whether a gateway port is a WAN port: on a WAN network, or backing a known WAN.</summary>
    public static bool IsWanPort(string? networkName, WanProfile? match) =>
        match != null || (networkName != null && networkName.StartsWith("wan", StringComparison.OrdinalIgnoreCase));

    /// <summary>The speed a WAN's plan implies at minimum: 1 Gbps for a plan above 100 Mbps, else none.</summary>
    public static int? PlanFloorMbps(WanProfile? profile)
    {
        var plan = Math.Max(profile?.DownloadMbps ?? 0, profile?.UploadMbps ?? 0);
        return plan > GigabitPlanThresholdMbps ? 1000 : null;
    }

    /// <summary>The port's expected speed: the highest of what is known, or null when nothing is.</summary>
    public static int? Expected(params int?[] candidates)
    {
        int? best = null;
        foreach (var c in candidates)
            if (c is > 0 && c > (best ?? 0)) best = c;
        return best;
    }

    /// <summary>"eth4.100" to "eth4"; other names unchanged.</summary>
    private static string? BaseInterface(string? name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }
}
