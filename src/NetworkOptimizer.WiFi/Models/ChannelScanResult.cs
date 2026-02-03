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

    /// <summary>When the scan was performed</summary>
    public DateTimeOffset ScanTime { get; set; }

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

    /// <summary>Whether this is a UniFi network (same site)</summary>
    public bool IsOwnNetwork { get; set; }

    /// <summary>Security type if detected</summary>
    public string? Security { get; set; }

    /// <summary>Last seen timestamp</summary>
    public DateTimeOffset? LastSeen { get; set; }
}
