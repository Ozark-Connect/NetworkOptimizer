using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>A score and grade for the LAN Health tile, as the ISP Health tile shows its own.</summary>
public sealed record LanHealthTile(double? Score, bool Ready, int Findings);

/// <summary>
/// Monitoring - LAN Health for the site in context. Reads are open to any role; exclusions are
/// Site Admin and audited. The detectors change nothing, so they need no audit entries.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface ILanHealthService
{
    /// <summary>The report over the last <paramref name="hours"/> hours, findings on the viewer's clock.</summary>
    [RequireRole(Roles.Viewer)]
    Task<LanHealthReport> GetReportAsync(int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct = default);

    /// <summary>One link's evaluation over the window, or null when the link is not on the site.</summary>
    [RequireRole(Roles.Viewer)]
    Task<LinkHealth?> GetLinkAsync(string linkId, int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct = default);

    /// <summary>The tile's score: the latest roll-up, refreshed live while someone watches.</summary>
    [RequireRole(Roles.Viewer)]
    Task<LanHealthTile> GetTileAsync(CancellationToken ct = default);

    /// <summary>The site's exclusions.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<LanHealthExclusion>> GetExclusionsAsync(CancellationToken ct = default);

    /// <summary>Adds or changes an exclusion (lanh-44). A fabric link can only be muted.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.LanHealthExclusionSaved, Category = AuditCategories.Settings, TargetType = "lan_health_exclusion")]
    Task SaveExclusionAsync(string scope, string key, ExclusionLevel level, string? label, CancellationToken ct = default);

    /// <summary>Removes an exclusion.</summary>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.LanHealthExclusionRemoved, Category = AuditCategories.Settings, TargetType = "lan_health_exclusion")]
    Task RemoveExclusionAsync(string scope, string key, CancellationToken ct = default);
}

/// <inheritdoc cref="ILanHealthService" />
public sealed class LanHealthService : ILanHealthService
{
    private static readonly HashSet<string> Scopes = ["profile", "port", "device", "access-all"];

    private readonly LanHealthRegistry _registry;
    private readonly SiteContextService _site;
    private readonly NetworkOptimizerDbContext _db;

    public LanHealthService(LanHealthRegistry registry, SiteContextService site, NetworkOptimizerDbContext db)
    {
        _registry = registry;
        _site = site;
        _db = db;
    }

    private LanHealthEngine Engine => _registry.GetFor(_site.Slug);

    /// <inheritdoc />
    public Task<LanHealthReport> GetReportAsync(int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct = default) =>
        Engine.GetReportAsync(Math.Clamp(hours, 1, 24 * 30), zone, format, ct);

    /// <inheritdoc />
    public Task<LinkHealth?> GetLinkAsync(string linkId, int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct = default) =>
        Engine.GetLinkAsync(linkId, Math.Clamp(hours, 1, 24 * 30), zone, format, ct);

    /// <inheritdoc />
    public async Task<LanHealthTile> GetTileAsync(CancellationToken ct = default)
    {
        var report = await Engine.GetReportAsync(48, TimeZoneInfo.Utc, DisplayFormat.Fallback, ct);
        return new LanHealthTile(report.Score, report.Ready, report.Findings.Count(f => f.Points > 0));
    }

    /// <inheritdoc />
    public Task<List<LanHealthExclusion>> GetExclusionsAsync(CancellationToken ct = default) =>
        _db.LanHealthExclusions.AsNoTracking().OrderBy(e => e.Scope).ThenBy(e => e.Label).ToListAsync(ct);

    /// <inheritdoc />
    public async Task SaveExclusionAsync(string scope, string key, ExclusionLevel level, string? label, CancellationToken ct = default)
    {
        if (!Scopes.Contains(scope)) throw new ArgumentException($"Unknown exclusion scope {scope}", nameof(scope));
        key = scope == "access-all" ? "" : key.Trim();
        // A fabric fault hits everyone behind it: fabric links can be muted, never excluded.
        if (scope == "port" && level == ExclusionLevel.ExcludeFromScore && !key.EndsWith("-access", StringComparison.Ordinal))
            level = ExclusionLevel.MuteAlerts;
        var row = await _db.LanHealthExclusions.FirstOrDefaultAsync(e => e.Scope == scope && e.Key == key, ct);
        if (row == null)
        {
            row = new LanHealthExclusion { Scope = scope, Key = key, CreatedAt = DateTime.UtcNow };
            _db.LanHealthExclusions.Add(row);
        }
        row.Level = (int)level;
        row.Label = label is { Length: > 200 } l ? l[..200] : label;
        await _db.SaveChangesAsync(ct);
        await Engine.InvalidateAsync(ct);
    }

    /// <inheritdoc />
    public async Task RemoveExclusionAsync(string scope, string key, CancellationToken ct = default)
    {
        var rows = await _db.LanHealthExclusions.Where(e => e.Scope == scope && e.Key == key).ToListAsync(ct);
        _db.LanHealthExclusions.RemoveRange(rows);
        await _db.SaveChangesAsync(ct);
        await Engine.InvalidateAsync(ct);
    }
}
