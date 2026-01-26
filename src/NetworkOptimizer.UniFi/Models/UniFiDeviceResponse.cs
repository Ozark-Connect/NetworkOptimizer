using System.Text.Json.Serialization;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.UniFi.Models;

/// <summary>
/// Response from GET /api/s/{site}/stat/device
/// Represents a UniFi network device (AP, Switch, Gateway, etc.)
/// </summary>
public class UniFiDeviceResponse
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    /// <summary>
    /// Raw UniFi API type code (uap, usw, udm, etc.)
    /// Use DeviceType property for the normalized type constant.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Normalized device type enum value
    /// </summary>
    public DeviceType DeviceType => DeviceTypeExtensions.FromUniFiApiType(Type);

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Short model name like "UCG-Fiber", "USW-Enterprise-XG-24"
    /// This is the user-friendly product name
    /// </summary>
    [JsonPropertyName("shortname")]
    public string? Shortname { get; set; }

    /// <summary>
    /// Model in long-term support (legacy field)
    /// </summary>
    [JsonPropertyName("model_in_lts")]
    public bool? ModelInLts { get; set; }

    /// <summary>
    /// Model in end-of-life (legacy field)
    /// </summary>
    [JsonPropertyName("model_in_eol")]
    public bool? ModelInEol { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets the best available friendly product name using the product database lookup
    /// </summary>
    public string FriendlyModelName =>
        UniFiProductDatabase.GetBestProductName(Model, Shortname);

    /// <summary>
    /// Whether this device can run iperf3 for LAN speed testing
    /// </summary>
    public bool CanRunIperf3 =>
        UniFiProductDatabase.CanRunIperf3(FriendlyModelName);

    [JsonPropertyName("ip")]
    public string Ip { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>
    /// User-friendly firmware version string (e.g., "4.0.6" instead of internal build number)
    /// </summary>
    [JsonPropertyName("displayable_version")]
    public string? DisplayableVersion { get; set; }

    [JsonPropertyName("adopted")]
    public bool Adopted { get; set; }

    [JsonPropertyName("state")]
    public int State { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("last_seen")]
    public long LastSeen { get; set; }

    [JsonPropertyName("upgradable")]
    public bool Upgradable { get; set; }

    [JsonPropertyName("upgrade_to_firmware")]
    public string? UpgradeToFirmware { get; set; }

    [JsonPropertyName("two_phase_adopt")]
    public bool? TwoPhaseAdopt { get; set; }

    [JsonPropertyName("unsupported")]
    public bool? Unsupported { get; set; }

    [JsonPropertyName("unsupported_reason")]
    public int? UnsupportedReason { get; set; }

    // Network-specific properties
    [JsonPropertyName("ethernet_table")]
    public List<EthernetPort>? EthernetTable { get; set; }

    [JsonPropertyName("port_table")]
    public List<SwitchPort>? PortTable { get; set; }

    [JsonPropertyName("uplink")]
    public UplinkInfo? Uplink { get; set; }

    // Stats
    [JsonPropertyName("stat")]
    public DeviceStats? Stats { get; set; }

    [JsonPropertyName("sys_stats")]
    public SystemStats? SystemStats { get; set; }

    // Configuration
    [JsonPropertyName("config_network")]
    public ConfigNetwork? ConfigNetwork { get; set; }

    /// <summary>
    /// LAN network configuration - only present on devices acting as the network gateway.
    /// UDM-family devices (including UX Express) won't have this when operating as APs.
    /// </summary>
    [JsonPropertyName("config_network_lan")]
    public ConfigNetworkLan? ConfigNetworkLan { get; set; }
}

public class EthernetPort
{
    [JsonPropertyName("mac")]
    public string Mac { get; set; } = string.Empty;

    [JsonPropertyName("num_port")]
    public int NumPort { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class SwitchPort
{
    [JsonPropertyName("port_idx")]
    public int PortIdx { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("port_poe")]
    public bool PortPoe { get; set; }

    [JsonPropertyName("poe_enable")]
    public bool PoeEnable { get; set; }

    [JsonPropertyName("poe_mode")]
    public string? PoeMode { get; set; }

    [JsonPropertyName("poe_power")]
    public string? PoePower { get; set; }

    [JsonPropertyName("poe_voltage")]
    public string? PoeVoltage { get; set; }

    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    [JsonPropertyName("up")]
    public bool Up { get; set; }

    [JsonPropertyName("enable")]
    public bool Enable { get; set; }

    [JsonPropertyName("media")]
    public string? Media { get; set; }

    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }

    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    [JsonPropertyName("tx_packets")]
    public long TxPackets { get; set; }

    [JsonPropertyName("rx_packets")]
    public long RxPackets { get; set; }

    /// <summary>
    /// Whether this port is an uplink (WAN) port.
    /// Present on gateway devices to identify WAN interfaces.
    /// </summary>
    [JsonPropertyName("is_uplink")]
    public bool IsUplink { get; set; }

    /// <summary>
    /// Network name for this port (e.g., "wan", "wan2", "lan").
    /// Used to identify which network/WAN interface the port belongs to.
    /// </summary>
    [JsonPropertyName("network_name")]
    public string? NetworkName { get; set; }

    // VLAN trunk configuration fields

    /// <summary>
    /// Port forwarding mode: "customize" = trunk, "native" = access port, "disabled" = disabled
    /// </summary>
    [JsonPropertyName("forward")]
    public string? Forward { get; set; }

    /// <summary>
    /// Tagged VLAN management: "custom" = trunk (allows specific VLANs), "block_all" = access (no tagged VLANs)
    /// </summary>
    [JsonPropertyName("tagged_vlan_mgmt")]
    public string? TaggedVlanMgmt { get; set; }

    /// <summary>
    /// Network config IDs excluded from this trunk port.
    /// Allowed VLANs = All Networks - ExcludedNetworkConfIds
    /// Empty list means all VLANs are allowed.
    /// </summary>
    [JsonPropertyName("excluded_networkconf_ids")]
    public List<string>? ExcludedNetworkConfIds { get; set; }

    /// <summary>
    /// Native VLAN network config ID for this port
    /// </summary>
    [JsonPropertyName("native_networkconf_id")]
    public string? NativeNetworkConfId { get; set; }

    /// <summary>
    /// Port profile ID if a port profile is assigned to this port.
    /// When set, the port profile settings override the port's direct settings.
    /// </summary>
    [JsonPropertyName("portconf_id")]
    public string? PortConfId { get; set; }
}

public class UplinkInfo
{
    [JsonPropertyName("uplink_mac")]
    public string UplinkMac { get; set; } = string.Empty;

    [JsonPropertyName("uplink_remote_port")]
    public int UplinkRemotePort { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("up")]
    public bool Up { get; set; }

    [JsonPropertyName("speed")]
    public int Speed { get; set; }

    [JsonPropertyName("full_duplex")]
    public bool FullDuplex { get; set; }

    /// <summary>
    /// TX rate for wireless uplinks in Kbps
    /// </summary>
    [JsonPropertyName("tx_rate")]
    public long TxRate { get; set; }

    /// <summary>
    /// RX rate for wireless uplinks in Kbps
    /// </summary>
    [JsonPropertyName("rx_rate")]
    public long RxRate { get; set; }

    /// <summary>
    /// Radio band for wireless uplinks (ng=2.4GHz, na=5GHz, 6e=6GHz)
    /// </summary>
    [JsonPropertyName("radio")]
    public string? RadioBand { get; set; }

    /// <summary>
    /// Channel for wireless uplinks
    /// </summary>
    [JsonPropertyName("channel")]
    public int? Channel { get; set; }

    /// <summary>
    /// Whether this is a Multi-Link Operation (MLO) connection (Wi-Fi 7)
    /// </summary>
    [JsonPropertyName("is_mlo")]
    public bool? IsMlo { get; set; }

    /// <summary>
    /// Signal strength in dBm for wireless uplinks
    /// </summary>
    [JsonPropertyName("signal")]
    public int? Signal { get; set; }

    /// <summary>
    /// Noise floor in dBm for wireless uplinks
    /// </summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }
}

public class DeviceStats
{
    [JsonPropertyName("tx_bytes")]
    public long TxBytes { get; set; }

    [JsonPropertyName("rx_bytes")]
    public long RxBytes { get; set; }

    [JsonPropertyName("tx_packets")]
    public long TxPackets { get; set; }

    [JsonPropertyName("rx_packets")]
    public long RxPackets { get; set; }
}

public class SystemStats
{
    [JsonPropertyName("cpu")]
    public string? Cpu { get; set; }

    [JsonPropertyName("mem")]
    public string? Mem { get; set; }

    [JsonPropertyName("uptime")]
    public string? Uptime { get; set; }

    [JsonPropertyName("loadavg_1")]
    public double? LoadAvg1 { get; set; }

    [JsonPropertyName("loadavg_5")]
    public double? LoadAvg5 { get; set; }

    [JsonPropertyName("loadavg_15")]
    public double? LoadAvg15 { get; set; }
}

public class ConfigNetwork
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }
}

/// <summary>
/// LAN network configuration - only present on gateway devices that manage networks.
/// Used to distinguish actual gateways from UDM-family devices operating as APs.
/// </summary>
public class ConfigNetworkLan
{
    [JsonPropertyName("dhcp_enabled")]
    public bool? DhcpEnabled { get; set; }

    [JsonPropertyName("cidr")]
    public string? Cidr { get; set; }
}

/// <summary>
/// Port override configuration from device port_overrides array.
/// Contains per-port VLAN and profile configuration that may differ from defaults.
/// </summary>
public class PortOverride
{
    [JsonPropertyName("port_idx")]
    public int PortIdx { get; set; }

    [JsonPropertyName("native_networkconf_id")]
    public string? NativeNetworkConfId { get; set; }

    [JsonPropertyName("voice_networkconf_id")]
    public string? VoiceNetworkConfId { get; set; }

    [JsonPropertyName("tagged_networkconf_ids")]
    public List<string>? TaggedNetworkConfIds { get; set; }

    [JsonPropertyName("portconf_id")]
    public string? PortConfId { get; set; }
}
