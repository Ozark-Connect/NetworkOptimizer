using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// The two operator actions on Upstream path discovery: running a discovery, and committing the
/// hops it proposes as monitoring targets. Both went straight to the per-site tracer from the
/// panel, so a discovery run - and, more to the point, the target set it writes on commit - left
/// nothing in the Audit Log.
///
/// A thin gate in front of <see cref="Monitoring.UpstreamTracerService"/> rather than an interface
/// over it: the panel reads the tracer's live State throughout, and only these two calls mutate
/// anything, so wrapping the whole surface would buy nothing.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IUpstreamDiscoveryService
{
    /// <summary>Traces the upstream path and proposes targets for review.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "upstream_discovery")]
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Commits the reviewed discovery, writing its hops as monitoring targets.</summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "upstream_discovery")]
    Task CommitAsync(CancellationToken ct = default);
}
