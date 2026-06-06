using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Configuration for an external cable modem that can be polled via HTTP.
/// The Provider column routes to the matching ICableModemProvider.
/// </summary>
public class CmConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this cable modem (e.g., "CM1000 Primary")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>
    /// Provider key (e.g. "netgear", "arris-surfboard") that
    /// selects which ICableModemProvider handles this modem.
    /// </summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = "netgear";

    /// <summary>Hostname or IP address for the cable modem web interface</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "";

    /// <summary>HTTP port (default 80)</summary>
    public int Port { get; set; } = 80;

    /// <summary>Username for HTTP auth (default "admin" for most cable modems)</summary>
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = "admin";

    /// <summary>HTTP admin password (encrypted at rest)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>
    /// Optional override for the status page URL path.
    /// If null, provider uses its built-in default.
    /// </summary>
    [MaxLength(255)]
    public string? StatusPagePath { get; set; }

    /// <summary>Whether this cable modem is enabled for polling</summary>
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
