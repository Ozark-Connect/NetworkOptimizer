using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Bridges the schedule loop to the site registry: enumerates the sites whose
/// schedules should run (just the default site until multi-site is enabled)
/// and pins evaluation scopes to a site so the schedule repository and the
/// audit pipeline resolve that site's database and console connection.
/// </summary>
public class ScheduleSiteContext : IScheduleSiteContext
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;

    public ScheduleSiteContext(IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory)
    {
        _mainDbFactory = mainDbFactory;
    }

    public string DefaultKey => SiteManagementService.DefaultSiteSlug;

    public async Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync(ct);
        var setting = await db.SystemSettings.FindAsync(new object[] { SystemSettingKeys.MultiSiteEnabled }, ct);
        if (!bool.TryParse(setting?.Value, out var enabled) || !enabled)
            return new[] { DefaultKey };

        var slugs = await db.Sites.AsNoTracking()
            .Where(s => s.Enabled)
            .OrderBy(s => s.IsDefault ? 0 : 1)
            .ThenBy(s => s.SortOrder)
            .ThenBy(s => s.Slug)
            .Select(s => s.Slug)
            .ToListAsync(ct);
        if (!slugs.Contains(DefaultKey))
            slugs.Insert(0, DefaultKey);
        return slugs;
    }

    public void PinScope(IServiceScope scope, string siteKey) =>
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteKey);
}
