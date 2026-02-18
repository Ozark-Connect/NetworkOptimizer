namespace NetworkOptimizer.WiFi.Models;

/// <summary>
/// Site-wide Wi-Fi metrics for a time period
/// </summary>
public class SiteWiFiMetrics
{
    /// <summary>Time of this data point</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Metrics broken down by radio band</summary>
    public Dictionary<RadioBand, BandMetrics> ByBand { get; set; } = new();

    /// <summary>Total client count across all bands</summary>
    public int TotalClients { get; set; }

    /// <summary>Total TX bytes across all bands</summary>
    public long TotalTxBytes { get; set; }

    /// <summary>Total RX bytes across all bands</summary>
    public long TotalRxBytes { get; set; }
}

/// <summary>
/// Metrics for a single radio band at a point in time
/// </summary>
public class BandMetrics
{
    /// <summary>Radio band</summary>
    public RadioBand Band { get; set; }

    /// <summary>Average channel utilization (0-100)</summary>
    public double? ChannelUtilization { get; set; }

    /// <summary>Average interference level (0-100)</summary>
    public double? Interference { get; set; }

    /// <summary>TX retry percentage</summary>
    public double? TxRetryPct { get; set; }

    /// <summary>Total TX packets</summary>
    public long? TxPackets { get; set; }

    /// <summary>Total RX packets</summary>
    public long? RxPackets { get; set; }

    /// <summary>Total TX retries</summary>
    public long? TxRetries { get; set; }

    /// <summary>WiFi TX attempts</summary>
    public long? WifiTxAttempts { get; set; }

    /// <summary>WiFi TX dropped</summary>
    public long? WifiTxDropped { get; set; }

    /// <summary>Client count on this band</summary>
    public int? ClientCount { get; set; }
}

/// <summary>
/// Per-client Wi-Fi metrics for a time period
/// </summary>
public class ClientWiFiMetrics
{
    /// <summary>Time of this data point</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>Client MAC</summary>
    public string ClientMac { get; set; } = string.Empty;

    /// <summary>Connected AP MAC at this time</summary>
    public string? ApMac { get; set; }

    /// <summary>Signal strength (dBm)</summary>
    public int? Signal { get; set; }

    /// <summary>TX retry percentage</summary>
    public double? TxRetryPct { get; set; }

    /// <summary>TX packets</summary>
    public long? TxPackets { get; set; }

    /// <summary>RX packets</summary>
    public long? RxPackets { get; set; }

    /// <summary>TX retries</summary>
    public long? TxRetries { get; set; }

    /// <summary>WiFi TX attempts</summary>
    public long? WifiTxAttempts { get; set; }

    /// <summary>WiFi TX dropped</summary>
    public long? WifiTxDropped { get; set; }

    /// <summary>Radio band at this time</summary>
    public RadioBand? Band { get; set; }

    /// <summary>Channel at this time</summary>
    public int? Channel { get; set; }

    /// <summary>Channel width (MHz) at this time</summary>
    public int? ChannelWidth { get; set; }

    /// <summary>Average TX rate (kbps) - from AP to client</summary>
    public long? TxRateKbps { get; set; }

    /// <summary>Average RX rate (kbps) - from client to AP</summary>
    public long? RxRateKbps { get; set; }

    /// <summary>Wi-Fi protocol (e.g. "ax", "be", "ac")</summary>
    public string? Protocol { get; set; }

    /// <summary>Satisfaction score (0-100)</summary>
    public double? Satisfaction { get; set; }
}

/// <summary>
/// An AP channel change event from the UniFi system log
/// </summary>
public class ChannelChangeEvent
{
    /// <summary>When the channel change occurred</summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>AP MAC address</summary>
    public string ApMac { get; set; } = string.Empty;

    /// <summary>Radio band prefix (ng, na, 6e)</summary>
    public string RadioBandPrefix { get; set; } = string.Empty;

    /// <summary>Radio band</summary>
    public RadioBand Band { get; set; }

    /// <summary>New channel number</summary>
    public int NewChannel { get; set; }

    /// <summary>Previous channel number</summary>
    public int PreviousChannel { get; set; }
}
