using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Stores results from an iperf3 speed test to a UniFi device
/// </summary>
public class Iperf3Result
{
    [Key]
    public int Id { get; set; }

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

    /// <summary>Raw iperf3 JSON output for upload test</summary>
    public string? RawUploadJson { get; set; }

    /// <summary>Raw iperf3 JSON output for download test</summary>
    public string? RawDownloadJson { get; set; }
}
