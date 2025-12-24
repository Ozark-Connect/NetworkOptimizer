using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A UniFi device that can be targeted for SSH operations (speed tests, etc.)
/// SSH credentials come from the shared UniFiSshSettings.
/// </summary>
public class DeviceSshConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Friendly name for this device</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Hostname or IP address</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "";

    /// <summary>Device type (Gateway, Switch, AccessPoint, Server, Desktop)</summary>
    [MaxLength(50)]
    public string DeviceType { get; set; } = DeviceTypes.AccessPoint;

    /// <summary>Whether this device is enabled for operations</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to start iperf3 server before running test (for devices without persistent iperf3)</summary>
    public bool StartIperf3Server { get; set; } = false;

    /// <summary>Optional SSH username override (uses global settings if null/empty)</summary>
    [MaxLength(100)]
    public string? SshUsername { get; set; }

    /// <summary>Optional SSH password override (encrypted, uses global settings if null/empty)</summary>
    [MaxLength(500)]
    public string? SshPassword { get; set; }

    /// <summary>Optional SSH private key path override (uses global settings if null/empty)</summary>
    [MaxLength(500)]
    public string? SshPrivateKeyPath { get; set; }

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
