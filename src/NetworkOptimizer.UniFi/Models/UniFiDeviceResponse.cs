using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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
    /// Normalized device type enum value.
    /// Uses model-based filtering to exclude smart power devices (USP-Strip, etc.)
    /// from being classified as AccessPoints.
    /// </summary>
    public DeviceType DeviceType => DeviceTypeExtensions.FromUniFiApiType(Type, Model);

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

    // Wi-Fi specific (APs only)
    /// <summary>
    /// Radio configuration table - per-radio settings (channel, tx_power, antenna)
    /// Only present on access points.
    /// </summary>
    [JsonPropertyName("radio_table")]
    public List<RadioTableEntry>? RadioTable { get; set; }

    /// <summary>
    /// Radio statistics table - per-radio runtime stats (satisfaction, tx_retries)
    /// Only present on access points.
    /// </summary>
    [JsonPropertyName("radio_table_stats")]
    public List<RadioTableStats>? RadioTableStats { get; set; }

    /// <summary>
    /// Antenna table - available antenna modes (Internal, OMNI, etc.)
    /// Only present on outdoor APs with switchable antenna modes.
    /// </summary>
    [JsonPropertyName("antenna_table")]
    public List<AntennaTableEntry>? AntennaTable { get; set; }

    /// <summary>
    /// Virtual AP table - per-SSID/radio statistics
    /// Only present on access points.
    /// </summary>
    [JsonPropertyName("vap_table")]
    public List<VapTableEntry>? VapTable { get; set; }

    /// <summary>
    /// Device satisfaction score (0-100). Higher is better.
    /// Represents overall Wi-Fi experience quality.
    /// </summary>
    [JsonPropertyName("satisfaction")]
    public int? Satisfaction { get; set; }

    /// <summary>
    /// Whether spectrum scanning is currently active on this device
    /// </summary>
    [JsonPropertyName("spectrum_scanning")]
    public bool? SpectrumScanning { get; set; }

    /// <summary>
    /// Whether quickscan is currently active on this device
    /// </summary>
    [JsonPropertyName("quickscan_scanning")]
    public bool? QuickscanScanning { get; set; }

    /// <summary>
    /// Scan radio table - results from RF environment scans.
    /// May contain a dedicated scan radio (radio: "scan") on supported APs.
    /// </summary>
    [JsonPropertyName("scan_radio_table")]
    public List<ScanRadioEntry>? ScanRadioTable { get; set; }

    /// <summary>
    /// Whether this AP has a dedicated scan radio that can scan without disrupting clients.
    /// APs with dedicated scan hardware have an entry in scan_radio_table with radio="scan".
    /// </summary>
    public bool HasDedicatedScanRadio =>
        ScanRadioTable?.Any(s => s.Radio?.Equals("scan", StringComparison.OrdinalIgnoreCase) == true) ?? false;

    /// <summary>
    /// Whether this AP supports spectrum/RF environment scanning.
    /// All modern APs with scan_radio_table support quickscan; APs with dedicated
    /// scan radio can scan without impacting client connectivity.
    /// </summary>
    public bool SupportsSpectrumScan => ScanRadioTable != null;

    // Configuration
    [JsonPropertyName("config_network")]
    public ConfigNetwork? ConfigNetwork { get; set; }

    /// <summary>
    /// LAN network configuration - only present on devices acting as the network gateway.
    /// UDM-family devices (including UX Express) won't have this when operating as APs.
    /// </summary>
    [JsonPropertyName("config_network_lan")]
    public ConfigNetworkLan? ConfigNetworkLan { get; set; }

    /// <summary>
    /// Whether hardware offload (packet acceleration) is enabled on the gateway.
    /// Only present on gateway devices.
    /// </summary>
    [JsonPropertyName("hardware_offload")]
    public bool? HardwareOffload { get; set; }

    /// <summary>
    /// Captures additional JSON properties not mapped to typed properties.
    /// Used to extract WAN interface objects (wan, wan1, wan2, etc.) which are dynamic keys.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    private static readonly Regex WanKeyPattern = new(@"^wan\d*$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts WAN interface objects from AdditionalData.
    /// Matches any key starting with "wan" followed by optional digits (wan, wan1, wan2, wan3, etc.)
    /// </summary>
    public List<GatewayWanInterface> GetWanInterfaces()
    {
        var result = new List<GatewayWanInterface>();
        if (AdditionalData == null)
            return result;

        foreach (var kvp in AdditionalData)
        {
            if (!WanKeyPattern.IsMatch(kvp.Key))
                continue;

            if (kvp.Value.ValueKind != JsonValueKind.Object)
                continue;

            try
            {
                var info = JsonSerializer.Deserialize<GatewayWanInterface>(kvp.Value.GetRawText());
                if (info != null)
                {
                    info.Key = kvp.Key;
                    result.Add(info);
                }
            }
            catch (JsonException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to deserialize WAN interface '{kvp.Key}': {ex.Message}");
            }
        }

        return result;
    }
}

/// <summary>
/// Represents a WAN interface from the gateway device JSON.
/// These appear as dynamic keys (wan, wan1, wan2, wan3, etc.) on the device object.
/// </summary>
public class GatewayWanInterface
{
    /// <summary>
    /// The JSON key this was parsed from (e.g., "wan1", "wan2")
    /// </summary>
    [JsonIgnore]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// WAN type: "ethernet", "wireless_5g", "lte", "wireless_lte"
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("up")]
    public bool Up { get; set; }

    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

    [JsonPropertyName("latency")]
    public int? Latency { get; set; }

    [JsonPropertyName("availability")]
    public int? Availability { get; set; }

    /// <summary>
    /// Whether this is a cellular WAN interface (5G, LTE)
    /// </summary>
    public bool IsCellular => Type is "wireless_5g" or "lte" or "wireless_lte";
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

    /// <summary>
    /// Whether auto-negotiation is enabled for this port.
    /// When false, speed is forced/manually configured.
    /// </summary>
    [JsonPropertyName("autoneg")]
    public bool Autoneg { get; set; } = true;

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
    /// IP address assigned to this port (present on gateway WAN ports).
    /// For WAN ports this is the public-facing IP from DHCP or static config.
    /// </summary>
    [JsonPropertyName("ip")]
    public string? Ip { get; set; }

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

    /// <summary>
    /// Whether port security (MAC restriction) is enabled on this port.
    /// When true, only devices with MACs in PortSecurityMacAddresses can connect.
    /// </summary>
    [JsonPropertyName("port_security_enabled")]
    public bool PortSecurityEnabled { get; set; }

    /// <summary>
    /// List of MAC addresses allowed on this port when PortSecurityEnabled is true.
    /// </summary>
    [JsonPropertyName("port_security_mac_address")]
    public List<string>? PortSecurityMacAddresses { get; set; }
}

public class UplinkInfo
{
    [JsonPropertyName("uplink_mac")]
    public string UplinkMac { get; set; } = string.Empty;

    [JsonPropertyName("uplink_remote_port")]
    public int UplinkRemotePort { get; set; }

    /// <summary>
    /// Local port index on this device that connects to the upstream device.
    /// For wired uplinks, this is the physical port number. Not present for wireless uplinks.
    /// </summary>
    [JsonPropertyName("port_idx")]
    public int? PortIdx { get; set; }

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

/// <summary>
/// Radio configuration entry from radio_table - per-radio settings
/// </summary>
public class RadioTableEntry
{
    /// <summary>
    /// Radio identifier (wifi0, wifi1, wifi2)
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Radio band code: ng=2.4GHz, na=5GHz, 6e=6GHz
    /// </summary>
    [JsonPropertyName("radio")]
    public string Radio { get; set; } = string.Empty;

    /// <summary>
    /// Current channel number (or "auto")
    /// </summary>
    [JsonPropertyName("channel")]
    public object? Channel { get; set; }

    /// <summary>
    /// Channel width in MHz (20, 40, 80, 160)
    /// </summary>
    [JsonPropertyName("ht")]
    public int? ChannelWidth { get; set; }

    /// <summary>
    /// TX power mode: auto, medium, high, low, custom
    /// </summary>
    [JsonPropertyName("tx_power_mode")]
    public string? TxPowerMode { get; set; }

    /// <summary>
    /// Minimum TX power in dBm
    /// </summary>
    [JsonPropertyName("min_txpower")]
    public int? MinTxPower { get; set; }

    /// <summary>
    /// Maximum TX power in dBm
    /// </summary>
    [JsonPropertyName("max_txpower")]
    public int? MaxTxPower { get; set; }

    /// <summary>
    /// Number of spatial streams
    /// </summary>
    [JsonPropertyName("nss")]
    public int? Nss { get; set; }

    /// <summary>
    /// Antenna gain in dBi
    /// </summary>
    [JsonPropertyName("antenna_gain")]
    public int? AntennaGain { get; set; }

    /// <summary>
    /// Current antenna gain being used
    /// </summary>
    [JsonPropertyName("current_antenna_gain")]
    public int? CurrentAntennaGain { get; set; }

    /// <summary>
    /// Whether using built-in antenna
    /// </summary>
    [JsonPropertyName("builtin_antenna")]
    public bool? BuiltinAntenna { get; set; }

    /// <summary>
    /// Active antenna mode ID. Links to antenna_table[].id on the parent device.
    /// -1 means not applicable (indoor APs with "Combined" antenna only).
    /// </summary>
    [JsonPropertyName("antenna_id")]
    public int? AntennaId { get; set; }

    /// <summary>
    /// Whether min RSSI (client steering) is enabled
    /// </summary>
    [JsonPropertyName("min_rssi_enabled")]
    public bool? MinRssiEnabled { get; set; }

    /// <summary>
    /// Minimum RSSI threshold for client steering (dBm)
    /// </summary>
    [JsonPropertyName("min_rssi")]
    public int? MinRssi { get; set; }

    /// <summary>
    /// Whether Roaming Assistant (soft roaming via BSS transition) is enabled (5 GHz only)
    /// </summary>
    [JsonPropertyName("assisted_roaming_enabled")]
    public bool? AssistedRoamingEnabled { get; set; }

    /// <summary>
    /// Roaming Assistant RSSI threshold (dBm) - clients below this get BSS transition request
    /// </summary>
    [JsonPropertyName("assisted_roaming_rssi")]
    public int? AssistedRoamingRssi { get; set; }

    /// <summary>
    /// Whether hard noise floor is enabled
    /// </summary>
    [JsonPropertyName("hard_noise_floor_enabled")]
    public bool? HardNoiseFloorEnabled { get; set; }

    /// <summary>
    /// Whether DFS channels are available
    /// </summary>
    [JsonPropertyName("has_dfs")]
    public bool? HasDfs { get; set; }

    /// <summary>
    /// Whether FCC DFS is available
    /// </summary>
    [JsonPropertyName("has_fccdfs")]
    public bool? HasFccDfs { get; set; }

    /// <summary>
    /// Radio capabilities bitmask
    /// </summary>
    [JsonPropertyName("radio_caps")]
    public long? RadioCaps { get; set; }

    /// <summary>
    /// Radio capabilities bitmask (extended)
    /// </summary>
    [JsonPropertyName("radio_caps2")]
    public long? RadioCaps2 { get; set; }

    /// <summary>
    /// Whether the radio supports 802.11be (Wi-Fi 7).
    /// Required for MLO (Multi-Link Operation) support.
    /// </summary>
    [JsonPropertyName("is_11be")]
    public bool Is11Be { get; set; }
}

/// <summary>
/// Radio statistics entry from radio_table_stats - per-radio runtime metrics
/// </summary>
public class RadioTableStats
{
    /// <summary>
    /// Radio identifier (wifi0, wifi1, wifi2)
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Radio band code: ng=2.4GHz, na=5GHz, 6e=6GHz
    /// </summary>
    [JsonPropertyName("radio")]
    public string Radio { get; set; } = string.Empty;

    /// <summary>
    /// Current channel number
    /// </summary>
    [JsonPropertyName("channel")]
    public int? Channel { get; set; }

    /// <summary>
    /// Extension channel for 40MHz+ widths
    /// </summary>
    [JsonPropertyName("extchannel")]
    public int? ExtChannel { get; set; }

    /// <summary>
    /// Last channel before any change
    /// </summary>
    [JsonPropertyName("last_channel")]
    public int? LastChannel { get; set; }

    /// <summary>
    /// Current TX power in dBm
    /// </summary>
    [JsonPropertyName("tx_power")]
    public int? TxPower { get; set; }

    /// <summary>
    /// Satisfaction score for this radio (0-100)
    /// </summary>
    [JsonPropertyName("satisfaction")]
    public int? Satisfaction { get; set; }

    /// <summary>
    /// Number of connected clients on this radio
    /// </summary>
    [JsonPropertyName("num_sta")]
    public int? NumSta { get; set; }

    /// <summary>
    /// Total TX packets
    /// </summary>
    [JsonPropertyName("tx_packets")]
    public long? TxPackets { get; set; }

    /// <summary>
    /// TX retries count
    /// </summary>
    [JsonPropertyName("tx_retries")]
    public long? TxRetries { get; set; }

    /// <summary>
    /// TX retries as percentage of total TX
    /// </summary>
    [JsonPropertyName("tx_retries_pct")]
    public double? TxRetriesPct { get; set; }

    /// <summary>
    /// Channel utilization total (0-100)
    /// </summary>
    [JsonPropertyName("cu_total")]
    public int? CuTotal { get; set; }

    /// <summary>
    /// Channel utilization from self (0-100)
    /// </summary>
    [JsonPropertyName("cu_self_rx")]
    public int? CuSelfRx { get; set; }

    /// <summary>
    /// Channel utilization from self TX (0-100)
    /// </summary>
    [JsonPropertyName("cu_self_tx")]
    public int? CuSelfTx { get; set; }

    /// <summary>
    /// Interference level (0-100)
    /// </summary>
    [JsonPropertyName("interference")]
    public int? Interference { get; set; }

    /// <summary>
    /// Guest TX packets
    /// </summary>
    [JsonPropertyName("guest-tx_packets")]
    public long? GuestTxPackets { get; set; }

    /// <summary>
    /// Guest TX retries
    /// </summary>
    [JsonPropertyName("guest-tx_retries")]
    public long? GuestTxRetries { get; set; }

    /// <summary>
    /// User TX packets
    /// </summary>
    [JsonPropertyName("user-tx_packets")]
    public long? UserTxPackets { get; set; }

    /// <summary>
    /// User TX retries
    /// </summary>
    [JsonPropertyName("user-tx_retries")]
    public long? UserTxRetries { get; set; }
}

/// <summary>
/// Antenna mode entry from antenna_table - available antenna configurations.
/// Present on outdoor APs with switchable modes (Internal, OMNI, etc.)
/// </summary>
public class AntennaTableEntry
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("default")]
    public bool IsDefault { get; set; }

    /// <summary>Gain for wifi0 radio (typically 2.4 GHz)</summary>
    [JsonPropertyName("wifi0_gain")]
    public int? Wifi0Gain { get; set; }

    /// <summary>Gain for wifi1 radio (typically 5 GHz)</summary>
    [JsonPropertyName("wifi1_gain")]
    public int? Wifi1Gain { get; set; }

    /// <summary>Gain for wifi2 radio (typically 6 GHz)</summary>
    [JsonPropertyName("wifi2_gain")]
    public int? Wifi2Gain { get; set; }
}

/// <summary>
/// Virtual AP table entry - per-SSID/radio statistics
/// </summary>
public class VapTableEntry
{
    /// <summary>
    /// SSID name
    /// </summary>
    [JsonPropertyName("essid")]
    public string Essid { get; set; } = string.Empty;

    /// <summary>
    /// BSSID (MAC address of this VAP)
    /// </summary>
    [JsonPropertyName("bssid")]
    public string Bssid { get; set; } = string.Empty;

    /// <summary>
    /// Radio band code: ng=2.4GHz, na=5GHz, 6e=6GHz
    /// </summary>
    [JsonPropertyName("radio")]
    public string Radio { get; set; } = string.Empty;

    /// <summary>
    /// Radio name (wifi0, wifi1, wifi2)
    /// </summary>
    [JsonPropertyName("radio_name")]
    public string? RadioName { get; set; }

    /// <summary>
    /// Channel number
    /// </summary>
    [JsonPropertyName("channel")]
    public int? Channel { get; set; }

    /// <summary>
    /// Extension channel for 40MHz+ widths
    /// </summary>
    [JsonPropertyName("extchannel")]
    public int? ExtChannel { get; set; }

    /// <summary>
    /// TX power in dBm
    /// </summary>
    [JsonPropertyName("tx_power")]
    public int? TxPower { get; set; }

    /// <summary>
    /// Usage state: "active" or other
    /// </summary>
    [JsonPropertyName("usage")]
    public string? Usage { get; set; }

    /// <summary>
    /// Number of connected clients on this VAP
    /// </summary>
    [JsonPropertyName("num_sta")]
    public int? NumSta { get; set; }

    /// <summary>
    /// Satisfaction score (0-100)
    /// </summary>
    [JsonPropertyName("satisfaction")]
    public int? Satisfaction { get; set; }

    /// <summary>
    /// Average client signal strength (dBm)
    /// </summary>
    [JsonPropertyName("avg_client_signal")]
    public int? AvgClientSignal { get; set; }

    /// <summary>
    /// Whether this is a guest network
    /// </summary>
    [JsonPropertyName("is_guest")]
    public bool? IsGuest { get; set; }

    /// <summary>
    /// Network configuration ID
    /// </summary>
    [JsonPropertyName("networkconf_id")]
    public string? NetworkConfId { get; set; }

    // Traffic stats
    [JsonPropertyName("rx_bytes")]
    public long? RxBytes { get; set; }

    [JsonPropertyName("tx_bytes")]
    public long? TxBytes { get; set; }

    [JsonPropertyName("rx_packets")]
    public long? RxPackets { get; set; }

    [JsonPropertyName("tx_packets")]
    public long? TxPackets { get; set; }

    [JsonPropertyName("rx_errors")]
    public long? RxErrors { get; set; }

    [JsonPropertyName("tx_errors")]
    public long? TxErrors { get; set; }

    [JsonPropertyName("rx_dropped")]
    public long? RxDropped { get; set; }

    [JsonPropertyName("tx_dropped")]
    public long? TxDropped { get; set; }

    /// <summary>
    /// TX retries count
    /// </summary>
    [JsonPropertyName("tx_retries")]
    public long? TxRetries { get; set; }

    /// <summary>
    /// WiFi TX attempts
    /// </summary>
    [JsonPropertyName("wifi_tx_attempts")]
    public long? WifiTxAttempts { get; set; }

    /// <summary>
    /// WiFi TX dropped
    /// </summary>
    [JsonPropertyName("wifi_tx_dropped")]
    public long? WifiTxDropped { get; set; }

    /// <summary>
    /// TX latency moving average stats
    /// </summary>
    [JsonPropertyName("wifi_tx_latency_mov")]
    public WifiTxLatency? WifiTxLatencyMov { get; set; }
}

/// <summary>
/// WiFi TX latency statistics
/// </summary>
public class WifiTxLatency
{
    [JsonPropertyName("avg")]
    public double? Avg { get; set; }

    [JsonPropertyName("min")]
    public double? Min { get; set; }

    [JsonPropertyName("max")]
    public double? Max { get; set; }

    [JsonPropertyName("total")]
    public long? Total { get; set; }

    [JsonPropertyName("total_count")]
    public long? TotalCount { get; set; }
}

/// <summary>
/// Scan radio entry from scan_radio_table - RF environment scan results
/// </summary>
public class ScanRadioEntry
{
    /// <summary>
    /// Radio name (wifi0, wifi1, wifi2, wifi3 for dedicated scan radio)
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Radio band code: ng=2.4GHz, na=5GHz, 6e=6GHz, or "scan" for dedicated scan radio
    /// </summary>
    [JsonPropertyName("radio")]
    public string Radio { get; set; } = string.Empty;

    /// <summary>
    /// Scanning state
    /// </summary>
    [JsonPropertyName("scanning")]
    public bool? Scanning { get; set; }

    /// <summary>
    /// Radio capabilities bitmask
    /// </summary>
    [JsonPropertyName("radio_caps")]
    public long? RadioCaps { get; set; }

    /// <summary>
    /// Radio capabilities bitmask (extended)
    /// </summary>
    [JsonPropertyName("radio_caps2")]
    public long? RadioCaps2 { get; set; }

    /// <summary>
    /// Spectrum table with per-channel scan results
    /// </summary>
    [JsonPropertyName("spectrum_table")]
    public List<SpectrumEntry>? SpectrumTable { get; set; }
}

/// <summary>
/// Spectrum entry with per-channel RF environment data
/// </summary>
public class SpectrumEntry
{
    /// <summary>
    /// Channel number
    /// </summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>
    /// Channel width in MHz
    /// </summary>
    [JsonPropertyName("width")]
    public int? Width { get; set; }

    /// <summary>
    /// Channel utilization percentage
    /// </summary>
    [JsonPropertyName("utilization")]
    public int? Utilization { get; set; }

    /// <summary>
    /// Interference level
    /// </summary>
    [JsonPropertyName("interference")]
    public int? Interference { get; set; }

    /// <summary>
    /// Whether this is a DFS channel
    /// </summary>
    [JsonPropertyName("is_dfs")]
    public bool? IsDfs { get; set; }

    /// <summary>
    /// DFS state if applicable
    /// </summary>
    [JsonPropertyName("dfs_state")]
    public string? DfsState { get; set; }
}

/// <summary>
/// Response from GET /api/s/{site}/stat/rogueap - Neighboring Wi-Fi networks detected by APs
/// </summary>
public class UniFiRogueApResponse
{
    /// <summary>
    /// SSID of the neighboring network (may be empty for hidden networks)
    /// </summary>
    [JsonPropertyName("essid")]
    public string Essid { get; set; } = string.Empty;

    /// <summary>
    /// BSSID (MAC address) of the neighboring network
    /// </summary>
    [JsonPropertyName("bssid")]
    public string Bssid { get; set; } = string.Empty;

    /// <summary>
    /// Channel number
    /// </summary>
    [JsonPropertyName("channel")]
    public int Channel { get; set; }

    /// <summary>
    /// Channel width in MHz
    /// </summary>
    [JsonPropertyName("bw")]
    public int? Width { get; set; }

    /// <summary>
    /// Signal strength in dBm
    /// </summary>
    [JsonPropertyName("signal")]
    public int? Signal { get; set; }

    /// <summary>
    /// RSSI value
    /// </summary>
    [JsonPropertyName("rssi")]
    public int? Rssi { get; set; }

    /// <summary>
    /// Noise floor in dBm
    /// </summary>
    [JsonPropertyName("noise")]
    public int? Noise { get; set; }

    /// <summary>
    /// MAC of the AP that detected this network
    /// </summary>
    [JsonPropertyName("ap_mac")]
    public string ApMac { get; set; } = string.Empty;

    /// <summary>
    /// Radio band code: ng=2.4GHz, na=5GHz, 6e=6GHz
    /// </summary>
    [JsonPropertyName("band")]
    public string Band { get; set; } = string.Empty;

    /// <summary>
    /// Radio code (ng, na, 6e)
    /// </summary>
    [JsonPropertyName("radio")]
    public string Radio { get; set; } = string.Empty;

    /// <summary>
    /// Whether this is a Ubiquiti device
    /// </summary>
    [JsonPropertyName("is_ubnt")]
    public bool IsUbnt { get; set; }

    /// <summary>
    /// Whether this is marked as rogue
    /// </summary>
    [JsonPropertyName("is_rogue")]
    public bool IsRogue { get; set; }

    /// <summary>
    /// Whether this is an ad-hoc network
    /// </summary>
    [JsonPropertyName("is_adhoc")]
    public bool IsAdhoc { get; set; }

    /// <summary>
    /// Security type description
    /// </summary>
    [JsonPropertyName("security")]
    public string? Security { get; set; }

    /// <summary>
    /// Last seen timestamp (Unix seconds)
    /// </summary>
    [JsonPropertyName("last_seen")]
    public long? LastSeen { get; set; }

    /// <summary>
    /// Report time (Unix seconds)
    /// </summary>
    [JsonPropertyName("report_time")]
    public long? ReportTime { get; set; }

    /// <summary>
    /// Center frequency in MHz
    /// </summary>
    [JsonPropertyName("center_freq")]
    public int? CenterFreq { get; set; }

    /// <summary>
    /// Frequency in MHz
    /// </summary>
    [JsonPropertyName("freq")]
    public int? Freq { get; set; }

    /// <summary>
    /// OUI (manufacturer) of the device
    /// </summary>
    [JsonPropertyName("oui")]
    public string? Oui { get; set; }

    /// <summary>
    /// Age of the reading in seconds
    /// </summary>
    [JsonPropertyName("age")]
    public int? Age { get; set; }
}
