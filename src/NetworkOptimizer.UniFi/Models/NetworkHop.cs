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

    /// <summary>Firmware version of the device at test time</summary>
    public string? DeviceFirmware { get; set; }

    /// <summary>
    /// Whether MLO (Multi-Link Operation) is enabled on any SSID served by this AP.
    /// Only populated for AccessPoint hop types.
    /// </summary>
    public bool? MloEnabled { get; set; }

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

    /// <summary>Radio band for wireless ingress (ng=2.4GHz, na=5GHz, 6e=6GHz)</summary>
    public string? WirelessIngressBand { get; set; }

    /// <summary>Radio band for wireless egress (ng=2.4GHz, na=5GHz, 6e=6GHz)</summary>
    public string? WirelessEgressBand { get; set; }

    /// <summary>Channel for wireless link</summary>
    public int? WirelessChannel { get; set; }

    /// <summary>Signal strength in dBm for wireless link</summary>
    public int? WirelessSignalDbm { get; set; }

    /// <summary>Noise floor in dBm for wireless link</summary>
    public int? WirelessNoiseDbm { get; set; }

    /// <summary>TX rate in Mbps for wireless link (from device to uplink)</summary>
    public int? WirelessTxRateMbps { get; set; }

    /// <summary>RX rate in Mbps for wireless link (from uplink to device)</summary>
    public int? WirelessRxRateMbps { get; set; }

    /// <summary>Additional notes (e.g., "L3 routing", "Wireless uplink")</summary>
    public string? Notes { get; set; }
}

/// <summary>
/// Type of network hop
/// </summary>
public enum HopType
{
    /// <summary>Wired client endpoint (desktop)</summary>
    Client,

    /// <summary>L2 switch</summary>
    Switch,

    /// <summary>Wireless access point</summary>
    AccessPoint,

    /// <summary>Gateway/router (L3 routing)</summary>
    Gateway,

    /// <summary>The iperf3/speed test server (this application)</summary>
    Server,

    /// <summary>Wireless client endpoint (laptop)</summary>
    WirelessClient,

    /// <summary>Teleport VPN gateway (external VPN)</summary>
    Teleport,

    /// <summary>Tailscale VPN (CGNAT mesh)</summary>
    Tailscale,

    /// <summary>WAN/Internet (external IP not in local network)</summary>
    Wan,

    /// <summary>Generic VPN (UniFi remote-user-vpn network)</summary>
    Vpn
}
