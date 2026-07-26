using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Deploys and controls the WAN Steering daemon on the gateway. Gated at the service layer (design
/// doc 06, gate 9): reads are open to any authenticated user, every change to what runs on the
/// gateway is Admin-only and audited.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IWanSteerDeploymentService
{
    /// <summary>Current deployment/run status of the WAN Steering daemon on the gateway.</summary>
    [RequireGlobalRole(GlobalRoles.Viewer)]
    Task<WanSteerStatus> GetStatusAsync();

    /// <summary>Deploys (or updates) the WAN Steering binary and configuration on the gateway.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> DeployAsync(IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Stops the running WAN Steering daemon.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task StopAsync();

    /// <summary>Regenerates the daemon configuration and signals it to reload.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> ReloadConfigAsync();

    /// <summary>Removes WAN Steering from the gateway.</summary>
    [RequireGlobalRole(GlobalRoles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> RemoveAsync();

    /// <summary>Discovers the gateway's WAN interfaces and their routing marks/tables.</summary>
    [RequireGlobalRole(GlobalRoles.Viewer)]
    Task<List<WanSteerWanInfo>> DiscoverWanInterfacesAsync();

    /// <summary>Builds the daemon configuration JSON for the discovered WANs (preview, not deployed).</summary>
    [RequireGlobalRole(GlobalRoles.Viewer)]
    Task<string> GenerateConfigJsonAsync(List<WanSteerWanInfo> wans);
}
