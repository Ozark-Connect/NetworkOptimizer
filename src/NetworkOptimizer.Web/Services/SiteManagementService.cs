using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages the site registry and multi-site feature state. Creating a site
/// provisions its own SQLite database file by running the full EF migration
/// set against a fresh file under sites/{slug}/.
/// </summary>
public class SiteManagementService
{
    /// <summary>Slug reserved for the default site (the pre-multi-site instance).</summary>
    public const string DefaultSiteSlug = "main";

    private readonly ISiteRepository _siteRepository;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly SiteDatabasePaths _dbPaths;
    private readonly ILogger<SiteManagementService> _logger;

    public SiteManagementService(
        ISiteRepository siteRepository,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteDatabasePaths dbPaths,
        ILogger<SiteManagementService> logger)
    {
        _siteRepository = siteRepository;
        _mainDbFactory = mainDbFactory;
        _dbPaths = dbPaths;
        _logger = logger;
    }

    /// <summary>
    /// Whether multi-site management is enabled on this instance. The flag is
    /// instance-wide, so it is read from the main database via the factory rather
    /// than the scoped, site-routed context.
    /// </summary>
    public async Task<bool> IsMultiSiteEnabledAsync()
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var setting = await db.SystemSettings.FindAsync(SystemSettingKeys.MultiSiteEnabled);
        return bool.TryParse(setting?.Value, out var enabled) && enabled;
    }

    /// <summary>
    /// Enables or disables multi-site management. Enabling ensures the default
    /// site registry row exists. Disabling only hides the multi-site UX; no
    /// site data is ever removed.
    /// </summary>
    public async Task SetMultiSiteEnabledAsync(bool enabled)
    {
        await using var db = await _mainDbFactory.CreateDbContextAsync();
        var setting = await db.SystemSettings.FindAsync(SystemSettingKeys.MultiSiteEnabled);
        if (setting == null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.MultiSiteEnabled, Value = enabled.ToString() });
        }
        else
        {
            setting.Value = enabled.ToString();
            setting.UpdatedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();

        if (enabled)
            await EnsureDefaultSiteAsync();
        _logger.LogInformation("Multi-site management {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Sites permitted under the BSL Additional Use Grant (personal,
    /// non-commercial use on up to three sites). A future licensing / unlock-key
    /// scheme will raise this - <see cref="GetSiteLimitAsync"/> is the single
    /// place that decision hooks into.
    /// </summary>
    public const int FreeSiteLimit = 3;

    /// <summary>
    /// The effective maximum number of sites for this instance. Today it is
    /// always <see cref="FreeSiteLimit"/>; the future unlock key raises it here.
    /// </summary>
    public Task<int> GetSiteLimitAsync() => Task.FromResult(FreeSiteLimit);

    /// <summary>How many more sites may be created before hitting the limit.</summary>
    public async Task<int> RemainingSiteSlotsAsync()
    {
        var limit = await GetSiteLimitAsync();
        var count = (await GetSitesAsync()).Count;
        return Math.Max(0, limit - count);
    }

    /// <summary>Gets all registered sites, with the default (main) site always first.</summary>
    public async Task<List<Site>> GetSitesAsync()
    {
        var sites = await _siteRepository.GetAllAsync();
        return sites.OrderByDescending(s => s.IsDefault).ToList();
    }

    /// <summary>Updates a site's mutable fields (name, enabled, sort order, notes).</summary>
    public Task UpdateSiteAsync(Site site) => _siteRepository.UpdateAsync(site);

    /// <summary>
    /// Previews the slug that would be generated for a site name, including
    /// uniqueness suffixing against existing sites.
    /// </summary>
    public async Task<string> PreviewSlugAsync(string name)
    {
        return await GenerateUniqueSlugAsync(name);
    }

    /// <summary>
    /// Creates a new site: registers it with an auto-generated immutable slug and
    /// provisions its database file with the current schema.
    /// </summary>
    public async Task<Site> CreateSiteAsync(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Site name is required", nameof(name));

        var limit = await GetSiteLimitAsync();
        if ((await _siteRepository.GetAllAsync()).Count >= limit)
            throw new InvalidOperationException(
                $"This instance is limited to {limit} sites under the current license. " +
                "Remove a site, or unlock more, to add another.");

        var slug = await GenerateUniqueSlugAsync(name);
        var site = new Site { Slug = slug, Name = name.Trim() };

        await ProvisionSiteDatabaseAsync(slug);
        await _siteRepository.AddAsync(site);
        return site;
    }

    private async Task EnsureDefaultSiteAsync()
    {
        var existing = await _siteRepository.GetDefaultAsync();
        if (existing != null)
            return;

        await _siteRepository.AddAsync(new Site
        {
            Slug = DefaultSiteSlug,
            Name = "Main Site",
            IsDefault = true,
        });
    }

    private async Task<string> GenerateUniqueSlugAsync(string name)
    {
        var baseSlug = StringUtilities.ToSlug(name);
        var existing = (await _siteRepository.GetAllAsync())
            .Select(s => s.Slug)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        existing.Add(DefaultSiteSlug);

        if (!existing.Contains(baseSlug))
            return baseSlug;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseSlug}-{i}";
            if (!existing.Contains(candidate))
                return candidate;
        }
    }

    private async Task ProvisionSiteDatabaseAsync(string slug)
    {
        var dbPath = _dbPaths.GetSiteDbPath(slug, isDefault: false);
        Directory.CreateDirectory(_dbPaths.GetSiteDataDir(slug));

        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        await using var db = new NetworkOptimizerDbContext(options);
        await db.Database.MigrateAsync();
        _logger.LogInformation("Provisioned site database for {Slug} at {Path}", slug, dbPath);
    }
}
