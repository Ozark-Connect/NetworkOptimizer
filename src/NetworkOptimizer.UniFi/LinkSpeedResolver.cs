namespace NetworkOptimizer.UniFi;

/// <summary>
/// The one place a LAN port's speed is decided. Never read <c>speed_bps</c> or ifHighSpeed alone:
/// <list type="bullet">
/// <item>Negotiated speed is the lower of SNMP and the console port table: a gateway inflates copper
/// ports to their ceiling over SNMP.</item>
/// <item>A gateway's built-in switch ports report the 10G conduit over SNMP: the console speed only,
/// never an SNMP fallback.</item>
/// <item>WAN ports report port table speed 0, so SNMP wins there.</item>
/// <item>A device's own reported uplink speed beats the upstream port table (an unmanaged switch
/// between them), except a gateway's, whose uplink is its WAN.</item>
/// <item>Capability comes from UniFi only (<c>speed_caps</c>), never SNMP.</item>
/// </list>
/// </summary>
public static class LinkSpeedResolver
{
    /// <summary>The lower of two reported speeds, either of which may be missing.</summary>
    public static int? LowerOf(int? a, int? b) => (a, b) switch
    {
        (null, null) => null,
        (null, var y) => y,
        (var x, null) => x,
        var (x, y) => Math.Min(x!.Value, y!.Value),
    };

    /// <summary>A port's negotiated speed in Mbps from what the console and SNMP report.</summary>
    /// <param name="isGatewayPort">The port is on a gateway (its SNMP speed is the switch conduit or a ceiling).</param>
    /// <param name="consoleMbps">The console port table's speed; 0 or null when down or unreported.</param>
    /// <param name="snmpBps">SNMP ifHighSpeed in bits per second; 0 or null when unknown.</param>
    /// <param name="isWan">The port is a WAN port.</param>
    public static int? Negotiated(bool isGatewayPort, int? consoleMbps, long? snmpBps, bool isWan = false)
    {
        int? console = consoleMbps is > 0 ? consoleMbps : null;
        int? snmp = snmpBps is > 0 ? (int)(snmpBps.Value / 1_000_000) : null;
        if (isWan) return snmp ?? console;
        if (isGatewayPort) return console;
        return LowerOf(console, snmp);
    }

    /// <summary>
    /// A wired link's speed: the upstream port's negotiated speed, or the downstream device's own
    /// uplink speed where that is lower. A gateway downstream contributes nothing (its uplink is WAN).
    /// </summary>
    public static int? WiredLink(int? upstreamPortMbps, int? downstreamOwnUplinkMbps, bool downstreamIsGateway)
    {
        int? own = !downstreamIsGateway && downstreamOwnUplinkMbps is > 0 ? downstreamOwnUplinkMbps : null;
        int? port = upstreamPortMbps is > 0 ? upstreamPortMbps : null;
        return LowerOf(port, own);
    }

    /// <summary>The fastest speed both ends support, from UniFi speed_caps only; null unless both report caps.</summary>
    public static int? Capability(int? upstreamMaxMbps, int? downstreamMaxMbps) =>
        upstreamMaxMbps is > 0 && downstreamMaxMbps is > 0 ? Math.Min(upstreamMaxMbps.Value, downstreamMaxMbps.Value) : null;
}
