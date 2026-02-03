namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// A client connection event (connect, disconnect, or roam)
/// </summary>
public class ClientConnectionEvent
{
    /// <summary>Event ID</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Event type key (e.g., CLIENT_CONNECTED_WIRELESS_2, CLIENT_DISCONNECTED_WIRELESS_2, CLIENT_ROAMED_2)</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Event type</summary>
    public ClientConnectionEventType Type { get; set; }

    /// <summary>Event timestamp</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Client MAC address</summary>
    public string ClientMac { get; set; } = string.Empty;

    /// <summary>Client name</summary>
    public string? ClientName { get; set; }

    /// <summary>SSID/WLAN name</summary>
    public string? WlanName { get; set; }

    /// <summary>IP address</summary>
    public string? IpAddress { get; set; }

    // Connection info
    /// <summary>AP MAC address (for connect events)</summary>
    public string? ApMac { get; set; }

    /// <summary>AP name (for connect events)</summary>
    public string? ApName { get; set; }

    /// <summary>Signal strength (dBm)</summary>
    public int? Signal { get; set; }

    /// <summary>Radio band (ng=2.4GHz, na=5GHz, 6e=6GHz)</summary>
    public string? RadioBand { get; set; }

    /// <summary>Channel</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width (MHz)</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Wi-Fi stats summary string</summary>
    public string? WifiStats { get; set; }

    // Roaming-specific fields
    /// <summary>Source AP MAC (for roam events)</summary>
    public string? FromApMac { get; set; }

    /// <summary>Source AP name (for roam events)</summary>
    public string? FromApName { get; set; }

    /// <summary>Destination AP MAC (for roam events)</summary>
    public string? ToApMac { get; set; }

    /// <summary>Destination AP name (for roam events)</summary>
    public string? ToApName { get; set; }

    /// <summary>Signal before roaming (dBm)</summary>
    public int? PreviousSignal { get; set; }

    /// <summary>Radio band before roaming</summary>
    public string? PreviousRadioBand { get; set; }

    /// <summary>Channel before roaming</summary>
    public int? PreviousChannel { get; set; }

    // Disconnect-specific fields
    /// <summary>Time connected (for disconnect events)</summary>
    public string? Duration { get; set; }

    /// <summary>Data uploaded (for disconnect events)</summary>
    public string? DataUp { get; set; }

    /// <summary>Data downloaded (for disconnect events)</summary>
    public string? DataDown { get; set; }

    /// <summary>
    /// Get display-friendly radio band name
    /// </summary>
    public string? GetRadioBandDisplay() => RadioBand switch
    {
        "ng" => "2.4 GHz",
        "na" => "5 GHz",
        "6e" => "6 GHz",
        _ => RadioBand
    };

    /// <summary>
    /// Get display-friendly previous radio band name
    /// </summary>
    public string? GetPreviousRadioBandDisplay() => PreviousRadioBand switch
    {
        "ng" => "2.4 GHz",
        "na" => "5 GHz",
        "6e" => "6 GHz",
        _ => PreviousRadioBand
    };
}

/// <summary>
/// Type of client connection event
/// </summary>
public enum ClientConnectionEventType
{
    Unknown,
    Connected,
    Disconnected,
    Roamed
}
