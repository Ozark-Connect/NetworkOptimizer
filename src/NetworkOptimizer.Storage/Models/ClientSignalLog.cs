using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores periodic signal quality and connection snapshots for a Wi-Fi client.
/// Captured every 5 seconds while the Client Dashboard page is open.
/// </summary>
public class ClientSignalLog
{
    [Key]
    public int Id { get; set; }

    /// <summary>When this snapshot was taken (UTC)</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Client MAC address (lowercase, colon-separated)</summary>
    [Required]
    [MaxLength(17)]
    public string ClientMac { get; set; } = "";

    /// <summary>Client IP address at poll time</summary>
    [MaxLength(45)]
    public string? ClientIp { get; set; }

    /// <summary>Client display name (from UniFi)</summary>
    [MaxLength(200)]
    public string? DeviceName { get; set; }

    // --- Wi-Fi signal data ---

    /// <summary>Wi-Fi signal strength in dBm</summary>
    public int? SignalDbm { get; set; }

    /// <summary>Wi-Fi noise floor in dBm</summary>
    public int? NoiseDbm { get; set; }

    /// <summary>Wi-Fi channel number</summary>
    public int? Channel { get; set; }

    /// <summary>Radio band - "ng" (2.4GHz), "na" (5GHz), "6e" (6GHz)</summary>
    [MaxLength(10)]
    public string? Band { get; set; }

    /// <summary>Radio protocol - "ax", "ac", "be", etc.</summary>
    [MaxLength(10)]
    public string? Protocol { get; set; }

    /// <summary>TX link rate in Kbps (AP to client)</summary>
    public long? TxRateKbps { get; set; }

    /// <summary>RX link rate in Kbps (client to AP)</summary>
    public long? RxRateKbps { get; set; }

    /// <summary>Wi-Fi 7 MLO (Multi-Link Operation) active</summary>
    public bool IsMlo { get; set; }

    /// <summary>MLO link details as JSON array</summary>
    public string? MloLinksJson { get; set; }

    // --- AP data ---

    /// <summary>Connected AP MAC address</summary>
    [MaxLength(17)]
    public string? ApMac { get; set; }

    /// <summary>Connected AP name</summary>
    [MaxLength(200)]
    public string? ApName { get; set; }

    /// <summary>Connected AP model</summary>
    [MaxLength(100)]
    public string? ApModel { get; set; }

    /// <summary>AP's channel for this client's radio</summary>
    public int? ApChannel { get; set; }

    /// <summary>AP's TX power in dBm for this client's radio</summary>
    public int? ApTxPower { get; set; }

    /// <summary>Number of clients connected to the AP</summary>
    public int? ApClientCount { get; set; }

    /// <summary>AP radio band serving this client</summary>
    [MaxLength(10)]
    public string? ApRadioBand { get; set; }

    // --- Location ---

    /// <summary>GPS latitude (from browser geolocation)</summary>
    public double? Latitude { get; set; }

    /// <summary>GPS longitude (from browser geolocation)</summary>
    public double? Longitude { get; set; }

    /// <summary>GPS accuracy in meters</summary>
    public int? LocationAccuracyMeters { get; set; }

    // --- L2 Trace ---

    /// <summary>SHA256 hash of normalized trace path (for dedup)</summary>
    [MaxLength(64)]
    public string? TraceHash { get; set; }

    /// <summary>Full trace as JSON (only stored when TraceHash changes)</summary>
    public string? TraceJson { get; set; }

    /// <summary>Number of hops in the trace</summary>
    public int? HopCount { get; set; }

    /// <summary>Bottleneck link speed in Mbps</summary>
    public double? BottleneckLinkSpeedMbps { get; set; }
}
