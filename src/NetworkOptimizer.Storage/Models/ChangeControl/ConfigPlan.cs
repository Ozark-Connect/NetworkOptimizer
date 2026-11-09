using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>What produced a plan. Every write Change Control makes is a plan.</summary>
public enum ConfigPlanKind
{
    /// <summary>Roll Back to a snapshot.</summary>
    RollbackSnapshot = 0,
    /// <summary>Roll Back one change.</summary>
    RollbackChange = 1,
    /// <summary>Apply a YAML edit.</summary>
    Edit = 2,
    /// <summary>Restore a snapshot onto this site, another site, or a blank console.</summary>
    Restore = 3,
    /// <summary>Replace Device.</summary>
    ReplaceDevice = 4,
    /// <summary>Set Up Zones.</summary>
    Zones = 5,
    /// <summary>Apply a template.</summary>
    Template = 6,
    /// <summary>Return to Baseline.</summary>
    Baseline = 7,
    /// <summary>The automatic revert of an applied plan (commit-confirmed).</summary>
    Revert = 8,
    /// <summary>Legacy firewall rules translated to zone-based policies.</summary>
    LegacyToZones = 9,
    /// <summary>Re-address Networks: move the site's LAN subnets, and every address in them.</summary>
    Readdress = 10,
    /// <summary>Re-address Networks, before the move: shorter DHCP leases so clients move quickly.</summary>
    ReaddressPrepare = 11,
    /// <summary>Fix it for me: a Security Audit finding's fix, built from the operator's own rules.</summary>
    AuditFix = 12,
}

/// <summary>Plan lifecycle. Draft, Pending approval, Approved, Applied are the user-facing track.</summary>
public enum ConfigPlanStatus
{
    Draft = 0,
    PendingApproval = 1,
    Approved = 2,
    /// <summary>Approved (or not needing approval) and booked for a time.</summary>
    Scheduled = 3,
    /// <summary>Writes in progress.</summary>
    Applying = 4,
    /// <summary>Written; the commit-confirmed window is open.</summary>
    Watching = 5,
    /// <summary>Written and confirmed. Terminal.</summary>
    Applied = 6,
    /// <summary>Reverted by commit-confirmed or by hand. Terminal.</summary>
    Reverted = 7,
    /// <summary>Stopped by an error part way. Terminal.</summary>
    Failed = 8,
    /// <summary>Discarded. Terminal.</summary>
    Cancelled = 9,
}

/// <summary>Status groupings used by queries, as arrays so EF can translate them.</summary>
public static class ConfigPlanStatuses
{
    /// <summary>Statuses a plan never leaves.</summary>
    public static readonly ConfigPlanStatus[] Terminal =
    [
        ConfigPlanStatus.Applied, ConfigPlanStatus.Reverted, ConfigPlanStatus.Failed, ConfigPlanStatus.Cancelled,
    ];

    /// <summary>Statuses shown under Plans in Progress.</summary>
    public static readonly ConfigPlanStatus[] InProgress =
    [
        ConfigPlanStatus.PendingApproval, ConfigPlanStatus.Approved, ConfigPlanStatus.Scheduled,
        ConfigPlanStatus.Applying, ConfigPlanStatus.Watching,
    ];
}

/// <summary>
/// One Change Control plan: the desired-vs-live operations, their safety review, and the result.
/// The operations carry write bodies that can hold secrets, so <see cref="OperationsEncrypted"/>
/// is encrypted with the site's key.
/// </summary>
public class ConfigPlan
{
    [Key]
    public int Id { get; set; }

    /// <summary>Display name ("Roll Back HQ to Oct 15, 9:00 AM").</summary>
    [MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>What produced it.</summary>
    public ConfigPlanKind Kind { get; set; }

    /// <summary>Lifecycle state.</summary>
    public ConfigPlanStatus Status { get; set; }

    /// <summary>Source line on Review Plan ("Snapshot, Oct 15, 9:00 AM").</summary>
    [MaxLength(300)]
    public string SourceDescription { get; set; } = string.Empty;

    /// <summary>The snapshot the desired state came from, when it came from one.</summary>
    public int? SourceSnapshotId { get; set; }

    /// <summary>For a Roll Back of one change: the change it rolls back.</summary>
    public int? RollsBackChangeId { get; set; }

    /// <summary>The site a cross-site restore or template came from.</summary>
    [MaxLength(100)]
    public string? SourceSiteSlug { get; set; }

    /// <summary>The serialized plan document (operations, impact, lockout checks), encrypted for the site.</summary>
    public string OperationsEncrypted { get; set; } = string.Empty;

    /// <summary>Highest safety tier among the plan's items (1 Direct to 4 Lockout).</summary>
    public int HighestTier { get; set; }

    /// <summary>Config objects written.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Devices written.</summary>
    public int DeviceCount { get; set; }

    /// <summary>Acknowledged item keys and the typed confirmation, as JSON.</summary>
    public string? AcknowledgementsJson { get; set; }

    /// <summary>Hash of the live state of every object the plan touches, taken at approval.</summary>
    [MaxLength(64)]
    public string? ApprovedLiveHash { get; set; }

    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [MaxLength(200)]
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAt { get; set; }

    [MaxLength(200)]
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }

    /// <summary>UTC time to apply, for a scheduled plan.</summary>
    public DateTime? ScheduledFor { get; set; }

    /// <summary>Apply as soon as it is approved.</summary>
    public bool ApplyOnApproval { get; set; }

    [MaxLength(200)]
    public string? AppliedBy { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>End of the commit-confirmed window.</summary>
    public DateTime? WatchUntil { get; set; }

    /// <summary>Auto-pinned snapshot taken before the apply.</summary>
    public int? PreApplySnapshotId { get; set; }

    /// <summary>Snapshot taken after the apply, compared with the plan.</summary>
    public int? PostApplySnapshotId { get; set; }

    /// <summary>Per-operation results and the verify report, as JSON.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Before/after report (Firmware Rollout's report shape), as JSON.</summary>
    public string? ReportJson { get; set; }

    /// <summary>Why it failed or was reverted.</summary>
    [MaxLength(1000)]
    public string? Message { get; set; }

    /// <summary>For a revert plan, the plan it reverts.</summary>
    public int? RevertOfPlanId { get; set; }

    /// <summary>The site is reached through an On-Site Agent that holds the revert locally.</summary>
    public bool AgentRevertArmed { get; set; }
}
