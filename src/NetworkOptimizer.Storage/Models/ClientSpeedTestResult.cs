using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Source of the client speed test
/// </summary>
public enum ClientSpeedTestSource
{
    /// <summary>Browser-based test via OpenSpeedTest</summary>
    OpenSpeedTest,

    /// <summary>iperf3 client connecting to our server</summary>
    Iperf3Client,

    /// <summary>Manual entry or import</summary>
    Manual
}

/// <summary>
/// Stores results from client-initiated speed tests (browser or iperf3 client).
/// Unlike Iperf3Result which tests TO network devices, this captures tests FROM client devices.
/// </summary>
public class ClientSpeedTestResult
{
    [Key]
    public int Id { get; set; }

    /// <summary>When the test was performed</summary>
    public DateTime TestTime { get; set; } = DateTime.UtcNow;

    /// <summary>Source of the test (OpenSpeedTest, Iperf3Client, Manual)</summary>
    public ClientSpeedTestSource Source { get; set; }

    /// <summary>Client IP address that ran the test</summary>
    [Required]
    [MaxLength(45)]  // IPv6 max length
    public string ClientIp { get; set; } = "";

    /// <summary>Client MAC address (looked up from UniFi client list)</summary>
    [MaxLength(17)]
    public string? ClientMac { get; set; }

    /// <summary>Client hostname or device name (from UniFi or user agent)</summary>
    [MaxLength(255)]
    public string? ClientName { get; set; }

    /// <summary>User agent string (for browser tests)</summary>
    [MaxLength(500)]
    public string? UserAgent { get; set; }

    // Speed results
    /// <summary>Download speed in Mbps</summary>
    public double DownloadMbps { get; set; }

    /// <summary>Upload speed in Mbps</summary>
    public double UploadMbps { get; set; }

    /// <summary>Ping/latency in milliseconds</summary>
    public double? PingMs { get; set; }

    /// <summary>Jitter in milliseconds</summary>
    public double? JitterMs { get; set; }

    // Data transfer stats
    /// <summary>Data downloaded during test in MB</summary>
    public double? DownloadDataMb { get; set; }

    /// <summary>Data uploaded during test in MB</summary>
    public double? UploadDataMb { get; set; }

    // iperf3 specific fields
    /// <summary>Download retransmits (iperf3 only)</summary>
    public int? DownloadRetransmits { get; set; }

    /// <summary>Upload retransmits (iperf3 only)</summary>
    public int? UploadRetransmits { get; set; }

    /// <summary>Test duration in seconds (iperf3 only)</summary>
    public int? DurationSeconds { get; set; }

    /// <summary>Number of parallel streams (iperf3 only)</summary>
    public int? ParallelStreams { get; set; }

    /// <summary>Whether the test completed successfully</summary>
    public bool Success { get; set; } = true;

    /// <summary>Error message if test failed</summary>
    [MaxLength(2000)]
    public string? ErrorMessage { get; set; }

    /// <summary>Raw JSON output for debugging</summary>
    public string? RawJson { get; set; }
}
