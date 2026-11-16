using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>A place a trace gave a hop's address.</summary>
/// <param name="Source">What placed it: <see cref="HopLocationSource.Hostname"/> or <see cref="HopLocationSource.Neighbor"/>.</param>
/// <param name="Detail">How that trace placed it, as its tooltip said.</param>
public sealed record LearnedHop(string Address, string City, string CountryCode, double Latitude, double Longitude,
    HopLocationSource Source, string Detail, DateTime SeenAt);

/// <summary>
/// The strongest place traces gave each transit or peering router (<see cref="HopLocations.Learnable"/>). One trace
/// may name a router by its hostname or a hop beside it while the next, missing the PTR or the neighbor, has only
/// GeoLite2, which for transit routers is often where the network is registered. Install-wide, kept per site that
/// saw it: an address two sites learned in different cities is used only by each site's own traces. Derived data
/// that changes no setting, so not audited.
/// </summary>
public sealed class LearnedHopLocations
{
    /// <summary>A place no trace has given again in this long is dropped: routers move and are renumbered.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    // A place given again this soon is not written again: every render of a trace table would otherwise write.
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);

    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly ILogger<LearnedHopLocations> _logger;
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile IReadOnlyList<(string Site, LearnedHop Hop)>? _rows;

    public LearnedHopLocations(IDbContextFactory<NetworkOptimizerDbContext> mainDb, ILogger<LearnedHopLocations> logger)
    {
        _mainDb = mainDb;
        _logger = logger;
    }

    /// <summary>
    /// The places a trace from <paramref name="siteSlug"/> uses, by address: every site's, except where sites
    /// disagree on the city, which only that site's own place settles. Empty when none can be read.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, LearnedHop>> GetAsync(string siteSlug, CancellationToken ct = default) =>
        ForSite(await RowsAsync(ct), siteSlug, DateTime.UtcNow);

    /// <summary>The merge <see cref="GetAsync"/> does, over rows already read.</summary>
    internal static Dictionary<string, LearnedHop> ForSite(IEnumerable<(string Site, LearnedHop Hop)> rows, string siteSlug, DateTime now)
    {
        var result = new Dictionary<string, LearnedHop>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in rows.Where(r => now - r.Hop.SeenAt <= MaxAge).GroupBy(r => r.Hop.Address, StringComparer.OrdinalIgnoreCase))
        {
            var agree = g.Select(r => r.Hop.City).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
            var usable = agree ? g : g.Where(r => string.Equals(r.Site, siteSlug, StringComparison.OrdinalIgnoreCase));
            if (usable.OrderBy(r => r.Hop.Source).ThenByDescending(r => r.Hop.SeenAt).Select(r => r.Hop).FirstOrDefault() is { } best)
                result[g.Key] = best;
        }
        return result;
    }

    /// <summary>
    /// Keeps the transit and peering routers a trace from <paramref name="siteSlug"/> placed by hostname or a nearby
    /// hop: a stronger source replaces that site's weaker one, the same source a different or older one. Never throws.
    /// </summary>
    /// <param name="hops">The trace's responding hops, in hop order.</param>
    public async Task LearnAsync(string siteSlug, IReadOnlyList<(string? Address, string? Hostname, double? MinRttMs)> hops,
        IReadOnlyDictionary<string, HopLocationView> found, Func<string, GeoInfo> geo, CancellationToken ct = default)
    {
        try
        {
            var now = DateTime.UtcNow;
            var mine = (await RowsAsync(ct)).Where(r => string.Equals(r.Site, siteSlug, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(r => r.Hop.Address, r => r.Hop, StringComparer.OrdinalIgnoreCase);
            var changes = HopLocations.Learnable(hops, found, geo, now)
                .Where(h => Replaces(h, mine.GetValueOrDefault(h.Address), now)).ToList();
            if (changes.Count == 0) return;

            await _write.WaitAsync(ct);
            try
            {
                await using var db = await _mainDb.CreateDbContextAsync(ct);
                var addresses = changes.Select(c => c.Address).ToList();
                var rows = await db.LearnedHopLocations.Where(r => r.SiteSlug == siteSlug && addresses.Contains(r.Address))
                    .ToDictionaryAsync(r => r.Address, StringComparer.OrdinalIgnoreCase, ct);
                foreach (var c in changes)
                {
                    if (!rows.TryGetValue(c.Address, out var row))
                    {
                        row = new LearnedHopLocation { SiteSlug = siteSlug, Address = c.Address };
                        db.LearnedHopLocations.Add(row);
                        rows[c.Address] = row;
                    }
                    else if (!Replaces(c, ToHop(row), now)) continue;
                    row.City = c.City;
                    row.CountryCode = c.CountryCode;
                    row.Latitude = c.Latitude;
                    row.Longitude = c.Longitude;
                    row.Source = (int)c.Source;
                    row.Detail = c.Detail.Length > 300 ? c.Detail[..300] : c.Detail;
                    row.SeenAt = now;
                }
                await db.SaveChangesAsync(ct);
                var expired = now - MaxAge;
                await db.LearnedHopLocations.Where(r => r.SeenAt < expired).ExecuteDeleteAsync(ct);
                _rows = null;
            }
            finally
            {
                _write.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not keep the learned hop locations for site {Site}", siteSlug);
        }
    }

    /// <summary>Whether <paramref name="next"/> should be kept over the same site's <paramref name="current"/>.</summary>
    internal static bool Replaces(LearnedHop next, LearnedHop? current, DateTime now)
    {
        if (current == null || now - current.SeenAt > MaxAge) return true;
        if (next.Source < current.Source) return true;
        if (next.Source > current.Source) return false;
        return !string.Equals(next.City, current.City, StringComparison.OrdinalIgnoreCase) || now - current.SeenAt >= RefreshEvery;
    }

    private async Task<IReadOnlyList<(string Site, LearnedHop Hop)>> RowsAsync(CancellationToken ct)
    {
        if (_rows is { } cached) return cached;
        try
        {
            await using var db = await _mainDb.CreateDbContextAsync(ct);
            var since = DateTime.UtcNow - MaxAge;
            var rows = await db.LearnedHopLocations.AsNoTracking().Where(r => r.SeenAt >= since).ToListAsync(ct);
            var list = rows.Select(r => (r.SiteSlug, ToHop(r))).ToList();
            _rows = list;
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the learned hop locations");
            return [];
        }
    }

    private static LearnedHop ToHop(LearnedHopLocation r) =>
        new(r.Address, r.City, r.CountryCode, r.Latitude, r.Longitude, (HopLocationSource)r.Source, r.Detail, r.SeenAt);
}
