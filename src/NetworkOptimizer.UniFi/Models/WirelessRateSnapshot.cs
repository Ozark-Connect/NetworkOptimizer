namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// A snapshot of wireless rates captured during a speed test.
/// Used to compare with current rates and pick the highest values.
/// </summary>
public class WirelessRateSnapshot
{
    /// <summary>Wireless client rates keyed by MAC address (TxKbps, RxKbps from AP's perspective: Tx=ToDevice, Rx=FromDevice, ApMac for roam detection)</summary>
    public Dictionary<string, (long TxKbps, long RxKbps, string? ApMac)> ClientRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Mesh device uplink rates keyed by MAC address (TxKbps, RxKbps from child AP's perspective)</summary>
    public Dictionary<string, (long TxKbps, long RxKbps)> MeshUplinkRates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>WiFiman-sourced client info keyed by IP (band as UniFi radio code, channel)</summary>
    public Dictionary<string, WiFiManClientInfo> WiFiManData { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// WiFiman client data captured during a speed test snapshot.
/// </summary>
public class WiFiManClientInfo
{
    /// <summary>TX rate in Kbps (client upload → AP RX perspective, mapped from WiFiman LinkUploadRateKbps)</summary>
    public long TxKbps { get; set; }

    /// <summary>RX rate in Kbps (client download → AP TX perspective, mapped from WiFiman LinkDownloadRateKbps)</summary>
    public long RxKbps { get; set; }

    /// <summary>Radio band as UniFi code (ng/na/6e)</summary>
    public string? Band { get; set; }

    /// <summary>Channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz</summary>
    public int? ChannelWidth { get; set; }
}
