using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Curating the site's latency targets: the Latency targets card's add, delete, pause/resume,
/// interval and WAN context edits. These ran as direct DbContext writes inside the card, which
/// left them outside the gate (design doc 06, gate 9) and so unaudited - a target could be paused
/// or deleted with nothing recorded. Routing them through a gated interface gives each one an
/// audit envelope and an enforced role, rather than relying on the card not rendering the button.
///
/// Site-scoped: a target belongs to the site in context, and Operator on THAT site is what the
/// card's own SiteOperatorOnly gate means.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IMonitoringTargetService
{
    /// <summary>Adds a manually-created target, resolving its ASN and tracing it for ancestry.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<MonitoringTarget> AddAsync(NewMonitoringTarget spec, CancellationToken ct = default);

    /// <summary>Deletes a target. False when it no longer exists.</summary>
    /// <remarks>Admin, not Operator: removing a target is the card's one SiteAdminOnly action.</remarks>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Pauses or resumes probing of a target. False when it no longer exists.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<bool> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default);

    /// <summary>Changes how often a target is probed. False when it no longer exists.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<bool> SetPollIntervalAsync(int id, int seconds, CancellationToken ct = default);

    /// <summary>Dismisses the LAN flaky-target advisory for a target. False when it no longer exists.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<bool> DismissLanFlakyHintAsync(int id, CancellationToken ct = default);

    /// <summary>Reassigns which WAN a target is probed over. False when it no longer exists.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_target")]
    Task<bool> SetWanContextAsync(int id, int? wanContextId, CancellationToken ct = default);
}

/// <summary>What the Latency targets card collects for a new target, before defaults are applied.</summary>
public sealed record NewMonitoringTarget
{
    public required string Name { get; init; }
    public required string Address { get; init; }
    public MonitoringTargetType TargetType { get; init; } = MonitoringTargetType.Custom;
    public ProbeMode ProbeMode { get; init; } = ProbeMode.Icmp;
    public int Port { get; init; } = 443;
    public int PollIntervalSeconds { get; init; } = 10;
}

/// <summary>Thrown when a new target fails validation, so the card can show the reason inline.</summary>
public sealed class MonitoringTargetValidationException : Exception
{
    public MonitoringTargetValidationException(string message) : base(message) { }
}
