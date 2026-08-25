using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Reads what the AP Agents recorded about roaming and radio health on the site in context.
///
/// Site-scoped and read-only: roams and radio counters belong to the site's own access points, and
/// both surface on monitoring pages that are open to any role, so every method is Viewer. Nothing
/// here writes; the collectors do that from the monitoring tier loop.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IApAgentRoamHistoryService
{
    /// <summary>The site's most recent roams, newest first.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApRoamRecord>> GetRecentRoamsAsync(DateTime since, int limit = 500, CancellationToken ct = default);

    /// <summary>One client's roams, newest first. The MAC is the MLD MAC for an MLO client.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApRoamRecord>> GetClientRoamsAsync(string clientMac, DateTime since, int limit = 200, CancellationToken ct = default);

    /// <summary>How far this server has read each access point's event ring, truncations included.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApAgentEventCursor>> GetEventCursorsAsync(CancellationToken ct = default);

    /// <summary>One access point's radio counter windows over a period, oldest first.</summary>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<ApRadioHealthSample>> GetRadioHealthAsync(string apMac, DateTime since, CancellationToken ct = default);
}

/// <inheritdoc cref="IApAgentRoamHistoryService" />
public sealed class ApAgentRoamHistoryService : IApAgentRoamHistoryService
{
    /// <summary>Ceiling on any one read, so a wide window cannot pull a season into memory.</summary>
    private const int MaxRows = 5000;

    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;

    /// <param name="siteDbFactory">Per-site database factory.</param>
    /// <param name="siteContext">The site this scope operates on.</param>
    public ApAgentRoamHistoryService(SiteDbContextFactory siteDbFactory, SiteContextService siteContext)
    {
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApRoamRecord>> GetRecentRoamsAsync(
        DateTime since, int limit = 500, CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.ApRoamRecords.AsNoTracking()
            .Where(r => r.RoamedAt >= since)
            .OrderByDescending(r => r.RoamedAt)
            .Take(Math.Clamp(limit, 1, MaxRows))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApRoamRecord>> GetClientRoamsAsync(
        string clientMac, DateTime since, int limit = 200, CancellationToken ct = default)
    {
        var mac = (clientMac ?? "").Trim().ToLowerInvariant();
        if (mac.Length == 0) return Array.Empty<ApRoamRecord>();

        using var db = CreateDb();
        return await db.ApRoamRecords.AsNoTracking()
            .Where(r => r.ClientMac == mac && r.RoamedAt >= since)
            .OrderByDescending(r => r.RoamedAt)
            .Take(Math.Clamp(limit, 1, MaxRows))
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApAgentEventCursor>> GetEventCursorsAsync(CancellationToken ct = default)
    {
        using var db = CreateDb();
        return await db.ApAgentEventCursors.AsNoTracking()
            .OrderBy(c => c.DeviceMac)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApRadioHealthSample>> GetRadioHealthAsync(
        string apMac, DateTime since, CancellationToken ct = default)
    {
        var mac = (apMac ?? "").Trim().ToLowerInvariant();
        if (mac.Length == 0) return Array.Empty<ApRadioHealthSample>();

        using var db = CreateDb();
        return await db.ApRadioHealthSamples.AsNoTracking()
            .Where(s => s.ApMac == mac && s.SampleAt >= since)
            .OrderBy(s => s.SampleAt)
            .Take(MaxRows)
            .ToListAsync(ct);
    }

    private Storage.Models.NetworkOptimizerDbContext CreateDb()
        => _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
}
