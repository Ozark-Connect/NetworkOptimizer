using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Admin authentication settings for the application.
/// Allows overriding the environment-set admin password with a database-stored password.
/// This is a singleton table (only one row).
/// </summary>
public class AdminSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Admin password hash (PBKDF2-SHA256, format: iterations.salt.hash)</summary>
    [MaxLength(500)]
    public string? Password { get; set; }

    /// <summary>Whether admin authentication is enabled via database config</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>When this configuration was created</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>When this configuration was last updated</summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the user dismissed the "no DDM? check ONT monitoring" advisory on the
    /// Optical (SFP / ONT) card. Global (this is the app-wide settings row, not a
    /// per-site value) so the hint stays dismissed across sites and browsers. Null =
    /// never dismissed, so the advisory still shows.
    /// </summary>
    public DateTime? SfpOntHintDismissedAt { get; set; }

    /// <summary>
    /// App version this install was first seen running, stamped the moment a genuinely
    /// new install is detected (settings row created during that startup). Null for
    /// installs predating the column - it cannot be reconstructed later. Install-level
    /// fact, deliberately not per-subject tour state: it decides Highlights-tour
    /// eligibility for installs that were new before any Highlights content shipped.
    /// </summary>
    [MaxLength(32)]
    public string? FirstSeenVersion { get; set; }

    /// <summary>
    /// App version recorded at the last startup, written after the tour due-check has
    /// read it. Stored version below the running version means an upgrade happened.
    /// </summary>
    [MaxLength(32)]
    public string? LastSeenAppVersion { get; set; }

    /// <summary>Check if password is configured (non-empty after decryption)</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}
