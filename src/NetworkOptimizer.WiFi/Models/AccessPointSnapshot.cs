namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Point-in-time snapshot of an access point's Wi-Fi state
/// </summary>
public class AccessPointSnapshot
{
    /// <summary>AP MAC address (unique identifier)</summary>
    public string Mac { get; set; } = string.Empty;

    /// <summary>User-assigned name</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Model name (e.g., "U7 Pro")</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>IP address</summary>
    public string Ip { get; set; } = string.Empty;

    /// <summary>Overall device satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Total connected clients across all radios</summary>
    public int TotalClients { get; set; }

    /// <summary>Per-radio details</summary>
    public List<RadioSnapshot> Radios { get; set; } = new();

    /// <summary>Per-SSID/radio details (VAP table)</summary>
    public List<VapSnapshot> Vaps { get; set; } = new();

    /// <summary>When this snapshot was taken</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Whether this AP is a mesh child (has wireless uplink to another AP)</summary>
    public bool IsMeshChild { get; set; }

    /// <summary>MAC address of the mesh parent AP (if this is a mesh child)</summary>
    public string? MeshParentMac { get; set; }

    /// <summary>Radio band used for mesh uplink (if mesh child)</summary>
    public RadioBand? MeshUplinkBand { get; set; }

    /// <summary>Channel used for mesh uplink (if mesh child)</summary>
    public int? MeshUplinkChannel { get; set; }
}

/// <summary>
/// Point-in-time snapshot of a single radio on an AP
/// </summary>
public class RadioSnapshot
{
    /// <summary>Radio identifier (wifi0, wifi1, wifi2)</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Band: 2.4GHz, 5GHz, or 6GHz</summary>
    public RadioBand Band { get; set; }

    /// <summary>Current channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width in MHz (20, 40, 80, 160)</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Current TX power in dBm</summary>
    public int? TxPower { get; set; }

    /// <summary>TX power mode (auto, high, medium, low, custom)</summary>
    public string? TxPowerMode { get; set; }

    /// <summary>Antenna gain in dBi</summary>
    public int? AntennaGain { get; set; }

    /// <summary>EIRP (Effective Isotropic Radiated Power) = TxPower + AntennaGain</summary>
    public int? Eirp => TxPower.HasValue ? TxPower.Value + (AntennaGain ?? 0) : null;

    /// <summary>Radio satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Number of connected clients</summary>
    public int? ClientCount { get; set; }

    /// <summary>Channel utilization percentage (0-100)</summary>
    public int? ChannelUtilization { get; set; }

    /// <summary>Interference level (0-100)</summary>
    public int? Interference { get; set; }

    /// <summary>TX retries as percentage</summary>
    public double? TxRetriesPct { get; set; }

    /// <summary>Whether min RSSI steering is enabled</summary>
    public bool MinRssiEnabled { get; set; }

    /// <summary>Min RSSI threshold if enabled (dBm)</summary>
    public int? MinRssi { get; set; }

    /// <summary>Whether DFS channels are available</summary>
    public bool HasDfs { get; set; }
}

/// <summary>
/// Radio frequency band
/// </summary>
public enum RadioBand
{
    Unknown,
    Band2_4GHz,
    Band5GHz,
    Band6GHz
}

/// <summary>
/// Point-in-time snapshot of a Virtual AP (SSID on a radio)
/// </summary>
public class VapSnapshot
{
    /// <summary>SSID name</summary>
    public string Essid { get; set; } = string.Empty;

    /// <summary>BSSID (MAC of this VAP)</summary>
    public string Bssid { get; set; } = string.Empty;

    /// <summary>Radio band</summary>
    public RadioBand Band { get; set; }

    /// <summary>Channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Number of connected clients</summary>
    public int? ClientCount { get; set; }

    /// <summary>Satisfaction score (0-100)</summary>
    public int? Satisfaction { get; set; }

    /// <summary>Average client signal strength (dBm)</summary>
    public int? AvgClientSignal { get; set; }

    /// <summary>Whether this is a guest network</summary>
    public bool IsGuest { get; set; }

    /// <summary>TX bytes since last reset</summary>
    public long? TxBytes { get; set; }

    /// <summary>RX bytes since last reset</summary>
    public long? RxBytes { get; set; }

    /// <summary>TX retries count</summary>
    public long? TxRetries { get; set; }

    /// <summary>WiFi TX attempts</summary>
    public long? WifiTxAttempts { get; set; }

    /// <summary>WiFi TX dropped</summary>
    public long? WifiTxDropped { get; set; }
}

public static class RadioBandExtensions
{
    /// <summary>
    /// Convert UniFi radio code to RadioBand enum
    /// </summary>
    public static RadioBand FromUniFiCode(string? code)
    {
        return code?.ToLowerInvariant() switch
        {
            "ng" => RadioBand.Band2_4GHz,
            "na" => RadioBand.Band5GHz,
            "6e" => RadioBand.Band6GHz,
            _ => RadioBand.Unknown
        };
    }

    /// <summary>
    /// Get display string for band
    /// </summary>
    public static string ToDisplayString(this RadioBand band)
    {
        return band switch
        {
            RadioBand.Band2_4GHz => "2.4 GHz",
            RadioBand.Band5GHz => "5 GHz",
            RadioBand.Band6GHz => "6 GHz",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// Get UniFi code for band
    /// </summary>
    public static string ToUniFiCode(this RadioBand band)
    {
        return band switch
        {
            RadioBand.Band2_4GHz => "ng",
            RadioBand.Band5GHz => "na",
            RadioBand.Band6GHz => "6e",
            _ => ""
        };
    }
}
