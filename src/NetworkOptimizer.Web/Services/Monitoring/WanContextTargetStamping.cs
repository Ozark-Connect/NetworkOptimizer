using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Keeps <see cref="MonitoringTarget.WanContextId"/> (probe routing) and
/// <see cref="MonitoringTarget.WanInterface"/> (which WAN the data describes - the key every
/// per-WAN reader scopes on) moving together at runtime.
/// <para>
/// Targets bound to no WAN carry <see cref="MonitoringTarget.UnpinnedWan"/>, not NULL. An absent
/// value got interpreted, and the readers disagreed: most took it for the primary, while the one
/// governing discovery writes took it for "belongs to whatever WAN is asking" and let a metered
/// secondary slow every hand-added target on the site.
/// </para>
/// </summary>
public static class WanContextTargetStamping
{
    /// <summary>
    /// The WanInterface a target should carry after a WAN-context (re)assignment: the context's
    /// WAN, or the site's primary when the target moves back off a context.
    /// </summary>
    /// <param name="target">The target being assigned.</param>
    /// <param name="wanContextId">The context it moves to, or null to go back to unpinned.</param>
    /// <param name="contextWanInterface">That context's WAN, ignored when moving off a context.</param>
    public static void ApplyAssignment(
        MonitoringTarget target, int? wanContextId, string? contextWanInterface)
    {
        target.WanContextId = wanContextId;
        // A context naming no WAN (one exists on every pre-WAN-column install) says no more than
        // no context does, so both land on unpinned.
        target.WanInterface = wanContextId == null
            ? MonitoringTarget.UnpinnedWan
            : Pinned(contextWanInterface);
    }

    /// <summary>The WAN key to store, or the unpinned marker when there is none.</summary>
    private static string Pinned(string? wanInterface) =>
        string.IsNullOrEmpty(wanInterface) ? MonitoringTarget.UnpinnedWan : wanInterface;

    /// <summary>
    /// Re-stamps every target assigned to a context after the context's WAN changed, so their
    /// data is attributed to the WAN the context now measures. Caller saves.
    /// </summary>
    public static async Task<int> RestampContextTargetsAsync(
        NetworkOptimizerDbContext db, int wanContextId, string? wanInterface, CancellationToken ct = default)
    {
        var targets = await db.MonitoringTargets.Where(t => t.WanContextId == wanContextId).ToListAsync(ct);
        foreach (var target in targets)
            target.WanInterface = Pinned(wanInterface);
        return targets.Count;
    }

    /// <summary>
    /// Moves a deleted context's targets back to unpinned - nothing binds their probes any more,
    /// and keeping the dead context's WAN would file them under one nothing probes. Caller saves.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="wanContextId">The context being deleted.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> ReleaseContextTargetsAsync(
        NetworkOptimizerDbContext db, int wanContextId, CancellationToken ct = default)
    {
        var targets = await db.MonitoringTargets.Where(t => t.WanContextId == wanContextId).ToListAsync(ct);
        foreach (var target in targets)
            ApplyAssignment(target, null, null);
        return targets.Count;
    }
}
