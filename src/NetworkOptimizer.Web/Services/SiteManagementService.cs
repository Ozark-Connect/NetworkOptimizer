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
    private readonly ISettingsRepository _settingsRepository;
    private readonly SiteDatabasePaths _dbPaths;
    private readonly ILogger<SiteManagementService> _logger;

    public SiteManagementService(
        ISiteRepository siteRepository,
        ISettingsRepository settingsRepository,
        SiteDatabasePaths dbPaths,
        ILogger<SiteManagementService> logger)
    {
        _siteRepository = siteRepository;
        _settingsRepository = settingsRepository;
        _dbPaths = dbPaths;
        _logger = logger;
    }

    /// <summary>Whether multi-site management is enabled on this instance.</summary>
    public async Task<bool> IsMultiSiteEnabledAsync()
    {
        var value = await _settingsRepository.GetSystemSettingAsync(SystemSettingKeys.MultiSiteEnabled);
        return bool.TryParse(value, out var enabled) && enabled;
    }

    /// <summary>
    /// Enables or disables multi-site management. Enabling ensures the default
    /// site registry row exists. Disabling only hides the multi-site UX; no
    /// site data is ever removed.
    /// </summary>
    public async Task SetMultiSiteEnabledAsync(bool enabled)
    {
        await _settingsRepository.SaveSystemSettingAsync(SystemSettingKeys.MultiSiteEnabled, enabled.ToString());
        if (enabled)
            await EnsureDefaultSiteAsync();
        _logger.LogInformation("Multi-site management {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>Gets all registered sites.</summary>
    public Task<List<Site>> GetSitesAsync() => _siteRepository.GetAllAsync();

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
