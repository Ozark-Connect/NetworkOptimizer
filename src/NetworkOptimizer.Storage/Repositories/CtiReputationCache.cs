using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.CrowdSec;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// The instance-wide CrowdSec CTI cache, in the main database. The factory always opens the main
/// database, so a site's request reads the same rows as every other site.
/// </summary>
public sealed class CtiReputationCache(IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
    ILogger<CtiReputationCache> logger) : ICtiReputationCache
{
    public async Task<CrowdSecReputation?> GetAsync(string ip, CancellationToken cancellationToken = default)
    {
        if (NetworkUtilities.IsPrivateIpAddress(ip)) return null;
        await using var db = await mainDbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CrowdSecReputations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Ip == ip && r.ExpiresAt > DateTime.UtcNow, cancellationToken);
    }

    public async Task SaveAsync(CrowdSecReputation reputation, CancellationToken cancellationToken = default)
    {
        if (NetworkUtilities.IsPrivateIpAddress(reputation.Ip)) return;
        await using var db = await mainDbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CrowdSecReputations.FirstOrDefaultAsync(r => r.Ip == reputation.Ip, cancellationToken);
        if (existing != null)
        {
            existing.ReputationJson = reputation.ReputationJson;
            existing.FetchedAt = reputation.FetchedAt;
            existing.ExpiresAt = reputation.ExpiresAt;
        }
        else
        {
            db.CrowdSecReputations.Add(new CrowdSecReputation
            {
                Ip = reputation.Ip,
                ReputationJson = reputation.ReputationJson,
                FetchedAt = reputation.FetchedAt,
                ExpiresAt = reputation.ExpiresAt
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await mainDbFactory.CreateDbContextAsync(cancellationToken);
        var count = await db.CrowdSecReputations.Where(r => r.ExpiresAt < DateTime.UtcNow).ExecuteDeleteAsync(cancellationToken);
        if (count > 0)
            logger.LogDebug("Purged {Count} expired CrowdSec cache entries", count);
    }

    /// <summary>
    /// Copies a site database's unexpired answers into the main database, so the first startup on the
    /// shared cache does not spend quota again on addresses a site already looked up. Site databases
    /// are no longer written, so their rows expire and this becomes a no-op. Returns the rows copied.
    /// </summary>
    public static int MergeFromSite(NetworkOptimizerDbContext main, NetworkOptimizerDbContext site)
    {
        var now = DateTime.UtcNow;
        var rows = site.CrowdSecReputations.AsNoTracking().Where(r => r.ExpiresAt > now).ToList();
        if (rows.Count == 0) return 0;

        var have = main.CrowdSecReputations.AsNoTracking()
            .Where(r => r.ExpiresAt > now).Select(r => r.Ip).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var copied = 0;
        foreach (var row in rows.Where(r => !have.Contains(r.Ip) && !NetworkUtilities.IsPrivateIpAddress(r.Ip)))
        {
            // An expired row for the same IP may still be in the main database until the nightly purge.
            if (main.CrowdSecReputations.Find(row.Ip) is { } stale)
            {
                stale.ReputationJson = row.ReputationJson;
                stale.FetchedAt = row.FetchedAt;
                stale.ExpiresAt = row.ExpiresAt;
            }
            else
            {
                main.CrowdSecReputations.Add(row);
            }
            have.Add(row.Ip);
            copied++;
        }
        if (copied > 0) main.SaveChanges();
        return copied;
    }
}
