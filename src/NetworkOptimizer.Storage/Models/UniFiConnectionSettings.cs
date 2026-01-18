using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// UniFi controller connection settings for a site.
/// Stores the URL, credentials, and connection state for the UniFi controller API.
/// Password is encrypted at rest using CredentialProtectionService.
/// </summary>
public class UniFiConnectionSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Site this connection belongs to (1:1 relationship)</summary>
    public int SiteId { get; set; }

    /// <summary>Navigation property to parent site</summary>
    public Site? Site { get; set; }

    /// <summary>UniFi controller URL (e.g., https://192.168.1.1 or https://unifi.example.com)</summary>
    [MaxLength(500)]
    public string? ControllerUrl { get; set; }

    /// <summary>Username for UniFi controller authentication</summary>
    [MaxLength(100)]
    public string? Username { get; set; }

    /// <summary>Password for UniFi controller authentication (encrypted at rest)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>UniFi site ID within the controller, used in API paths (default: "default")</summary>
    [MaxLength(100)]
    public string UniFiSiteId { get; set; } = "default";

    /// <summary>Whether to persist credentials for auto-reconnect on startup</summary>
    public bool RememberCredentials { get; set; } = true;

    /// <summary>
    /// Whether to ignore SSL certificate errors when connecting to the UniFi controller.
    /// Default is true because UniFi controllers use self-signed certificates.
    /// </summary>
    public bool IgnoreControllerSSLErrors { get; set; } = true;

    /// <summary>Whether connection settings are configured</summary>
    public bool IsConfigured { get; set; } = false;

    /// <summary>Last successful connection timestamp</summary>
    public DateTime? LastConnectedAt { get; set; }

    /// <summary>Last connection error message (if any)</summary>
    [MaxLength(1000)]
    public string? LastError { get; set; }

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Check if credentials are configured</summary>
    public bool HasCredentials => !string.IsNullOrEmpty(ControllerUrl)
        && !string.IsNullOrEmpty(Username)
        && !string.IsNullOrEmpty(Password);
}
