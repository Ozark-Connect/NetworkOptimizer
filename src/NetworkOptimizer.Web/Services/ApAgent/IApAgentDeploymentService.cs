using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Deploys and supervises the AP Agent on one site's access points.
///
/// Site-scoped: an access point belongs to the site in context, and every deploy action is driven
/// from the site-admin-only Settings page, so the write side requires Admin ON THAT SITE. Reads are
/// Viewer, matching the monitoring surfaces that show agent state.
///
/// The AP Agent is ephemeral - it lives in tmpfs and dies with the AP's power-on session - so
/// redeploy is the normal path rather than a repair, and the supervisor drives it from both the
/// reboot signal and a periodic health poll.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IApAgentDeploymentService
{
    /// <summary>Every access point on this site with its last observed agent state.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApAgentFleetEntry>> GetFleetAsync(CancellationToken ct = default);

    /// <summary>What one SSH round trip reports about an access point.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ApAgentSshStatus> GetStatusAsync(string deviceMac, CancellationToken ct = default);

    /// <summary>Probes one access point's agent over HTTP and classifies the result.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ApAgentAssessment> CheckHealthAsync(string deviceMac, CancellationToken ct = default);

    /// <summary>The agent contract version this server ships.</summary>
    [RequireRole(Roles.Viewer)]
    Task<int> GetExpectedBinaryVersionAsync();

    /// <summary>Whether AP Agent deployment is switched on for this site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<bool> IsSiteEnabledAsync();

    /// <summary>Switches AP Agent deployment on or off for this whole site.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ApAgentSettingsChanged, TargetType = "site")]
    Task SetSiteEnabledAsync(bool enabled);

    /// <summary>Pushes the agent to one access point and starts it. Idempotent.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ApAgentDeployed, TargetType = "ap")]
    Task<ApAgentOperationResult> DeployAsync(string deviceMac, IProgress<string>? progress = null, CancellationToken ct = default);

    /// <summary>Restarts the agent already on an access point, without re-transferring the binary.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ApAgentRestarted, TargetType = "ap")]
    Task<ApAgentOperationResult> RestartAsync(string deviceMac, CancellationToken ct = default);

    /// <summary>Stops the agent and clears its install directory. A reboot would do the same.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ApAgentRemoved, TargetType = "ap")]
    Task<ApAgentOperationResult> RemoveAsync(string deviceMac, CancellationToken ct = default);

    /// <summary>Opts one access point in or out of AP Agent deployment.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.ApAgentSettingsChanged, TargetType = "ap")]
    Task SetEnabledAsync(string deviceMac, bool enabled, CancellationToken ct = default);
}
