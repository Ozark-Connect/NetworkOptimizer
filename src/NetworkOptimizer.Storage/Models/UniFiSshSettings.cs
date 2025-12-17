using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Shared SSH settings for UniFi network devices (APs, switches).
/// All UniFi devices on a network share the same SSH credentials.
/// Note: Gateway/UDM may have different credentials and should use separate config.
/// </summary>
public class UniFiSshSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>SSH port (default 22)</summary>
    public int Port { get; set; } = 22;

    /// <summary>SSH username (usually 'root' for UniFi devices)</summary>
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
    public bool Enabled { get; set; } = false;

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
