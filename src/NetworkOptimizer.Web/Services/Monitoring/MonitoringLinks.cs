namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// The links the Live surfaces build into the analysis views.
/// <para>
/// One place because there are TWO Live surfaces showing the same tiles - the Monitoring Live View
/// tab and the dashboard's Live View panel - and while each built its own links they drifted. The
/// panel went on opening whichever WAN ISP Health was last left on for as long as it took someone
/// to notice, and its stat tiles never carried a WAN at all. A tile that means the same thing on
/// two pages has to land in the same place from both.
/// </para>
/// </summary>
public static class MonitoringLinks
{
    /// <summary>Chart categories, as the Latency &amp; Packet Loss module names them.</summary>
    public const string FabricCategory = "Fabric";
    public const string AccessIspCategory = "AccessIsp";
    public const string TransitCategory = "Transit";
    public const string CustomCategory = "Custom";

    /// <summary>
    /// The <c>?at=</c> value meaning the view was live rather than parked on an instant, which
    /// lands the analysis on a trailing window instead of one frozen at the moment of the click.
    /// </summary>
    public const string LiveAtToken = "live";

    /// <summary>
    /// Latency and Packet Loss for a category, at a moment, scoped to the WANs on screen.
    /// <para>
    /// LAN and Custom targets are not reached over any one WAN, so those views ask for all of them
    /// rather than narrowing to whichever WAN the tiles happened to be showing.
    /// </para>
    /// <para>
    /// The WAN fragment is asked of the scope rather than rebuilt here. This method once took the
    /// selection apart and reassembled it, which drifted from the scope's own rule: it emitted a
    /// filter whenever a key was selected, where the scope emits one only when the site has a
    /// choice worth carrying. Two spellings of one rule is what this class exists to prevent, so
    /// it does not get to keep a second copy of that one either.
    /// </para>
    /// </summary>
    /// <param name="category">Chart category to open.</param>
    /// <param name="at">A Unix-ms instant, or <see cref="LiveAtToken"/>.</param>
    /// <param name="wanScope">The WAN selection on screen, which writes its own query fragment.</param>
    public static string Analysis(string category, string at, LiveWanScope wanScope)
    {
        var spansEveryWan = category is FabricCategory or CustomCategory
            ? LiveWanScope.AllWansToken
            : null;
        return $"/monitoring?tab=performance&category={category}&at={at}"
            + wanScope.QueryFragment(spansEveryWan);
    }

    /// <summary>
    /// Latency and Packet Loss on a LAN target - the gateway's own fabric row, from a live tile.
    /// Asks for every WAN the way any LAN view does: the LAN categories are only offered in the All
    /// scope, so a link that named no WAN landed in whatever scope the destination was last left in
    /// and the chart moved off the category - and the target - the link had asked for.
    /// </summary>
    public static string FabricTarget(string? targetId, LiveWanScope wanScope) =>
        $"/monitoring?tab=performance&category={FabricCategory}"
        + (string.IsNullOrEmpty(targetId) ? "" : $"&target={Uri.EscapeDataString(targetId)}")
        + wanScope.QueryFragment(LiveWanScope.AllWansToken);

    /// <summary>
    /// The ISP Health report for one WAN. The primary is named like any other: the destination
    /// remembers the WAN it was last left on, so leaving it unnamed opened that one instead.
    /// </summary>
    public static string IspHealth(string? wanKey) =>
        string.IsNullOrEmpty(wanKey)
            ? "/monitoring?tab=isp-health"
            : $"/monitoring?tab=isp-health&wan={Uri.EscapeDataString(wanKey)}";
}
