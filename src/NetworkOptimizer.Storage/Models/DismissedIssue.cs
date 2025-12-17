using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Tracks dismissed audit issues so they persist across restarts
/// </summary>
public class DismissedIssue
{
    [Key]
    public int Id { get; set; }

    /// <summary>
    /// Unique key for the issue (Title|DeviceName|Port)
    /// </summary>
    [Required]
    [MaxLength(500)]
    public string IssueKey { get; set; } = "";

    /// <summary>
    /// When the issue was dismissed
    /// </summary>
    public DateTime DismissedAt { get; set; }
}
