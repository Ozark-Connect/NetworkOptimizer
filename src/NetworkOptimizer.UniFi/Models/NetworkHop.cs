namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Represents a single hop in the network path between two endpoints
/// </summary>
public class NetworkHop
{
    /// <summary>Order in the path (0 = closest to source)</summary>
    public int Order { get; set; }

    /// <summary>Type of device at this hop</summary>
    public HopType Type { get; set; }

    /// <summary>MAC address of the device</summary>
    public string DeviceMac { get; set; } = "";

    /// <summary>Friendly name of the device</summary>
    public string DeviceName { get; set; } = "";

    /// <summary>Model of the device</summary>
    public string DeviceModel { get; set; } = "";

    /// <summary>IP address of the device</summary>
    public string DeviceIp { get; set; } = "";

    /// <summary>Port number where traffic enters this device</summary>
    public int? IngressPort { get; set; }

    /// <summary>Name of the ingress port</summary>
    public string? IngressPortName { get; set; }

    /// <summary>Link speed on ingress port (Mbps)</summary>
    public int IngressSpeedMbps { get; set; }

    /// <summary>Port number where traffic exits this device</summary>
    public int? EgressPort { get; set; }

    /// <summary>Name of the egress port</summary>
    public string? EgressPortName { get; set; }

    /// <summary>Link speed on egress port (Mbps)</summary>
    public int EgressSpeedMbps { get; set; }

    /// <summary>Whether this hop contains the path bottleneck</summary>
    public bool IsBottleneck { get; set; }

    /// <summary>Whether the ingress link is a wireless mesh uplink</summary>
    public bool IsWirelessIngress { get; set; }

    /// <summary>Whether the egress link is a wireless mesh uplink</summary>
    public bool IsWirelessEgress { get; set; }

    /// <summary>Additional notes (e.g., "L3 routing", "Wireless uplink")</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Type of network hop
/// </summary>
public enum HopType
{
    /// <summary>Client endpoint (source or destination)</summary>
    Client,

    /// <summary>L2 switch</summary>
    Switch,

    /// <summary>Wireless access point</summary>
    AccessPoint,

    /// <summary>Gateway/router (L3 routing)</summary>
    Gateway,

    /// <summary>The iperf3/speed test server (this application)</summary>
    Server
}
