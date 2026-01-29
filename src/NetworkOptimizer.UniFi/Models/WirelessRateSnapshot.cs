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
}
