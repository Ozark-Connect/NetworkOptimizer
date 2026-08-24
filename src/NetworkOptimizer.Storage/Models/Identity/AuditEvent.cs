namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// A single append-only audit record. Stored in the main DB (not per-site DBs) with
/// <see cref="SiteSlug"/> as a filterable column, so cross-site actions (user admin, federation,
/// licensing) and site-scoped actions share one timeline (design doc 05). Secrets never appear:
/// redaction happens at event construction via an allowlist of loggable fields.
/// </summary>
public class AuditEvent
{
    public long Id { get; set; }

    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Actor user id, or null for system/anonymous actors.</summary>
    public string? ActorUserId { get; set; }

    /// <summary>Actor name snapshot (survives user deletion/rename).</summary>
    public string? ActorName { get; set; }

    /// <summary>How the actor authenticated (password | totp | passkey | oidc:&lt;scheme&gt; | saml:&lt;scheme&gt; | system | recovery).</summary>
    public string? ActorAuthMethod { get; set; }

    public string? SourceIp { get; set; }

    /// <summary>Truncated user-agent string.</summary>
    public string? UserAgent { get; set; }

    /// <summary>One of <see cref="AuditCategories"/>.</summary>
    public string Category { get; set; } = "";

    /// <summary>Dotted verb (e.g. <c>auth.login.success</c>, <c>sqm.applied</c>). See <see cref="AuditActions"/>.</summary>
    public string Action { get; set; } = "";

    public string? TargetType { get; set; }
    public string? TargetId { get; set; }

    /// <summary>Target name snapshot.</summary>
    public string? TargetName { get; set; }

    /// <summary>Site slug when the action is site-scoped; null for cross-site actions.</summary>
    public string? SiteSlug { get; set; }

    /// <summary>One of <see cref="AuditOutcomes"/>.</summary>
    public string Outcome { get; set; } = AuditOutcomes.Success;

    /// <summary>Correlates with app logs (request id / circuit id).</summary>
    public string? CorrelationId { get; set; }

    /// <summary>Structured detail JSON; config changes carry a field-level, secret-redacted before/after diff.</summary>
    public string? DetailsJson { get; set; }
}

/// <summary>Audit event categories (design doc 05).</summary>
public static class AuditCategories
{
    public const string Auth = "auth";
    public const string User = "user";
    public const string Rbac = "rbac";
    public const string Federation = "federation";
    public const string Settings = "settings";
    public const string Site = "site";
    public const string Action = "action";
    public const string License = "license";
    public const string Agent = "agent";
    public const string Audit = "audit";
}

/// <summary>Audit outcomes.</summary>
public static class AuditOutcomes
{
    public const string Success = "Success";
    public const string Denied = "Denied";
    public const string Failure = "Failure";
}

/// <summary>
/// Canonical dotted audit action verbs (design doc 05 coverage checklist). Constants keep call
/// sites consistent and greppable.
/// </summary>
public static class AuditActions
{
    // AuthN
    public const string LoginSuccess = "auth.login.success";
    public const string LoginFailed = "auth.login.failed";
    public const string Lockout = "auth.lockout";
    public const string Logout = "auth.logout";
    public const string SessionRevoked = "auth.session.revoked";
    public const string SignedOutEverywhere = "auth.session.signed_out_everywhere";
    public const string MfaEnrolled = "auth.mfa.enrolled";
    public const string MfaRemoved = "auth.mfa.removed";
    public const string PasskeyRegistered = "auth.passkey.registered";
    public const string PasskeyRemoved = "auth.passkey.removed";
    public const string PasskeyRenamed = "auth.passkey.renamed";
    public const string RecoveryCodeUsed = "auth.recovery_code.used";
    public const string RecoveryCodesRegenerated = "auth.recovery_code.regenerated";
    public const string PasswordChanged = "auth.password.changed";
    public const string PasswordReset = "auth.password.reset";
    public const string BreakGlassUsed = "auth.break_glass.used";
    public const string FederatedLoginRejected = "auth.federated.rejected";
    public const string BridgeExchange = "auth.bridge.exchanged";

    // Identity admin
    public const string UserCreated = "user.created";
    public const string UserJitCreated = "user.jit_created";
    public const string UserDisabled = "user.disabled";
    public const string UserEnabled = "user.enabled";
    public const string UserDeleted = "user.deleted";
    public const string ExternalLinked = "user.external.linked";
    public const string ExternalUnlinked = "user.external.unlinked";

    // RBAC
    public const string RoleGranted = "rbac.role.granted";
    public const string RoleRevoked = "rbac.role.revoked";
    public const string MembershipChanged = "rbac.membership.changed";
    public const string LastAdminRefused = "rbac.last_admin.refused";

    // Federation
    public const string ProviderCreated = "federation.provider.created";
    public const string ProviderUpdated = "federation.provider.updated";
    public const string ProviderEnabled = "federation.provider.enabled";
    public const string ProviderDisabled = "federation.provider.disabled";
    public const string IdpResyncConflict = "federation.resync.conflict";

    // Settings / product actions
    public const string SettingsChanged = "settings.changed";
    public const string SqmApplied = "sqm.applied";
    public const string SqmReverted = "sqm.reverted";
    public const string OptimizerApplied = "optimizer.applied";
    public const string PerfTweakApplied = "perftweak.applied";
    public const string AuditScanRun = "audit_scan.run";
    public const string WanSteeringChanged = "wan_steering.changed";
    public const string AlertRuleChanged = "alert_rule.changed";
    public const string MonitoringSetupChanged = "monitoring_setup.changed";

    /// <summary>A cellular modem's radio was power-cycled to force a fresh tower selection.</summary>
    public const string CellularRadioReset = "cellular_radio.reset";
    public const string DbRestored = "db.restored";
    public const string DbExported = "db.exported";
    public const string PerfTweakRemoved = "perftweak.removed";
    public const string SpeedTestRun = "speedtest.run";
    public const string SpeedTestDeleted = "speedtest.deleted";
    public const string SiteChanged = "site.changed";
    public const string ScheduleChanged = "schedule.changed";

    // Firmware Rollout
    public const string FirmwareRolloutSettingsChanged = "firmware_rollout.settings.changed";
    public const string FirmwareRolloutScheduled = "firmware_rollout.scheduled";
    public const string FirmwareRolloutStarted = "firmware_rollout.started";
    public const string FirmwareRolloutPaused = "firmware_rollout.paused";
    public const string FirmwareRolloutResumed = "firmware_rollout.resumed";
    public const string FirmwareRolloutAborted = "firmware_rollout.aborted";
    public const string FirmwareRolloutPostponed = "firmware_rollout.postponed";
    public const string FirmwareRolloutRollback = "firmware_rollout.rollback";

    // AP Agent (the telemetry agent deployed onto an access point, not the on-site Agent)
    public const string ApAgentDeployed = "ap_agent.deployed";
    public const string ApAgentRemoved = "ap_agent.removed";
    public const string ApAgentRestarted = "ap_agent.restarted";
    public const string ApAgentSettingsChanged = "ap_agent.settings.changed";

    // Console support file
    public const string SupportFileGenerated = "support_file.generated";

    // License / agent
    public const string LicenseChanged = "license.changed";
    public const string AgentEnrolled = "agent.enrolled";
    public const string AgentRemoved = "agent.removed";

    // Meta
    public const string MigrationPerformed = "audit.migration";
    public const string Pruned = "audit.pruned";
}
