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
    /// </summary>
    /// <param name="category">Chart category to open.</param>
    /// <param name="at">A Unix-ms instant, or <see cref="LiveAtToken"/>.</param>
    /// <param name="selectedWanKeys">The WANs on screen. Empty on a single-WAN site.</param>
    /// <param name="allSelected">Whether that selection is every WAN the site has.</param>
    public static string Analysis(
        string category, string at, IReadOnlyCollection<string> selectedWanKeys, bool allSelected)
    {
        var wan = category is FabricCategory or CustomCategory || allSelected
            ? LiveWanScope.AllWansToken
            : string.Join(",", selectedWanKeys);

        var wanQuery = selectedWanKeys.Count > 0 && wan.Length > 0
            ? $"&wan={Uri.EscapeDataString(wan)}"
            : "";
        return $"/monitoring?tab=performance&category={category}&at={at}{wanQuery}";
    }

    /// <summary>
    /// The ISP Health report for one WAN. The primary is named like any other: the destination
    /// remembers the WAN it was last left on, so leaving it unnamed opened that one instead.
    /// </summary>
    public static string IspHealth(string? wanKey) =>
        string.IsNullOrEmpty(wanKey)
            ? "/monitoring?tab=isp-health"
            : $"/monitoring?tab=isp-health&wan={Uri.EscapeDataString(wanKey)}";
}
