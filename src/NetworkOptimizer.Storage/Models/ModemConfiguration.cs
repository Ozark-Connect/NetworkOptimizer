using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Configuration for a cellular modem that can be polled via SSH
/// </summary>
public class ModemConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this modem (e.g., "U5G-Max Primary")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Hostname or IP address for SSH connection</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "";

    /// <summary>SSH port (default 22)</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH username</summary>
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = "";

    /// <summary>SSH password (encrypted at rest)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>Path to SSH private key file (alternative to password)</summary>
    [MaxLength(500)]
    public string? PrivateKeyPath { get; set; }

    /// <summary>Modem type for determining which commands to run</summary>
    [MaxLength(50)]
    public string ModemType { get; set; } = "U5G-Max";

    /// <summary>QMI device path (e.g., /dev/wwan0qmi0)</summary>
    [MaxLength(100)]
    public string QmiDevice { get; set; } = "/dev/wwan0qmi0";

    /// <summary>Whether this modem is enabled for polling</summary>
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
