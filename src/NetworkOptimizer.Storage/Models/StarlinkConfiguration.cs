using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Configuration for a Starlink user terminal polled over its local gRPC API.
/// The Provider column routes to the matching IStarlinkProvider.
/// </summary>
public class StarlinkConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this terminal (e.g., "Starlink Roof")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Provider key (e.g. "starlink-grpc") that selects which
    /// IStarlinkProvider handles this terminal.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = "starlink-grpc";

    /// <summary>Hostname or IP of the dish's gRPC endpoint (192.168.100.1 on a stock install)</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "192.168.100.1";

    /// <summary>gRPC port (default 9200)</summary>
    public int Port { get; set; } = 9200;

    /// <summary>Whether this terminal is enabled for polling</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Polling interval in seconds (default 60)</summary>
    public int PollingIntervalSeconds { get; set; } = 60;

    /// <summary>Last successful poll timestamp</summary>
    public DateTime? LastPolled { get; set; }

    /// <summary>Last poll error message (null if successful)</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
