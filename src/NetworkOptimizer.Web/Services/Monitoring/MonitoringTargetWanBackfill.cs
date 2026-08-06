using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Gives every unstamped monitoring target its WAN, so no reader has to infer one.
/// <para>
/// This cannot be a migration. The primary is a role held by any WAN group, and the site database
/// only learns which one while a console is answering - which it is not, reliably, at the moment
/// migrations run. So the stamping is deferred: it runs at startup and again on the monitoring
/// tick, does nothing until the role is knowable, and stamps once it is. Idempotent by
/// construction, since a stamped row no longer matches.
/// </para>
/// </summary>
public static class MonitoringTargetWanBackfill
{
    /// <summary>
    /// Stamps every target that has no WAN with the site's primary. Returns how many were
    /// stamped, or 0 when there was nothing to do or the primary could not be resolved.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> StampUnstampedAsync(NetworkOptimizerDbContext db, CancellationToken ct = default)
    {
        var unstamped = await db.MonitoringTargets
            .Where(t => t.WanInterface == null || t.WanInterface == "")
            .ToListAsync(ct);
        if (unstamped.Count == 0) return 0;

        var primaryWanKey = await PrimaryWanResolver.ResolveKeyAsync(db, ct);
        if (string.IsNullOrEmpty(primaryWanKey)) return 0;

        foreach (var target in unstamped)
            target.WanInterface = primaryWanKey;
        await db.SaveChangesAsync(ct);
        return unstamped.Count;
    }
}
