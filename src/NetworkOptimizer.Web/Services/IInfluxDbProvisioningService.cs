using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Provisions the InfluxDB org, buckets, and scoped token the monitoring collector writes to. Gated
/// at the service layer (design doc 06, gate 9): probing and listing are open to any authenticated
/// user, anything that creates an org/bucket/token on the InfluxDB server is Admin-only and audited as
/// a monitoring setup change.
/// </summary>
[MutatingService]
public interface IInfluxDbProvisioningService
{
    /// <summary>Probes the usual local addresses for a reachable InfluxDB.</summary>
    [RequireRole(Roles.Viewer)]
    Task<InfluxDbProvisioningService.UrlProbeResult> AutoDetectUrlAsync(CancellationToken ct = default);

    /// <summary>Probes one URL for a reachable InfluxDB.</summary>
    [RequireRole(Roles.Viewer)]
    Task<InfluxDbProvisioningService.UrlProbeResult> ProbeAsync(string url, CancellationToken ct = default);

    /// <summary>Checks that an admin token is valid and has the permissions provisioning needs.</summary>
    [RequireRole(Roles.Viewer)]
    Task<InfluxDbProvisioningService.TokenValidationResult> ValidateAdminTokenAsync(string url, string adminToken, CancellationToken ct = default);

    /// <summary>Lists the orgs visible to the admin token.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<InfluxDbProvisioningService.InfluxOrg>> ListOrgsAsync(string url, string adminToken, CancellationToken ct = default);

    /// <summary>Resolves an org id by name, or null when the token cannot see it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string?> TryResolveOrgIdAsync(string url, string token, string orgName, CancellationToken ct = default);

    /// <summary>Creates an org on the InfluxDB server.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "influx_org")]
    Task<InfluxDbProvisioningService.InfluxOrg> CreateOrgAsync(string url, string adminToken, string orgName, CancellationToken ct = default);

    /// <summary>Creates the fast and long-term monitoring buckets if they do not exist.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "influx_bucket")]
    Task<InfluxDbProvisioningService.BucketProvisionResult> EnsureBucketsAsync(
        string url, string adminToken, string orgId, string primaryBucket, string longtermBucket, CancellationToken ct = default);

    /// <summary>Creates a token scoped to read/write on the two monitoring buckets.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "influx_token")]
    Task<InfluxDbProvisioningService.ScopedTokenResult> CreateScopedTokenAsync(
        string url, string adminToken, string orgId, string primaryBucketId, string longtermBucketId,
        string description = "Network Optimizer (read+write on monitoring buckets)", CancellationToken ct = default);

    /// <summary>Creates an org-scoped token (read/write plus bucket creation) for multi-site provisioning.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "influx_token")]
    Task<InfluxDbProvisioningService.ScopedTokenResult> CreateOrgScopedTokenAsync(
        string url, string adminToken, string orgId,
        string description = "Network Optimizer (org-scoped: read+write + create buckets)", CancellationToken ct = default);
}
