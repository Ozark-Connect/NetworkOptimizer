using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// SSH settings for the UniFi Gateway/UDM device at a site.
/// Gateway typically has different SSH credentials than other UniFi devices.
/// Used for iperf3 speed tests and other gateway-specific operations.
/// </summary>
public class GatewaySshSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Site these gateway settings belong to (1:1 relationship)</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    /// <summary>Gateway hostname or IP address</summary>
    [MaxLength(255)]
    public string? Host { get; set; }

    /// <summary>SSH port (default 22)</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH username</summary>
    [Required]
    [MaxLength(100)]
    public string Username { get; set; } = "root";

    /// <summary>SSH password (encrypted at rest)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>Path to SSH private key file (alternative to password)</summary>
    [MaxLength(500)]
    public string? PrivateKeyPath { get; set; }

    /// <summary>Whether SSH access is configured and enabled</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>iperf3 port on the gateway (typically 5201 or 5202)</summary>
    public int Iperf3Port { get; set; } = 5201;

    /// <summary>TC Monitor HTTP port for SQM rate monitoring</summary>
    public int TcMonitorPort { get; set; } = 8088;

    /// <summary>Last successful connection test timestamp</summary>
    public DateTime? LastTestedAt { get; set; }

    /// <summary>Result of last connection test</summary>
    [MaxLength(500)]
    public string? LastTestResult { get; set; }

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Check if credentials are configured</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(PrivateKeyPath);
}
