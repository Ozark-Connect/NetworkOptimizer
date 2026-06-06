using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Configuration for an external ONT (Optical Network Terminal) that can be polled via HTTP or SSH.
/// The Provider column routes to the matching IOntProvider.
/// </summary>
public class OntConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this ONT (e.g., "AT&T BGW320")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Provider key (e.g. "att-gateway", "generic-http-ont") that
    /// selects which IOntProvider handles this device.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = "att-gateway";

    /// <summary>Hostname or IP address for the ONT web interface</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "";

    /// <summary>Port (default 80 for HTTP, 22 for SSH)</summary>
    public int Port { get; set; } = 80;

    /// <summary>Username for auth (if required by device)</summary>
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = "";

    /// <summary>Password for auth (encrypted at rest)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>Path to SSH private key file (for SSH-based providers)</summary>
    [MaxLength(500)]
    public string? PrivateKeyPath { get; set; }

    /// <summary>Whether this ONT is enabled for polling</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Polling interval in seconds (default 300 = 5 minutes)</summary>
    public int PollingIntervalSeconds { get; set; } = 300;

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
