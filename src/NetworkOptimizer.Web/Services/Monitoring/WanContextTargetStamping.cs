using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Keeps <see cref="MonitoringTarget.WanContextId"/> (probe routing) and
/// <see cref="MonitoringTarget.WanInterface"/> (which WAN the data describes - the key every
/// per-WAN reader scopes on) moving together at runtime.
/// <para>
/// Every target states its WAN, including the ones bound to none: those carry
/// <see cref="MonitoringTarget.UnpinnedWan"/> rather than NULL. An absent value had to be
/// interpreted, and the interpretations disagreed - most readers took it for the primary, which is
/// only true on a failover site, while the one governing discovery writes took it for "belongs to
/// whatever WAN is asking", which let a metered secondary slow every hand-added target on the
/// site. Naming the state removes the inference without inventing an attribution no load-balancing
/// site could honestly make.
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
        // A context that names no WAN says no more about attribution than no context at all does -
        // one exists on every install that predates the WAN column - so both land on unpinned
        // rather than on a null this column no longer carries.
        target.WanInterface = wanContextId == null
            ? MonitoringTarget.UnpinnedWan
            : Pinned(contextWanInterface);
    }

    /// <summary>The WAN key to store, or the unpinned marker when there is nothing to store.</summary>
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
    /// Moves a deleted context's targets back to unpinned: nothing binds their probes to a WAN any
    /// more, and a row keeping the dead context's WAN would stay filed under a WAN nothing probes
    /// for it. Caller saves.
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
