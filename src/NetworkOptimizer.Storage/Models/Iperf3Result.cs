using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Direction of the speed test - who initiated it
/// </summary>
public enum SpeedTestDirection
{
    /// <summary>Server-initiated: Network Optimizer SSHs to device and runs iperf3 client</summary>
    ServerToDevice = 0,

    /// <summary>Client-initiated: External device runs iperf3 client to our server</summary>
    ClientToServer = 1,

    /// <summary>Browser-based: OpenSpeedTest or similar browser speed test</summary>
    BrowserToServer = 2,

    /// <summary>WAN speed test via Cloudflare: measures internet throughput from server</summary>
    CloudflareWan = 3,

    /// <summary>WAN speed test via Cloudflare: runs directly on the gateway via SSH</summary>
    CloudflareWanGateway = 4
}

/// <summary>
/// Stores results from an iperf3 speed test to a UniFi device
/// </summary>
public class Iperf3Result
{
    [Key]
    public int Id { get; set; }

    /// <summary>Test direction - who initiated the test</summary>
    public SpeedTestDirection Direction { get; set; } = SpeedTestDirection.ServerToDevice;

    /// <summary>Device hostname or IP that was tested</summary>
    [Required]
    [MaxLength(255)]
    public string DeviceHost { get; set; } = "";

    /// <summary>Friendly device name</summary>
    [MaxLength(100)]
    public string? DeviceName { get; set; }

    /// <summary>Device type (Gateway, Switch, AccessPoint)</summary>
    [MaxLength(50)]
    public string? DeviceType { get; set; }

    /// <summary>When the test was performed</summary>
    public DateTime TestTime { get; set; } = DateTime.UtcNow;

    /// <summary>Test duration in seconds</summary>
    public int DurationSeconds { get; set; } = 10;

    /// <summary>Number of parallel streams used</summary>
    public int ParallelStreams { get; set; } = 3;

    // Upload results (client to device)
    /// <summary>Upload speed in bits per second</summary>
    public double UploadBitsPerSecond { get; set; }

    /// <summary>Upload bytes transferred</summary>
    public long UploadBytes { get; set; }

    /// <summary>Upload retransmits (TCP only)</summary>
    public int UploadRetransmits { get; set; }

    // Download results (device to client, with -R flag)
    /// <summary>Download speed in bits per second</summary>
    public double DownloadBitsPerSecond { get; set; }

    /// <summary>Download bytes transferred</summary>
    public long DownloadBytes { get; set; }

    /// <summary>Download retransmits (TCP only)</summary>
    public int DownloadRetransmits { get; set; }

    // Calculated values for easy display
    /// <summary>Upload speed in Mbps</summary>
    public double UploadMbps => UploadBitsPerSecond / 1_000_000.0;

    /// <summary>Download speed in Mbps</summary>
    public double DownloadMbps => DownloadBitsPerSecond / 1_000_000.0;

    /// <summary>Whether the test completed successfully</summary>
    public bool Success { get; set; }

    /// <summary>Error message if test failed</summary>
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    // Browser speed test fields (OpenSpeedTest)
    /// <summary>Ping/latency in milliseconds (unloaded)</summary>
    public double? PingMs { get; set; }

    /// <summary>Jitter in milliseconds (unloaded)</summary>
    public double? JitterMs { get; set; }

    // Loaded latency fields (measured during throughput tests)
    /// <summary>Loaded latency during download in milliseconds</summary>
    public double? DownloadLatencyMs { get; set; }

    /// <summary>Loaded jitter during download in milliseconds</summary>
    public double? DownloadJitterMs { get; set; }

    /// <summary>Loaded latency during upload in milliseconds</summary>
    public double? UploadLatencyMs { get; set; }

    /// <summary>Loaded jitter during upload in milliseconds</summary>
    public double? UploadJitterMs { get; set; }

    /// <summary>User agent string (browser tests only)</summary>
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    /// <summary>Client MAC address (if resolved from UniFi)</summary>
    [MaxLength(17)]
    public string? ClientMac { get; set; }

    // Geolocation (browser tests with permission)
    /// <summary>Client latitude (browser tests with geolocation permission)</summary>
    public double? Latitude { get; set; }

    /// <summary>Client longitude (browser tests with geolocation permission)</summary>
    public double? Longitude { get; set; }

    /// <summary>Location accuracy in meters (browser tests)</summary>
    public int? LocationAccuracyMeters { get; set; }

    // Wi-Fi signal (from UniFi at test time)
    /// <summary>Wi-Fi signal strength in dBm (wireless clients only)</summary>
    public int? WifiSignalDbm { get; set; }

    /// <summary>Wi-Fi noise floor in dBm (wireless clients only)</summary>
    public int? WifiNoiseDbm { get; set; }

    /// <summary>Wi-Fi channel (wireless clients only)</summary>
    public int? WifiChannel { get; set; }

    /// <summary>Wi-Fi radio protocol - ax, ac, n, etc. (wireless clients only)</summary>
    [MaxLength(10)]
    public string? WifiRadioProto { get; set; }

    /// <summary>Wi-Fi radio band - ng (2.4GHz), na (5GHz), 6e (6GHz) (wireless clients only)</summary>
    [MaxLength(10)]
    public string? WifiRadio { get; set; }

    /// <summary>Wi-Fi TX rate in Kbps (wireless clients only)</summary>
    public long? WifiTxRateKbps { get; set; }

    /// <summary>Wi-Fi RX rate in Kbps (wireless clients only)</summary>
    public long? WifiRxRateKbps { get; set; }

    /// <summary>Wi-Fi 7 MLO (Multi-Link Operation) enabled</summary>
    public bool WifiIsMlo { get; set; }

    /// <summary>MLO link details as JSON array (wireless clients with MLO only)</summary>
    public string? WifiMloLinksJson { get; set; }

    /// <summary>WAN network group identifier - e.g. "WAN", "WAN2" (CloudflareWan tests only)</summary>
    [MaxLength(50)]
    public string? WanNetworkGroup { get; set; }

    /// <summary>WAN friendly name from UniFi - e.g. "Starlink", "AT&T Fiber" (CloudflareWan tests only)</summary>
    [MaxLength(100)]
    public string? WanName { get; set; }

    /// <summary>User notes about this test result</summary>
    [MaxLength(2000)]
    public string? Notes { get; set; }

    /// <summary>Raw iperf3 JSON output for upload test</summary>
    public string? RawUploadJson { get; set; }

    /// <summary>Raw iperf3 JSON output for download test</summary>
    public string? RawDownloadJson { get; set; }

    /// <summary>
    /// Local IP address used for the test (parsed from iperf3 output).
    /// This is the actual source IP the kernel chose for the connection.
    /// </summary>
    [MaxLength(45)]  // IPv6 max length
    public string? LocalIp { get; set; }

    /// <summary>
    /// Serialized path analysis JSON - stored as snapshot at test time.
    /// </summary>
    public string? PathAnalysisJson { get; set; }

    /// <summary>
    /// Network path analysis with bottleneck detection and performance grading.
    /// Deserialized from PathAnalysisJson on access, serialized on set.
    /// </summary>
    [NotMapped]
    public PathAnalysisResult? PathAnalysis
    {
        get
        {
            if (_pathAnalysis != null) return _pathAnalysis;
            if (string.IsNullOrEmpty(PathAnalysisJson)) return null;
            try
            {
                _pathAnalysis = JsonSerializer.Deserialize<PathAnalysisResult>(PathAnalysisJson, JsonOptions);
            }
            catch
            {
                _pathAnalysis = null;
            }
            return _pathAnalysis;
        }
        set
        {
            _pathAnalysis = value;
            PathAnalysisJson = value != null ? JsonSerializer.Serialize(value, JsonOptions) : null;
        }
    }
    private PathAnalysisResult? _pathAnalysis;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
}
