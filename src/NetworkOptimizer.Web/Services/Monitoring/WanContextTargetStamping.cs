using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Keeps <see cref="MonitoringTarget.WanContextId"/> (probe routing) and
/// <see cref="MonitoringTarget.WanInterface"/> (which WAN the data describes - the key every
/// per-WAN reader scopes on) moving together at runtime. The deploy-time backfill migration
/// only fixed rows that existed then; every later assignment, context edit, and context
/// deletion goes through here so the two keys can never drift apart again.
/// </summary>
public static class WanContextTargetStamping
{
    /// <summary>
    /// The WanInterface a target should carry after a WAN-context (re)assignment: the context's
    /// WAN, or null when the target moves back to the primary (an unstamped target IS a
    /// primary-path measurement to every scoped reader).
    /// </summary>
    public static void ApplyAssignment(MonitoringTarget target, int? wanContextId, string? contextWanInterface)
    {
        target.WanContextId = wanContextId;
        target.WanInterface = wanContextId == null ? null : contextWanInterface;
    }

    /// <summary>
    /// Re-stamps every target assigned to a context after the context's WAN changed, so their
    /// data is attributed to the WAN the context now measures. Caller saves.
    /// </summary>
    public static async Task<int> RestampContextTargetsAsync(
        NetworkOptimizerDbContext db, int wanContextId, string? wanInterface, CancellationToken ct = default)
    {
        var targets = await db.MonitoringTargets.Where(t => t.WanContextId == wanContextId).ToListAsync(ct);
        foreach (var target in targets)
            target.WanInterface = wanInterface;
        return targets.Count;
    }

    /// <summary>
    /// Moves a deleted context's targets back to the primary: both keys cleared, because a row
    /// keeping the dead context's WAN stamp would stay invisible to the primary report while no
    /// context probes it any more. Caller saves.
    /// </summary>
    public static async Task<int> ReleaseContextTargetsAsync(
        NetworkOptimizerDbContext db, int wanContextId, CancellationToken ct = default)
    {
        var targets = await db.MonitoringTargets.Where(t => t.WanContextId == wanContextId).ToListAsync(ct);
        foreach (var target in targets)
            ApplyAssignment(target, null, null);
        return targets.Count;
    }
}
