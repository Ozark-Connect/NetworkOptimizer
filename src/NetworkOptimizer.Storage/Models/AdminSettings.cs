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

    /// <summary>Check if password is configured (non-empty after decryption)</summary>
    public bool HasPassword => !string.IsNullOrEmpty(Password);
}
