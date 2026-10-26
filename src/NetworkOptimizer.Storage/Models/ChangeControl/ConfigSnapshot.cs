using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>Why a snapshot was taken.</summary>
public enum ConfigSnapshotTrigger
{
    /// <summary>The first snapshot of a site.</summary>
    Initial = 0,
    /// <summary>Change detection saw a change (AUDIT event or a device provision).</summary>
    Change = 1,
    /// <summary>The daily backstop.</summary>
    Daily = 2,
    /// <summary>A user pressed Take Snapshot.</summary>
    Manual = 3,
    /// <summary>Auto-pinned before a plan was applied.</summary>
    BeforeApply = 4,
    /// <summary>Taken after a plan was applied, to verify it.</summary>
    AfterApply = 5,
    /// <summary>Auto-pinned before a Firmware Rollout started.</summary>
    BeforeFirmwareRollout = 6,
    /// <summary>Auto-pinned when the Network app version changed, before the post-upgrade diff.</summary>
    NetworkAppUpgrade = 7,
}

/// <summary>
/// One point-in-time copy of a site's UniFi Network configuration. The content lives in the
/// per-site blob store, one blob per collection (<see cref="ConfigSnapshotBlob"/>), so a
/// collection that did not change costs nothing.
/// </summary>
public class ConfigSnapshot
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC time the snapshot was read from the UniFi Console.</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>Why it was taken.</summary>
    public ConfigSnapshotTrigger Trigger { get; set; }

    /// <summary>Short context for the trigger, such as the admin who made the change or the plan name.</summary>
    [MaxLength(300)]
    public string? TriggerDetail { get; set; }

    /// <summary>Network app version the snapshot was read from.</summary>
    [MaxLength(40)]
    public string? NetworkAppVersion { get; set; }

    /// <summary>The snapshot this one was diffed against when it was taken, if any.</summary>
    public int? PreviousSnapshotId { get; set; }

    /// <summary>Objects that differ from the previous snapshot (churn fields excluded).</summary>
    public int ChangedObjectCount { get; set; }

    /// <summary>Total objects across all collections.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Uncompressed size of all collections, for the storage figure.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Pinned snapshots are never thinned by retention.</summary>
    public bool Pinned { get; set; }

    /// <summary>Why it was pinned ("Before Firmware Rollout", "Pinned by Alex").</summary>
    [MaxLength(200)]
    public string? PinReason { get; set; }

    /// <summary>This snapshot is the site's Baseline. At most one per site.</summary>
    public bool IsBaseline { get; set; }

    /// <summary>Security Audit issue count on this snapshot's config, when it was run.</summary>
    public int? AuditIssueCount { get; set; }

    /// <summary>Issues added relative to the previous snapshot (the "+2" in "3 issues, +2").</summary>
    public int AuditRegressionCount { get; set; }

    /// <summary>Serialized issue keys and titles, so two snapshots can be compared without re-running the audit.</summary>
    public string? AuditIssuesJson { get; set; }

    /// <summary>Collections that could not be read, with the error. Null when every read succeeded.</summary>
    public string? ReadErrorsJson { get; set; }
}

/// <summary>One collection's content in one snapshot: a reference into the content-addressed blob store.</summary>
public class ConfigSnapshotBlob
{
    /// <summary>Owning snapshot.</summary>
    public int SnapshotId { get; set; }

    /// <summary>Collection key (see the collection catalog), such as "networkconf".</summary>
    [MaxLength(64)]
    public string Collection { get; set; } = string.Empty;

    /// <summary>SHA-256 of the collection's canonical JSON, hex. Names the blob file.</summary>
    [MaxLength(64)]
    public string Hash { get; set; } = string.Empty;

    /// <summary>Objects in the collection.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Uncompressed canonical JSON size.</summary>
    public long SizeBytes { get; set; }
}
