using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A UniFi device that can be targeted for SSH operations (speed tests, etc.)
/// SSH credentials come from the shared UniFiSshSettings.
/// </summary>
public class DeviceSshConfiguration
{
    [Key]
    public int Id { get; set; }

    /// <summary>Site this device configuration belongs to</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    /// <summary>Friendly name for this device</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>Hostname or IP address</summary>
    [Required]
    [MaxLength(255)]
    public string Host { get; set; } = "";

    /// <summary>Device type (Gateway, Switch, AccessPoint, Server, Desktop, etc.)</summary>
    public DeviceType DeviceType { get; set; } = DeviceType.AccessPoint;

    /// <summary>Whether this device is enabled for operations</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether to start iperf3 server before running test (for devices without persistent iperf3)</summary>
    public bool StartIperf3Server { get; set; } = false;

    /// <summary>Optional path to iperf3 binary on the remote device (uses "iperf3" if null/empty)</summary>
    [MaxLength(500)]
    public string? Iperf3BinaryPath { get; set; }

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

    /// <summary>
    /// Returns true if this device has its own SSH credentials configured (username + password or key).
    /// When true, the device can connect without needing global SSH settings.
    /// </summary>
    [NotMapped]
    public bool HasOwnCredentials =>
        !string.IsNullOrEmpty(SshUsername) &&
        (!string.IsNullOrEmpty(SshPassword) || !string.IsNullOrEmpty(SshPrivateKeyPath));
}
