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
    [RequireRole(Roles.Viewer)]
    Task<WanSteerStatus> GetStatusAsync();

    /// <summary>Deploys (or updates) the WAN Steering binary and configuration on the gateway.</summary>
    /// <remarks>Operator, matching Adaptive SQM: deploying steering config adjusts a running system and is
    /// undone by deploying again. Tearing it off the gateway is RemoveAsync, which stays Admin.</remarks>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> DeployAsync(IProgress<string>? progress, CancellationToken ct = default);

    /// <summary>Stops the running WAN Steering daemon.</summary>
    /// <remarks>Operator: stopping is reversible through DeployAsync, which an Operator also holds.</remarks>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task StopAsync();

    /// <summary>Regenerates the daemon configuration and signals it to reload.</summary>
    /// <remarks>Operator: re-reading config into the running daemon is routine operation.</remarks>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> ReloadConfigAsync();

    /// <summary>Removes WAN Steering from the gateway.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.WanSteeringChanged, TargetType = "wan_steering")]
    Task<(bool Success, string? Error)> RemoveAsync();

    /// <summary>Discovers the gateway's WAN interfaces and their routing marks/tables.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<WanSteerWanInfo>> DiscoverWanInterfacesAsync();

    /// <summary>Builds the daemon configuration JSON for the discovered WANs (preview, not deployed).</summary>
    [RequireRole(Roles.Viewer)]
    Task<string> GenerateConfigJsonAsync(List<WanSteerWanInfo> wans);
}
