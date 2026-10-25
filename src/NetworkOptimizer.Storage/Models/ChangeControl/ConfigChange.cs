using System.ComponentModel.DataAnnotations;

namespace NetworkOptimizer.Storage.Models;

/// <summary>Who made a change, as far as Network Optimizer can tell.</summary>
public enum ConfigChangeActorKind
{
    /// <summary>An admin named in the UniFi AUDIT log.</summary>
    Person = 0,
    /// <summary>The snapshot diff has no matching AUDIT event.</summary>
    NoAdminRecorded = 1,
    /// <summary>No AUDIT event, inside a Network app upgrade window or on a field UniFi rewrites itself.</summary>
    LikelyUniFi = 2,
    /// <summary>Network Optimizer wrote it (matched in the write journal).</summary>
    NetworkOptimizer = 3,
    /// <summary>A Firmware Rollout step or a Network app upgrade.</summary>
    Firmware = 4,
}

/// <summary>What kind of row a change is on the Changes timeline.</summary>
public enum ConfigChangeKind
{
    /// <summary>Config objects changed.</summary>
    Config = 0,
    /// <summary>A device firmware upgrade (Firmware Rollout step or seen in place).</summary>
    DeviceFirmware = 1,
    /// <summary>The Network app version changed.</summary>
    NetworkAppUpgrade = 2,
    /// <summary>Security-relevant UniFi Console settings changed (admins, SSH, remote access, auto-update).</summary>
    ConsoleSettings = 3,
}

/// <summary>How an object changed.</summary>
public enum ConfigChangeAction
{
    Created = 0,
    Updated = 1,
    Deleted = 2,
}

/// <summary>
/// One row on the Changes timeline: one attributed change event covering one or more objects.
/// The change history is never thinned; only snapshots are.
/// </summary>
public class ConfigChange
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC time the change happened (the AUDIT event time when known, else detection time).</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>UTC time Network Optimizer detected it.</summary>
    public DateTime DetectedAt { get; set; }

    /// <summary>Row kind.</summary>
    public ConfigChangeKind Kind { get; set; }

    /// <summary>Snapshot holding the state after the change.</summary>
    public int? SnapshotId { get; set; }

    /// <summary>Snapshot holding the state before the change.</summary>
    public int? PreviousSnapshotId { get; set; }

    /// <summary>Who made it.</summary>
    public ConfigChangeActorKind ActorKind { get; set; }

    /// <summary>Admin name from the AUDIT log, or the NO feature, or null.</summary>
    [MaxLength(200)]
    public string? ActorName { get; set; }

    /// <summary>Source IP from the AUDIT log.</summary>
    [MaxLength(64)]
    public string? ActorIp { get; set; }

    /// <summary>Platform from the AUDIT log (web, ios, android, api).</summary>
    [MaxLength(40)]
    public string? ActorPlatform { get; set; }

    /// <summary>One-line summary shown on the timeline ("WLAN Home: security WPA2 to WPA2/WPA3").</summary>
    [MaxLength(500)]
    public string Summary { get; set; } = string.Empty;

    /// <summary>Objects the change touched.</summary>
    public int ObjectCount { get; set; }

    /// <summary>Service tags (class D) across the change's objects, as a JSON string array.</summary>
    public string? ServiceTagsJson { get; set; }

    /// <summary>The change touches the management path (class B).</summary>
    public bool ManagementPath { get; set; }

    /// <summary>The change touches the WAN path (WAN networks, routes, port forwards, DNS).</summary>
    public bool WanPath { get; set; }

    /// <summary>Security Audit issues the change added.</summary>
    public int RegressionCount { get; set; }

    /// <summary>Security Audit issues the change removed.</summary>
    public int FixCount { get; set; }

    /// <summary>Impact lines (regressions and fixes) as JSON, from the before/after audit.</summary>
    public string? ImpactJson { get; set; }

    /// <summary>The Change Control plan that made this change, when Network Optimizer applied one.</summary>
    public int? PlanId { get; set; }

    /// <summary>The Firmware Rollout plan behind a firmware row.</summary>
    public int? FirmwareRolloutPlanId { get; set; }

    /// <summary>Raw AUDIT events matched to this change, as JSON (keys and parameters, never values).</summary>
    public string? AuditEventsJson { get; set; }

    /// <summary>Network app version before an upgrade row, or the device firmware before.</summary>
    [MaxLength(60)]
    public string? VersionFrom { get; set; }

    /// <summary>Network app version after an upgrade row, or the device firmware after.</summary>
    [MaxLength(60)]
    public string? VersionTo { get; set; }

    /// <summary>Alert evaluation for this change has run (so a restart does not alert twice).</summary>
    public bool Evaluated { get; set; }

    /// <summary>Mean site health (local part, ISP impairment removed) over the 3 hours before. Null until scored.</summary>
    public double? HealthBefore { get; set; }

    /// <summary>
    /// Mean site health (local part) over the 3 hours after. Set once those hours are stored, so the
    /// regression check has run; -1 when there was no health data after the change.
    /// </summary>
    public double? HealthAfter { get; set; }

    /// <summary>The post-change device check for a management-path change has run.</summary>
    public bool ManagementChecked { get; set; }
}

/// <summary>One object inside a <see cref="ConfigChange"/>, with its field-level diff.</summary>
public class ConfigChangeObject
{
    [Key]
    public int Id { get; set; }

    /// <summary>Owning change.</summary>
    public int ChangeId { get; set; }

    /// <summary>Collection key.</summary>
    [MaxLength(64)]
    public string Collection { get; set; } = string.Empty;

    /// <summary>UniFi ID of the object (or the identity key for objects without one).</summary>
    [MaxLength(128)]
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>Display name at the time of the change.</summary>
    [MaxLength(300)]
    public string ObjectName { get; set; } = string.Empty;

    /// <summary>Created, updated, or deleted.</summary>
    public ConfigChangeAction Action { get; set; }

    /// <summary>
    /// Field changes as JSON: path, old, new. Secret fields carry no values, only a "changed" flag,
    /// for every role.
    /// </summary>
    public string FieldsJson { get; set; } = "[]";

    /// <summary>Service tags for this object, as a JSON string array.</summary>
    public string? ServiceTagsJson { get; set; }

    /// <summary>The object is owned by a Network Optimizer feature (never rolled back).</summary>
    [MaxLength(60)]
    public string? OwnedBy { get; set; }
}

/// <summary>
/// One write Network Optimizer sent to the UniFi Console. AUDIT events are matched against this
/// journal (time, object, field) to recognize NO's own changes, since the AUDIT admin name is the
/// API key owner's and varies by install.
/// </summary>
public class ConfigWriteJournalEntry
{
    [Key]
    public long Id { get; set; }

    /// <summary>UTC time the write was sent.</summary>
    public DateTime At { get; set; }

    /// <summary>HTTP method.</summary>
    [MaxLength(10)]
    public string Method { get; set; } = string.Empty;

    /// <summary>Path below the site (rest/networkconf/abc, firewall-policies/xyz, cmd/devmgr).</summary>
    [MaxLength(300)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Collection key, when the path maps to one.</summary>
    [MaxLength(64)]
    public string? Collection { get; set; }

    /// <summary>Object ID from the path or the response, when known.</summary>
    [MaxLength(128)]
    public string? ObjectId { get; set; }

    /// <summary>Top-level field names in the body, as a JSON string array. Never values.</summary>
    public string? FieldsJson { get; set; }

    /// <summary>The NO feature that wrote it ("change-control", "threat-intelligence", ...), when known.</summary>
    [MaxLength(60)]
    public string? Source { get; set; }

    /// <summary>HTTP status of the response.</summary>
    public int Status { get; set; }
}

/// <summary>A period in which change detection could not see the site (agent tunnel or console down).</summary>
public class ChangeDetectionGap
{
    [Key]
    public int Id { get; set; }

    /// <summary>UTC start.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>UTC end, or null while it lasts.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Why ("agent offline", "console unreachable").</summary>
    [MaxLength(200)]
    public string Reason { get; set; } = string.Empty;

    /// <summary>On reconnect the AUDIT log was read back across the gap.</summary>
    public bool CaughtUp { get; set; }
}
