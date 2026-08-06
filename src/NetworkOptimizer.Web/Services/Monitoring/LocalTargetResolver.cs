using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Settles whether a target sits on the local network, once, and writes the answer down.
/// <para>
/// A literal address answers itself. A hostname does not - "cloudkey.local" and
/// "speedtest.example.net" are the same shape - so it is resolved to the address it actually
/// reaches and judged on that. Kept because it almost never changes and because the alternative is
/// a DNS lookup on every read of a column three different features consult.
/// </para>
/// </summary>
public static class LocalTargetResolver
{
    /// <summary>How many unresolved targets one sweep will look up, so a site with a long list of
    /// dead hostnames cannot spend a whole tick on DNS.</summary>
    private const int SweepBatchSize = 25;

    /// <summary>
    /// Whether a target is on the local network: what was resolved if anything was, otherwise what
    /// the address itself says. Fabric is local by what it is, whatever address it wears.
    /// </summary>
    public static bool IsLocal(MonitoringTarget target) =>
        IsLocal(target.TargetType, target.Address, target.IsLocal);

    /// <summary>
    /// The same question from loose fields, for callers projecting columns rather than loading
    /// entities.
    /// </summary>
    /// <param name="targetType">The target's type; Fabric is local whatever its address.</param>
    /// <param name="address">The configured address.</param>
    /// <param name="isLocal">The resolved answer, or null when nothing has settled it.</param>
    public static bool IsLocal(MonitoringTargetType targetType, string? address, bool? isLocal) =>
        targetType == MonitoringTargetType.Fabric
        || (isLocal ?? NetworkUtilities.IsPrivateIpAddress(address ?? string.Empty));

    /// <summary>
    /// Resolves one address to a local/not-local answer, or null when DNS cannot say. Never
    /// throws: an unanswerable name leaves the target unresolved rather than wrongly settled.
    /// </summary>
    /// <param name="address">The target's address, a literal or a hostname.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<bool?> ResolveAsync(string? address, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        try
        {
            var (ip, _) = await ReverseDnsCache.ResolveAsync(address.Trim(), ct);
            // ResolveAsync hands back the address unchanged when it could not resolve it, so a
            // name that answered nothing must not be judged as though it were an address.
            if (string.IsNullOrEmpty(ip)
                || (!System.Net.IPAddress.TryParse(ip, out _)))
                return null;
            return NetworkUtilities.IsPrivateIpAddress(ip);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves targets that have no answer yet. Returns how many were settled. Only touches nulls,
    /// so it costs nothing once a site is resolved and needs no run-once bookkeeping. Caller saves.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> SweepUnresolvedAsync(NetworkOptimizerDbContext db, CancellationToken ct = default)
    {
        var pending = await db.MonitoringTargets
            .Where(t => t.IsLocal == null)
            .OrderBy(t => t.Id)
            .Take(SweepBatchSize)
            .ToListAsync(ct);
        if (pending.Count == 0) return 0;

        var settled = 0;
        foreach (var target in pending)
        {
            if (ct.IsCancellationRequested) break;
            var answer = await ResolveAsync(target.Address, ct);
            if (answer == null) continue;
            target.IsLocal = answer;
            settled++;
        }
        return settled;
    }
}
