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
    /// <summary>
    /// Traces the upstream path and proposes targets for review. <paramref name="tracer"/>
    /// runs a specific tracer instance (a WAN context's own, from the panel's per-WAN view);
    /// null runs the site's primary tracer, exactly as before.
    /// <para>
    /// Checks the WAN's health first. When the health gate objects and
    /// <paramref name="overrideHealthGate"/> is false, nothing starts and the result carries the
    /// reason, so the caller can warn and ask again with the override.
    /// </para>
    /// </summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "upstream_discovery")]
    Task<UpstreamDiscoveryStart> StartAsync(Monitoring.UpstreamTracerService? tracer = null, bool overrideHealthGate = false,
        CancellationToken ct = default);

    /// <summary>
    /// Commits the reviewed discovery, writing its hops as monitoring targets. Same tracer
    /// selection rule as <see cref="StartAsync"/>.
    /// </summary>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.MonitoringSetupChanged, TargetType = "upstream_discovery")]
    Task CommitAsync(Monitoring.UpstreamTracerService? tracer = null, CancellationToken ct = default);
}

/// <summary>The outcome of a discovery start.</summary>
/// <param name="Started">Whether a run is under way.</param>
/// <param name="BusyReason">What the health gate objected to, or null when it did not.</param>
public sealed record UpstreamDiscoveryStart(bool Started, string? BusyReason);
