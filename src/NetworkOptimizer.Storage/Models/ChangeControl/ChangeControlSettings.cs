using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>Per-site Change Control settings (singleton row).</summary>
public class ChangeControlSettings
{
    [Key]
    public int Id { get; set; }

    /// <summary>Every snapshot is kept this many days.</summary>
    public int KeepEveryChangeDays { get; set; } = 30;

    /// <summary>Then the last snapshot per day is kept this many days (counted from now).</summary>
    public int KeepDailyDays { get; set; } = 365;

    /// <summary>Then the last snapshot per month is kept this many months; null keeps them forever.</summary>
    public int? KeepMonthlyMonths { get; set; }

    /// <summary>UniFi console backups (.unf) to keep, besides those Firmware Rollout triggers.</summary>
    public int ConsoleBackupsToKeep { get; set; } = 7;

    /// <summary>Commit-confirmed window on a directly connected site.</summary>
    public int CommitWindowMinutes { get; set; } = 5;

    /// <summary>Commit-confirmed window on a site reached through an On-Site Agent.</summary>
    public int AgentCommitWindowMinutes { get; set; } = 10;

    /// <summary>Watch internet reachability when a plan changes the WAN path.</summary>
    public bool WatchInternet { get; set; } = true;

    /// <summary>Scheduled applies default to the site's quietest hour.</summary>
    public bool ScheduleQuietestHour { get; set; } = true;

    /// <summary>Operators submit plans for Site Admin approval.</summary>
    public bool ApprovalsEnabled { get; set; }

    /// <summary>Operators may roll back to a snapshot from the last 24 hours without approval.</summary>
    public bool EmergencyRollbackWithoutApproval { get; set; } = true;

    /// <summary>How often the AUDIT log and device provisioning are checked.</summary>
    public int PollIntervalSeconds { get; set; } = 60;

    /// <summary>Local hour of the daily full snapshot.</summary>
    public int DailySnapshotHour { get; set; } = 3;

    /// <summary>Local minute of the daily full snapshot.</summary>
    public int DailySnapshotMinute { get; set; }

    /// <summary>Show Network Optimizer's own changes in the history.</summary>
    public bool ShowOwnChanges { get; set; }

    /// <summary>A template is the Baseline (instead of a snapshot marked Baseline).</summary>
    public int? BaselineTemplateId { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>Per-site change-detection cursor (singleton row).</summary>
public class ChangeDetectionState
{
    [Key]
    public int Id { get; set; }

    /// <summary>Timestamp (ms since epoch) of the newest AUDIT event processed.</summary>
    public long LastAuditEventTimestamp { get; set; }

    /// <summary>IDs of the AUDIT events at that timestamp, so a re-read does not count them twice.</summary>
    public string? LastAuditEventIdsJson { get; set; }

    /// <summary>Device MAC to cfgversion, as JSON.</summary>
    public string? CfgVersionsJson { get; set; }

    /// <summary>Device MAC to firmware version, as JSON (firmware rows on the timeline).</summary>
    public string? DeviceVersionsJson { get; set; }

    /// <summary>Network app version last seen.</summary>
    [MaxLength(40)]
    public string? NetworkAppVersion { get; set; }

    /// <summary>UTC time the Network app version last changed, for the "likely UniFi" window.</summary>
    public DateTime? NetworkAppVersionChangedAt { get; set; }

    public DateTime? LastPollAt { get; set; }
    public bool LastPollOk { get; set; }
    public DateTime? LastSnapshotAt { get; set; }
    public DateTime? LastDailySnapshotAt { get; set; }
    public DateTime? LastConsoleBackupAt { get; set; }
    public DateTime? LastRetentionAt { get; set; }
}

/// <summary>A Needs Attention item someone dismissed (Change Control Overview).</summary>
public class AttentionDismissal
{
    [Key]
    public int Id { get; set; }

    /// <summary>The item's stable key: "regression:{audit issue key}" or "plan:{plan id}".</summary>
    [MaxLength(300)]
    public string ItemKey { get; set; } = string.Empty;

    /// <summary>The item's title when dismissed, for the dismissed list.</summary>
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(200)]
    public string DismissedBy { get; set; } = string.Empty;

    /// <summary>UTC time it was dismissed.</summary>
    public DateTime DismissedAt { get; set; }
}

/// <summary>A UniFi console backup (.unf) kept by Network Optimizer, encrypted on disk.</summary>
public class ConsoleBackupRecord
{
    [Key]
    public int Id { get; set; }

    /// <summary>File name on the console.</summary>
    [MaxLength(200)]
    public string FileName { get; set; } = string.Empty;

    /// <summary>UTC time the console made it.</summary>
    public DateTime TakenAt { get; set; }

    /// <summary>UTC time Network Optimizer copied it.</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>Kept because Firmware Rollout triggered it; excluded from the keep count.</summary>
    public bool FirmwareRollout { get; set; }

    public long SizeBytes { get; set; }

    [MaxLength(64)]
    public string Sha256 { get; set; } = string.Empty;

    [MaxLength(40)]
    public string? NetworkAppVersion { get; set; }
}

/// <summary>
/// A config template: a set of collections that can be applied to any site and used as a Baseline.
/// Instance-wide, stored in the main database. Content is encrypted.
/// </summary>
public class ConfigTemplate
{
    [Key]
    public int Id { get; set; }

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? Description { get; set; }

    /// <summary>Site the template was made from.</summary>
    [MaxLength(100)]
    public string? SourceSiteSlug { get; set; }

    /// <summary>The template's desired state (collection to objects), encrypted.</summary>
    public string ContentEncrypted { get; set; } = string.Empty;

    /// <summary>Collections the template covers, as a JSON string array.</summary>
    public string CollectionsJson { get; set; } = "[]";

    [MaxLength(200)]
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
