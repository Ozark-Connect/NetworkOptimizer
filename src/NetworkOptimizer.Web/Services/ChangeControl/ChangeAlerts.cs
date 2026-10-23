namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>Alert event types Change Control publishes. Each has a seeded rule in DefaultAlertRules.</summary>
public static class ChangeAlerts
{
    public const string Source = "change_control";
    public const string SecurityRegression = "change.security_regression";
    public const string ManagementPath = "change.management_path";
    public const string Unattributed = "change.unattributed";
    public const string DriftRegression = "change.drift_regression";
    public const string Any = "change.any";
    public const string Digest = "change.digest";
    public const string NetworkAppUpgrade = "change.network_app_upgrade";
    public const string PlanAwaitingApproval = "change.plan_awaiting_approval";
    public const string PlanApplied = "change.plan_applied";
    public const string PlanReverted = "change.plan_reverted";
    public const string PlanFailed = "change.plan_failed";
    public const string ConsoleSettings = "change.console_settings";

    /// <summary>Link to a change's row on the Changes tab.</summary>
    public static string ChangeUrl(int changeId) => $"/change-control/changes?change={changeId}";

    /// <summary>Link to a plan's Review Plan page.</summary>
    public static string PlanUrl(int planId) => $"/change-control/plans/{planId}";
}
