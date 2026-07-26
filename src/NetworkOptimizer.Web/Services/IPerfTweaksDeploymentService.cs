using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Deploys the Performance Tweaks boot scripts and kernel modules to the gateway. Gated at the
/// service layer (design doc 06, gate 9): status reads are open to any authenticated user, deploying
/// or removing a tweak is Admin-only and audited.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IPerfTweaksDeploymentService
{
    /// <summary>Status of every tweak on the current site's gateway, plus gateway/firmware support.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PerfTweaksStatus> CheckAllStatusAsync();

    /// <summary>Deploys a single tweak by id, reporting progress as it goes.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.PerfTweakApplied, TargetType = "perftweak")]
    Task<(bool success, string message, List<string> steps)> DeployTweakAsync(string tweakId, IProgress<string>? progress = null);

    /// <summary>Removes a single tweak by id.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.PerfTweakRemoved, TargetType = "perftweak")]
    Task<(bool success, string message)> RemoveTweakAsync(string tweakId, PerfTweaksStatus? status = null);

    /// <summary>Records that a tweak was deployed by hand outside the app (so status reads stay honest).</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.PerfTweakApplied, TargetType = "perftweak")]
    Task SetManuallyDeployedAsync(string tweakId, bool isManual);

    /// <summary>Installs udm-boot on the gateway so boot scripts survive firmware upgrades.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.PerfTweakApplied, TargetType = "udm_boot")]
    Task<(bool success, string message)> InstallUdmBootAsync();
}
