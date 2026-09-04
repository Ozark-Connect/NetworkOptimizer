using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// How far this server has read one access point's AP Agent event ring.
///
/// The agent holds no state across a restart, so its sequence numbering begins at 1 again; the
/// cursor keeps <see cref="AgentStartedAt"/> to tell a restart apart from a gap. Truncation is
/// counted rather than smoothed over, because a lost window makes the previous association
/// unreliable and an interpolated roam would be a fabrication.
/// </summary>
public class ApAgentEventCursor
{
    [Key]
    public int Id { get; set; }

    /// <summary>The access point's MAC, normalized to lower-case colon form. One row per AP.</summary>
    [Required]
    [MaxLength(20)]
    public string DeviceMac { get; set; } = "";

    /// <summary>The highest event sequence this server has consumed.</summary>
    public long LastSeq { get; set; }

    /// <summary>The agent start time the sequence numbers belong to.</summary>
    public DateTime? AgentStartedAt { get; set; }

    /// <summary>When the ring was last read successfully.</summary>
    public DateTime? LastPolledAt { get; set; }

    /// <summary>When the ring last reported that the requested window had been overwritten.</summary>
    public DateTime? LastTruncatedAt { get; set; }

    /// <summary>How many times the window has been lost, so a chronically undersized ring is visible.</summary>
    public int TruncationCount { get; set; }

    /// <summary>Events the ring overwrote before anyone read them, as the agent counts them.</summary>
    public long DroppedEvents { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
