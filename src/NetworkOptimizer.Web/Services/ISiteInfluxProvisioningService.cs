using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Creating ONE site's buckets from an InfluxDB connection somebody else already established.
///
/// The same work as the matching methods on <see cref="IInfluxDbProvisioningService"/>, gated
/// differently on purpose. That service is instance-wide: creating orgs, minting org-scoped tokens
/// and writing the shared connection are decisions about the whole installation, so they ask for an
/// instance-wide Admin. Adding a site's own prefixed buckets with a shared credential that is
/// already org-scoped is not such a decision - it is that site's setup, and its own admin should be
/// able to finish it. Asking one question for both meant a Site Admin could reach the wizard and
/// never complete it.
///
/// Site-scoped, so <see cref="Roles.Admin"/> here is satisfied by Admin ON THAT SITE. It is
/// deliberately the narrow path: if the shared token turns out not to be able to create buckets,
/// this fails and the wizard falls back to the all-access token prompt, which is an
/// installation-wide decision and stays with the instance-wide service.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ISiteInfluxProvisioningService
{
    /// <summary>Resolves an org id by name, or null when the token cannot see it.</summary>
    [RequireRole(Roles.Viewer)]
    Task<string?> TryResolveOrgIdAsync(string url, string token, string orgName, CancellationToken ct = default);

    /// <summary>Creates this site's fast and long-term buckets if they do not exist.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "influx_bucket")]
    Task<InfluxDbProvisioningService.BucketProvisionResult> EnsureBucketsAsync(
        string url, string token, string orgId, string primaryBucket, string longtermBucket,
        CancellationToken ct = default);
}
