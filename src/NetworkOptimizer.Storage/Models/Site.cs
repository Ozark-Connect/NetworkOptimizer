using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Registry entry for a managed site. Lives only in the main (default site) database.
/// Each site's data is stored in its own SQLite database file; the immutable
/// <see cref="Slug"/> is the durable external identifier used in file paths,
/// InfluxDB bucket prefixes, agent configuration, and API routes. The integer
/// primary key never leaves the registry database.
/// </summary>
public class Site
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// URL-safe kebab-case identifier auto-generated from the site name at creation.
    /// Immutable after creation; renaming a site changes only <see cref="Name"/>.
    /// </summary>
    [Required]
    [MaxLength(64)]
    public string Slug { get; set; } = "";

    /// <summary>User-entered display name (e.g., "Acme Corp", "Lake House")</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = "";

    /// <summary>
    /// The default site is the one this Network Optimizer instance managed before
    /// multi-site was enabled. Its data stays in the main database file.
    /// </summary>
    public bool IsDefault { get; set; }

    /// <summary>Whether this site is enabled for scheduled operations</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Sort order for UI display (lower = first)</summary>
    public int SortOrder { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
