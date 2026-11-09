namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Channel scan results showing RF environment
/// </summary>
public class ChannelScanResult
{
    /// <summary>AP MAC that performed the scan</summary>
    public string ApMac { get; set; } = string.Empty;

    /// <summary>AP name</summary>
    public string? ApName { get; set; }

    /// <summary>Radio band scanned</summary>
    public RadioBand Band { get; set; }

    /// <summary>When we FETCHED this scan (not when the radio measured it).</summary>
    public DateTimeOffset ScanTime { get; set; }

    /// <summary>
    /// When the AP's per-channel spectrum scan was actually TAKEN, from UniFi's
    /// <c>spectrum_table_time</c>. This is the true age of the measured airtime/noise-floor data in
    /// <see cref="Channels"/> - distinct from <see cref="ScanTime"/> (our fetch moment), which stays
    /// "now" even when the underlying scan is hours old. Null when the controller reported no
    /// timestamp or the band was never scanned. Diagnostics only today (how stale the scan feeding a
    /// recommendation is); the neighbor (rogue) sighting recency lives on
    /// <see cref="NeighborNetwork.LastSeen"/> instead.
    /// </summary>
    public DateTimeOffset? SpectrumTableTime { get; set; }

    /// <summary>Per-channel scan data</summary>
    public List<ChannelInfo> Channels { get; set; } = new();

    /// <summary>Neighboring networks detected</summary>
    public List<NeighborNetwork> Neighbors { get; set; } = new();
}

/// <summary>
/// Information about a single channel from scan
/// </summary>
public class ChannelInfo
{
    /// <summary>Channel number</summary>
    public int Channel { get; set; }

    /// <summary>Channel width in MHz</summary>
    public int? Width { get; set; }

    /// <summary>Center frequency in MHz</summary>
    public int? CenterFrequency { get; set; }

    /// <summary>Channel utilization percentage (0-100)</summary>
    public int? Utilization { get; set; }

    /// <summary>Interference level (0-100)</summary>
    public int? Interference { get; set; }

    /// <summary>Noise floor in dBm</summary>
    public int? NoiseFloor { get; set; }

    /// <summary>Whether this is a DFS channel</summary>
    public bool IsDfs { get; set; }

    /// <summary>DFS state (available, unavailable, cac, etc.)</summary>
    public string? DfsState { get; set; }

    /// <summary>Number of neighboring networks on this channel</summary>
    public int NeighborCount { get; set; }

    /// <summary>Whether this AP is currently using this channel</summary>
    public bool IsCurrentChannel { get; set; }

    /// <summary>
    /// Quality score for this channel (computed).
    /// Higher is better. Based on utilization, interference, neighbor count.
    /// </summary>
    public int? QualityScore { get; set; }
}

/// <summary>
/// A neighboring Wi-Fi network detected during scan
/// </summary>
public class NeighborNetwork
{
    /// <summary>SSID (may be empty for hidden networks)</summary>
    public string Ssid { get; set; } = string.Empty;

    /// <summary>BSSID (MAC address)</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>Channel number</summary>
    public int Channel { get; set; }

    /// <summary>Channel width if detected</summary>
    public int? Width { get; set; }

    /// <summary>Signal strength in dBm</summary>
    public int? Signal { get; set; }

    /// <summary>
    /// One of this site's BSSIDs, so never external interference. Set together with
    /// <see cref="IsSiteBssid"/>; a neighbor's UniFi AP is not own.
    /// </summary>
    public bool IsOwnNetwork { get; set; }

    /// <summary>
    /// True when the BSSID is in a site access point's <c>vap_table</c> or derived from a site AP's
    /// MAC (mesh VAPs are missing from every <c>vap_table</c>). The scan's Ubiquiti flag plays no
    /// part: a neighbor's UniFi AP sets that flag too.
    /// </summary>
    public bool IsSiteBssid { get; set; }

    /// <summary>MAC of the site access point that owns this BSSID, when <see cref="IsSiteBssid"/>.</summary>
    public string? OwnApMac { get; set; }

    /// <summary>Name of the site access point that owns this BSSID, when <see cref="IsSiteBssid"/>.</summary>
    public string? OwnApName { get; set; }

    /// <summary>The owning VAP's ESSID, when <see cref="IsSiteBssid"/>. A mesh VAP's starts with <c>vwire-</c>.</summary>
    public string? OwnSsid { get; set; }

    /// <summary>Security type if detected</summary>
    public string? Security { get; set; }

    /// <summary>Last seen timestamp</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>OUI (manufacturer) resolved from BSSID</summary>
    public string? Oui { get; set; }

    /// <summary>
    /// How much this sighting should count for, in (0, 1]. 1.0 = a live scan sighting.
    /// Remembered sightings from the long-term neighbor memory enter with an age-decayed
    /// confidence, scaling their interference weight down as they age - and they never
    /// grant the full "directly observed" status a live sighting does.
    /// </summary>
    public double Confidence { get; set; } = 1.0;
}
