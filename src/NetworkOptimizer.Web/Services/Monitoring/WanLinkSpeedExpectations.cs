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

    /// <summary>Plan speed at or below which the plan implies nothing: 100 Mbps Ethernet carries it.</summary>
    private const double PlanFloorThresholdMbps = 100;

    /// <summary>Ethernet rates a plan can require, slowest first.</summary>
    private static readonly int[] EthernetRatesMbps = [1000, 2500, 5000, 10000];

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

    /// <summary>
    /// The link speed a WAN's plan needs: the slowest Ethernet rate that carries it (a 2000 Mbps
    /// plan needs 2.5 Gbps), capped at what the gateway port can negotiate. None for a plan of
    /// 100 Mbps or less. The cap keeps a plan faster than the port from alerting forever: that
    /// is a hardware limit, not a negotiation fault.
    /// </summary>
    /// <param name="profile">The WAN's profile, carrying the plan speeds.</param>
    /// <param name="portMedia">The port's UniFi <c>media</c>, e.g. "GE", "2.5GE", "10GE", "SFP+".</param>
    public static int? PlanFloorMbps(WanProfile? profile, string? portMedia)
    {
        var plan = Math.Max(profile?.DownloadMbps ?? 0, profile?.UploadMbps ?? 0);
        if (plan <= PlanFloorThresholdMbps) return null;
        var needed = EthernetRatesMbps.FirstOrDefault(r => r >= plan, EthernetRatesMbps[^1]);
        return Math.Min(needed, PortMaxMbps(portMedia));
    }

    /// <summary>
    /// The fastest an RJ45 port's <c>media</c> says it negotiates. SFP cages report the cage, not
    /// the module in it (a 10G cage often holds a 1G module), so they and anything unrecognized
    /// count as 1 Gbps.
    /// </summary>
    internal static int PortMaxMbps(string? media) => media?.Trim().ToUpperInvariant() switch
    {
        "2.5GE" => 2500,
        "5GE" => 5000,
        "10GE" => 10000,
        _ => 1000,
    };

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
