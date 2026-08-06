using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Which WAN a site treats as its primary, from the site database alone.
/// <para>
/// Primary is a ROLE, not a name: any WAN group can hold it, and a site whose primary is WAN2 is
/// perfectly ordinary. Nothing here ever falls back to <see cref="GatewayWanHelper.DefaultWanKey"/>
/// - a guess that names the wrong WAN would be stamped into rows and outlive the console coming
/// back, which is worse than not answering. Callers get null and defer.
/// </para>
/// </summary>
public static class PrimaryWanResolver
{
    /// <summary>
    /// The site's primary WAN key, normalized, or null when the site database cannot say yet.
    /// <para>
    /// Three sources, best first. The recorded role is authoritative but only written while a
    /// console is answering, so it is null on a site that has just upgraded. A site with one WAN
    /// profile has only one candidate. Failing both, the WAN that discovery has run against but
    /// which owns no context is the unbound run's WAN - and the unbound run is the primary's,
    /// because a secondary can only be traced through a context.
    /// </para>
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<string?> ResolveKeyAsync(NetworkOptimizerDbContext db, CancellationToken ct = default)
    {
        var profiles = await db.WanProfiles.AsNoTracking().ToListAsync(ct);

        var recorded = profiles.FirstOrDefault(w => w.IsPrimary == true)?.WanNetworkgroup;
        if (!string.IsNullOrEmpty(recorded)) return GatewayWanHelper.WanInterfaceKeyFromKey(recorded);

        var only = profiles.Count == 1 ? profiles[0].WanNetworkgroup : null;
        if (!string.IsNullOrEmpty(only)) return GatewayWanHelper.WanInterfaceKeyFromKey(only);

        return await ResolveFromUncontextedDiscoveryAsync(db, ct);
    }

    /// <summary>
    /// The WAN that discovery has run against but which holds no context. Context runs are the
    /// only way a secondary WAN is ever traced, so a discovered WAN with no context was traced by
    /// the unbound run - the primary's. Returns null when that is not exactly one WAN, which is
    /// the case on a site that has assigned its primary to a context too.
    /// </summary>
    private static async Task<string?> ResolveFromUncontextedDiscoveryAsync(
        NetworkOptimizerDbContext db, CancellationToken ct)
    {
        var discovered = (await db.WanDiscoveryContexts.AsNoTracking().Select(c => c.WanInterface).ToListAsync(ct))
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => GatewayWanHelper.WanInterfaceKeyFromKey(k!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (discovered.Count == 0) return null;

        var contexted = (await db.WanContexts.AsNoTracking().Select(c => c.WanInterface).ToListAsync(ct))
            .Where(k => !string.IsNullOrEmpty(k))
            .Select(k => GatewayWanHelper.WanInterfaceKeyFromKey(k!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        discovered.ExceptWith(contexted);
        return discovered.Count == 1 ? discovered.First() : null;
    }
}
