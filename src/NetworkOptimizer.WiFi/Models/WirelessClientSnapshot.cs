namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Point-in-time snapshot of a wireless client's connection state
/// </summary>
public class WirelessClientSnapshot
{
    /// <summary>Client MAC address</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>Client hostname or display name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Client IP address</summary>
    public string? Ip { get; set; }

    /// <summary>Connected AP MAC address</summary>
    public string ApMac { get; set; } = string.Empty;

    /// <summary>Connected AP name</summary>
    public string? ApName { get; set; }

    /// <summary>SSID connected to</summary>
    public string Essid { get; set; } = string.Empty;

    /// <summary>Radio band</summary>
    public RadioBand Band { get; set; }

    /// <summary>Channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz (20, 40, 80, 160, 320)</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Signal strength in dBm</summary>
    public int? Signal { get; set; }

    /// <summary>Noise floor in dBm</summary>
    public int? Noise { get; set; }

    /// <summary>Signal-to-noise ratio (calculated if noise available)</summary>
    public int? Snr => Signal.HasValue && Noise.HasValue ? Signal.Value - Noise.Value : null;

    /// <summary>RSSI (often same as signal)</summary>
    public int? Rssi { get; set; }

    /// <summary>Client satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Wi-Fi protocol (ac, ax, be, etc.)</summary>
    public string? WifiProtocol { get; set; }

    /// <summary>Wi-Fi generation (4, 5, 6, 6E, 7)</summary>
    public int? WifiGeneration { get; set; }

    /// <summary>PHY rate in bps (theoretical max)</summary>
    public long? PhyRate { get; set; }

    /// <summary>TX rate in Kbps</summary>
    public long? TxRate { get; set; }

    /// <summary>RX rate in Kbps</summary>
    public long? RxRate { get; set; }

    /// <summary>TX bytes since connection</summary>
    public long? TxBytes { get; set; }

    /// <summary>RX bytes since connection</summary>
    public long? RxBytes { get; set; }

    /// <summary>TX retries</summary>
    public long? TxRetries { get; set; }

    /// <summary>Connection uptime in seconds</summary>
    public long? Uptime { get; set; }

    /// <summary>Whether client is authorized (not blocked)</summary>
    public bool IsAuthorized { get; set; } = true;

    /// <summary>Whether client is a guest</summary>
    public bool IsGuest { get; set; }

    /// <summary>Whether client is currently online (connected)</summary>
    public bool IsOnline { get; set; } = true;

    /// <summary>Last seen timestamp (for offline clients)</summary>
    public DateTimeOffset? LastSeen { get; set; }

    /// <summary>Device manufacturer from OUI lookup</summary>
    public string? Manufacturer { get; set; }

    /// <summary>When this snapshot was taken</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Client capability flags discovered from connection
    /// </summary>
    public ClientCapabilities Capabilities { get; set; } = new();
}

/// <summary>
/// Client wireless capabilities
/// </summary>
public class ClientCapabilities
{
    /// <summary>Supports 2.4 GHz</summary>
    public bool Supports2_4GHz { get; set; }

    /// <summary>Supports 5 GHz</summary>
    public bool Supports5GHz { get; set; }

    /// <summary>Supports 6 GHz</summary>
    public bool Supports6GHz { get; set; }

    /// <summary>Maximum supported Wi-Fi generation</summary>
    public int? MaxWifiGeneration { get; set; }

    /// <summary>Supports 802.11r fast roaming</summary>
    public bool? Supports11r { get; set; }

    /// <summary>Supports 802.11k neighbor reports</summary>
    public bool? Supports11k { get; set; }

    /// <summary>Supports 802.11v BSS transition</summary>
    public bool? Supports11v { get; set; }

    /// <summary>Maximum spatial streams</summary>
    public int? MaxNss { get; set; }

    /// <summary>Maximum channel width supported</summary>
    public int? MaxChannelWidth { get; set; }
}
