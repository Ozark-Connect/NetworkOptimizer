using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Deploys the per-WAN monitoring interface aliases (boot script, routing marks, tables) to the
/// gateway. Gated at the service layer (design doc 06, gate 9): preflight and status reads are open
/// to any authenticated user, anything that writes to the gateway is Admin-only and audited as a
/// monitoring setup change.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IMonitoringInterfaceDeploymentService
{
    /// <summary>Checks whether the interface can be deployed (address conflicts, mark range, udm-boot).</summary>
    [RequireRole(Roles.Viewer)]
    Task<MonitoringInterfaceDeploymentService.PreflightResult> PreflightAsync(MonitoringInterface mi, CancellationToken ct = default);

    /// <summary>Deploys the alias interface and its boot script to the gateway.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_interface")]
    Task<MonitoringInterfaceDeploymentService.DeployResult> DeployAsync(MonitoringInterface mi, CancellationToken ct = default);

    /// <summary>Removes the alias interface and its boot script from the gateway.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_interface")]
    Task<(bool success, List<string> steps)> RemoveAsync(MonitoringInterface mi);

    /// <summary>Disables the alias interface on the gateway without removing its configuration.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_interface")]
    Task<(bool success, List<string> steps)> DisableAsync(MonitoringInterface mi);

    /// <summary>Re-enables a previously disabled alias interface.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "monitoring_interface")]
    Task<MonitoringInterfaceDeploymentService.DeployResult> EnableAsync(MonitoringInterface mi, CancellationToken ct = default);

    /// <summary>Live status of the alias interface on the gateway.</summary>
    [RequireRole(Roles.Viewer)]
    Task<MonitoringInterfaceDeploymentService.InterfaceStatus> CheckStatusAsync(MonitoringInterface mi);
}
