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

    /// <summary>Whether this client is locked/pinned to a specific AP</summary>
    public bool FixedApEnabled { get; set; }

    /// <summary>MAC address of the AP this client is locked to (if FixedApEnabled)</summary>
    public string? FixedApMac { get; set; }

    /// <summary>Name of the AP this client is locked to (resolved from MAC)</summary>
    public string? FixedApName { get; set; }

    /// <summary>Device manufacturer from OUI lookup</summary>
    public string? Manufacturer { get; set; }

    /// <summary>When this snapshot was taken</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Client capability flags discovered from connection
    /// </summary>
    public ClientCapabilities Capabilities { get; set; } = new();

    /// <summary>Whether this client is connected over Wi-Fi 7 MLO (Multi-Link Operation)</summary>
    public bool IsMlo { get; set; }

    /// <summary>
    /// Signal at authentication in dBm, from the AP Agent. Null without an agent, and when the
    /// association predates it: the field is evidence, never a count.
    /// </summary>
    public int? JoinSignal { get; set; }

    /// <summary>How long the client has been associated, from the AP Agent.</summary>
    public TimeSpan? AssociatedFor { get; set; }

    /// <summary>BSS transition requests this association answered, from the AP Agent.</summary>
    public int? RoamNudges { get; set; }

    /// <summary>Of <see cref="RoamNudges"/>, those the client accepted.</summary>
    public int? RoamNudgesAccepted { get; set; }

    /// <summary>The width the client negotiated in MHz, from the AP Agent. The console reports the radio's.</summary>
    public int? NegotiatedWidth { get; set; }

    /// <summary>Median of the AP's transmit latency toward this client over the last hour, in ms, from the AP Agent.</summary>
    public double? MeasuredLatencyAvgMs { get; set; }

    /// <summary>TCP stalls toward this client in the last hour, from the AP Agent.</summary>
    public int? MeasuredTcpStalls { get; set; }

    /// <summary>
    /// Per-link breakdown of an MLO connection, empty for everything else. The scalar fields above
    /// describe the active link only, so an idle link never stands in for the whole connection.
    /// </summary>
    public List<MloLinkSnapshot> MloLinks { get; set; } = new();
}

/// <summary>
/// One radio link of a Wi-Fi 7 MLO connection. A client negotiates several links at once and can
/// leave some idle, so a link is part of one client rather than a client of its own.
/// </summary>
public class MloLinkSnapshot
{
    /// <summary>Per-link MAC address. Locally administered, so it is not the client's identity</summary>
    public string? Mac { get; set; }

    /// <summary>Radio band this link runs on</summary>
    public RadioBand Band { get; set; }

    /// <summary>Channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz (20, 40, 80, 160, 320)</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Signal strength in dBm</summary>
    public int? Signal { get; set; }

    /// <summary>Noise floor in dBm</summary>
    public int? Noise { get; set; }

    /// <summary>RSSI (often same as signal)</summary>
    public int? Rssi { get; set; }

    /// <summary>Spatial streams in use on this link</summary>
    public int? Nss { get; set; }

    /// <summary>TX rate in Kbps</summary>
    public long? TxRate { get; set; }

    /// <summary>RX rate in Kbps</summary>
    public long? RxRate { get; set; }

    /// <summary>Satisfaction score (0-100) for this link</summary>
    public int? Satisfaction { get; set; }
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
