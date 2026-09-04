using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// A Wi-Fi Optimizer issue the operator has acknowledged: hidden from the active list, still
/// scored. Its own table rather than <see cref="DismissedIssue"/> so Security Audit's
/// "clear all" cannot take these with it.
/// </summary>
public class WiFiIssueAcknowledgment
{
    [Key]
    public int Id { get; set; }

    /// <summary>The issue's stable key (rule id and scope), unique per site.</summary>
    [Required]
    [MaxLength(500)]
    public string IssueKey { get; set; } = "";

    /// <summary>When it was acknowledged (UTC).</summary>
    public DateTime AcknowledgedAt { get; set; }
}
