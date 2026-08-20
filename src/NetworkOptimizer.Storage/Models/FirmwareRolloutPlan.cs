using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>
/// Lifecycle of one rollout. Reported, Aborted, and Failed are terminal - see
/// <see cref="FirmwareRolloutStatuses.Terminal"/>.
/// </summary>
public enum FirmwareRolloutStatus
{
    /// <summary>Built but not committed to a start time.</summary>
    Draft = 0,
    /// <summary>Has a ScheduledStartAt and is waiting for it.</summary>
    Scheduled = 1,
    /// <summary>The heads-up alert has gone out; still waiting to start.</summary>
    Announced = 2,
    /// <summary>Executing.</summary>
    Running = 3,
    /// <summary>Held by an admin or a wave approval.</summary>
    Paused = 4,
    /// <summary>All steps done; waiting out the soak before the report.</summary>
    SoakWait = 5,
    /// <summary>Soaked and reported. Terminal.</summary>
    Reported = 6,
    /// <summary>Stopped by an admin. Terminal.</summary>
    Aborted = 7,
    /// <summary>Stopped by the orchestrator after an unrecoverable error. Terminal.</summary>
    Failed = 8
}

/// <summary>
/// Status groupings used by queries. Kept as an array rather than a method so EF can
/// translate the membership test into SQL.
/// </summary>
public static class FirmwareRolloutStatuses
{
    /// <summary>Statuses a plan never leaves. A site has at most one plan outside this set.</summary>
    public static readonly FirmwareRolloutStatus[] Terminal =
    [
        FirmwareRolloutStatus.Reported,
        FirmwareRolloutStatus.Aborted,
        FirmwareRolloutStatus.Failed
    ];
}

/// <summary>
/// One planned or executed firmware rollout: the waves, the console channel state to put
/// back afterwards, and the post-soak report.
/// </summary>
public class FirmwareRolloutPlan
{
    [Key]
    public int Id { get; set; }

    /// <summary>Where this plan is in its lifecycle.</summary>
    public FirmwareRolloutStatus Status { get; set; } = FirmwareRolloutStatus.Draft;

    /// <summary>UTC start time for a scheduled or autopilot run; null for start-now plans.</summary>
    public DateTime? ScheduledStartAt { get; set; }

    /// <summary>UTC time the first upgrade command went out.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>UTC time the last step settled.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Serialized plan: waves, per-device ordering, and per-device ETAs.</summary>
    [Required]
    public string PlanJson { get; set; } = "{}";

    /// <summary>
    /// The console channel settings as they were before this rollout changed them. Persisted
    /// so the restore survives an app restart or an abort, not just an orderly finish.
    /// </summary>
    public string? OriginalChannelSettingsJson { get; set; }

    /// <summary>Serialized soak report; null until the soak completes.</summary>
    public string? ReportJson { get; set; }

    /// <summary>Who created the plan ("autopilot" for unattended runs).</summary>
    [Required]
    [MaxLength(200)]
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When the plan was created.</summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
