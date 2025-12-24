namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Represents the network path between the iperf3 server and a target device.
/// Used to calculate theoretical maximum throughput and identify bottlenecks.
/// </summary>
public class NetworkPath
{
    /// <summary>Source endpoint (the iperf3 server/container)</summary>
    public string SourceHost { get; set; } = "";

    /// <summary>MAC address of the source</summary>
    public string SourceMac { get; set; } = "";

    /// <summary>VLAN ID of the source network</summary>
    public int? SourceVlanId { get; set; }

    /// <summary>Name of the source network/VLAN</summary>
    public string? SourceNetworkName { get; set; }

    /// <summary>Destination endpoint (the device being tested)</summary>
    public string DestinationHost { get; set; } = "";

    /// <summary>MAC address of the destination</summary>
    public string DestinationMac { get; set; } = "";

    /// <summary>VLAN ID of the destination network</summary>
    public int? DestinationVlanId { get; set; }

    /// <summary>Name of the destination network/VLAN</summary>
    public string? DestinationNetworkName { get; set; }

    /// <summary>Ordered list of hops from source to destination</summary>
    public List<NetworkHop> Hops { get; set; } = new();

    /// <summary>Whether traffic must traverse the gateway for L3 routing (inter-VLAN)</summary>
    public bool RequiresRouting { get; set; }

    /// <summary>Gateway device name if routing is required</summary>
    public string? GatewayDevice { get; set; }

    /// <summary>Gateway model for reference</summary>
    public string? GatewayModel { get; set; }

    /// <summary>
    /// Theoretical maximum throughput in Mbps.
    /// This is the minimum link speed found along the path.
    /// </summary>
    public int TheoreticalMaxMbps { get; set; }

    /// <summary>
    /// Realistic maximum throughput in Mbps.
    /// Accounts for protocol overhead (~6% for Ethernet/TCP).
    /// </summary>
    public int RealisticMaxMbps { get; set; }

    /// <summary>
    /// Human-readable description of the bottleneck.
    /// E.g., "100M link on Port 5 of Switch-Closet"
    /// </summary>
    public string? BottleneckDescription { get; set; }

    /// <summary>When this path was calculated</summary>
    public DateTime CalculatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Whether the path calculation succeeded</summary>
    public bool IsValid { get; set; } = true;

    /// <summary>Error message if path calculation failed</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Number of switch hops in the path
    /// </summary>
    public int SwitchHopCount => Hops.Count(h => h.Type == HopType.Switch);

    /// <summary>
    /// Whether the path includes wireless segments
    /// </summary>
    public bool HasWirelessSegment => Hops.Any(h => h.Type == HopType.AccessPoint);
}
