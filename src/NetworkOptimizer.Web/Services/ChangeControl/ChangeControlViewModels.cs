using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One row of the Changes table.</summary>
public sealed class ChangeRowView
{
    public int Id { get; init; }
    public string SiteSlug { get; init; } = "";
    public string SiteName { get; init; } = "";
    public DateTime OccurredAt { get; init; }
    public ConfigChangeKind Kind { get; init; }
    public string Summary { get; init; } = "";
    public ConfigChangeActorKind ActorKind { get; init; }
    public string Who { get; init; } = "";
    public int RegressionCount { get; init; }
    public int FixCount { get; init; }
    public List<string> Tags { get; init; } = [];
    public int IncidentsAfter { get; init; }
    public bool ManagementPath { get; init; }
    public int? SnapshotId { get; init; }
    public int? PreviousSnapshotId { get; init; }
    public int? PlanId { get; init; }
    /// <summary>The applied Roll Back plan that undid this change, and when it finished; null when none has.</summary>
    public int? RolledBackByPlanId { get; init; }
    public DateTime? RolledBackAt { get; init; }
    /// <summary>Site Health lost in the 3 hours after (ISP part removed); null until scored or with no data.</summary>
    public double? HealthDrop { get; init; }
}

/// <summary>One object in a change, with its field rows.</summary>
public sealed class ChangeObjectView
{
    public string Collection { get; init; } = "";
    public string CollectionName { get; init; } = "";
    public string ObjectId { get; init; } = "";
    public string Name { get; init; } = "";
    public ConfigChangeAction Action { get; init; }
    public List<StoredField> Fields { get; init; } = [];
    public string? OwnedBy { get; init; }
    public string ChangeText { get; init; } = "";
}

/// <summary>An incident near a change.</summary>
public sealed class IncidentView
{
    public int Id { get; init; }
    public string Title { get; init; } = "";
    public DateTime StartedAt { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string Severity { get; init; } = "";

    /// <summary>WAN/ISP incidents are listed but not linked to LAN config changes.</summary>
    public bool IsWan { get; init; }
}

/// <summary>A before/after metric row (one hour each side).</summary>
public sealed record MetricDelta(string Label, string Before, string After, bool Worse);

/// <summary>A change's detail row.</summary>
public sealed class ChangeDetailView
{
    public ChangeRowView Row { get; init; } = new();
    public List<ChangeObjectView> Objects { get; init; } = [];
    public List<ImpactLine> Impact { get; init; } = [];
    public List<IncidentView> IncidentsAfter { get; init; } = [];
    public List<MetricDelta> BeforeAfter { get; init; } = [];
    public string? Source { get; init; }
    public bool CanRollBack { get; init; }
    /// <summary>Label and value rows for a change with no config objects: a firmware or Network app upgrade.</summary>
    public List<(string Label, string Value)> Facts { get; init; } = [];
}

/// <summary>One snapshot row.</summary>
public sealed class SnapshotRowView
{
    public int Id { get; init; }
    public DateTime TakenAt { get; init; }
    public string Trigger { get; init; } = "";
    public string TriggerDot { get; init; } = "";
    public int ChangedObjects { get; init; }
    public int? IssueCount { get; init; }
    public int RegressionCount { get; init; }
    public bool Pinned { get; init; }
    public bool IsBaseline { get; init; }
    public string? PinReason { get; init; }
}

/// <summary>The Snapshots tab.</summary>
public sealed class SnapshotListView
{
    public List<SnapshotRowView> Rows { get; init; } = [];
    public int Count { get; init; }
    public long StoredBytes { get; init; }
    public int? LiveIssueCount { get; init; }
    public ChangeControlSettings Settings { get; init; } = new();
}

/// <summary>One object in a comparison.</summary>
public sealed class CompareObjectView
{
    public ChangeObjectView Object { get; init; } = new();
    public string? WhenWho { get; init; }
    public string? Note { get; init; }
}

/// <summary>A comparison between two snapshots (or a snapshot and Now).</summary>
public sealed class CompareView
{
    public string FromLabel { get; init; } = "";
    public string FromDetail { get; init; } = "";
    public string ToLabel { get; init; } = "";
    public string ToDetail { get; init; } = "";
    public int? FromSnapshotId { get; init; }
    public int? ToSnapshotId { get; init; }
    public List<ImpactLine> Impact { get; init; } = [];
    public List<CompareObjectView> Objects { get; init; } = [];
    public string? Error { get; init; }
}

/// <summary>A plan, decrypted for Review Plan. Secret values are never in it.</summary>
public sealed class PlanView
{
    public ConfigPlan Plan { get; init; } = new();
    public PlanDocument Doc { get; init; } = new();
    public PlanAcknowledgements Acks { get; init; } = new();
    public PlanResult? Result { get; init; }
    public string SiteName { get; init; } = "";
    public string SiteDetail { get; init; } = "";
    public bool ApprovalsOn { get; init; }
    public bool ApprovalRequired { get; init; }
    public bool CallerIsSiteAdmin { get; init; }
    public List<string> Remaining { get; init; } = [];
    public string ConfirmationText { get; init; } = "";
    public int WatchMinutes { get; init; }
    public int DeviceCount { get; init; }
    public DateTime? QuietestHourSuggestion { get; init; }
}

/// <summary>Summary of a plan for lists.</summary>
public sealed class PlanSummaryView
{
    public int Id { get; init; }
    public string SiteSlug { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string Name { get; init; } = "";
    public ConfigPlanStatus Status { get; init; }
    public string Detail { get; init; } = "";
    public DateTime? WatchUntil { get; init; }
    public DateTime? StartedAt { get; init; }
}

/// <summary>A drift status (spec "Drift").</summary>
public sealed class DriftView
{
    public bool HasBaseline { get; init; }
    public string BaselineLabel { get; init; } = "";
    public int? BaselineSnapshotId { get; init; }
    public int? BaselineTemplateId { get; init; }
    public List<CompareObjectView> Objects { get; init; } = [];
    public List<ImpactLine> Regressions { get; init; } = [];
}

/// <summary>The Edit tab's live check.</summary>
public sealed class YamlValidation
{
    public List<YamlProblem> Problems { get; init; } = [];
    public int EditedObjects { get; init; }
    public int EditedCollections { get; init; }
    public Dictionary<string, int> EditedByCollection { get; init; } = [];
}
