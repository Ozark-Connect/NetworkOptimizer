using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Runs the mesh backhaul re-scan on an access point over SSH. Re-scanning briefly drops the AP's
/// uplink, so it is a mutating action: Admin-only and audited (design doc 06, gate 9).
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IMeshOptimizationService
{
    /// <summary>Triggers a mesh backhaul re-scan on the given AP.</summary>
    /// <remarks>Operator: mesh uplinks are re-optimised as RF conditions change, and a poor result is undone by
    /// running it again.</remarks>
    [RequireRole(GlobalRoles.Operator)]
    [AuditAction(AuditActions.OptimizerApplied, TargetType = "mesh_ap")]
    Task<MeshOptimizationResult> OptimizeAsync(string? host, string? iface, string? apName, CancellationToken cancellationToken = default);
}
