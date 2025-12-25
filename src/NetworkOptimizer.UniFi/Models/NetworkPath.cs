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
    /// Whether the path includes wireless segments (any AP)
    /// </summary>
    public bool HasWirelessSegment => Hops.Any(h => h.Type == HopType.AccessPoint);

    /// <summary>
    /// Whether the path includes an actual wireless connection:
    /// - Client followed by AP (wireless client)
    /// - AP followed by AP (wireless backhaul)
    /// Does NOT trigger for AP followed by Switch (wired uplink)
    /// </summary>
    public bool HasWirelessConnection
    {
        get
        {
            for (int i = 0; i < Hops.Count - 1; i++)
            {
                var current = Hops[i].Type;
                var next = Hops[i + 1].Type;

                // Client -> AP = wireless client connection
                if (current == HopType.Client && next == HopType.AccessPoint)
                    return true;

                // AP -> AP = wireless backhaul
                if (current == HopType.AccessPoint && next == HopType.AccessPoint)
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Whether there's a real bottleneck (a link slower than others in the path)
    /// </summary>
    public bool HasRealBottleneck { get; set; }

    /// <summary>
    /// Whether the target is a gateway device.
    /// Gateway tests have inherent CPU overhead and will show lower efficiency.
    /// </summary>
    public bool TargetIsGateway { get; set; }

    /// <summary>
    /// Whether the target is an access point.
    /// AP tests are CPU-limited; speeds above ~4.5 Gbps are considered good.
    /// </summary>
    public bool TargetIsAccessPoint { get; set; }
}
