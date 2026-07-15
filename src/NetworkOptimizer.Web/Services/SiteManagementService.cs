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
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly Licensing.LicenseActivationService _activation;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly MonitoringCollectionRegistry _collectionRegistry;
    private readonly SiteRegistryChangeNotifier _changeNotifier;
    private readonly ILogger<SiteManagementService> _logger;

    public SiteManagementService(
        ISiteRepository siteRepository,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        SiteDatabasePaths dbPaths,
        Licensing.LicenseStateService licenseState,
        Licensing.LicenseActivationService activation,
        SiteConnectionRegistry siteConnections,
        MonitoringCollectionRegistry collectionRegistry,
        SiteRegistryChangeNotifier changeNotifier,
        ILogger<SiteManagementService> logger)
    {
        _siteRepository = siteRepository;
        _mainDbFactory = mainDbFactory;
        _dbPaths = dbPaths;
        _licenseState = licenseState;
        _activation = activation;
        _siteConnections = siteConnections;
        _collectionRegistry = collectionRegistry;
        _changeNotifier = changeNotifier;
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
        {
            await EnsureDefaultSiteAsync();
            // The implicit main site now has a real registry row. Assign it (and any
            // other existing sites) to active keys so the consumed/available seat
            // counts carry over seamlessly from the single-site view.
            await _activation.AutoAssignAsync();
        }

        // Always notify: toggling multi-site changes what the Licensing card shows even
        // when the license snapshot is unchanged (e.g. the main site was already covered),
        // so force subscribers to reload rather than relying on a snapshot diff.
        await _licenseState.RecomputeAsync(alwaysNotify: true);
        _changeNotifier.NotifySitesChanged();
        _logger.LogInformation("Multi-site management {State}", enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Sites permitted under the BSL Additional Use Grant (personal,
    /// non-commercial use on up to three sites). License keys raise the
    /// effective limit through <see cref="GetSiteLimitAsync"/>.
    /// </summary>
    public const int FreeSiteLimit = 3;

    /// <summary>
    /// The effective maximum number of sites for this instance: the free-tier
    /// limit with no active licensing, otherwise the summed allowance of the
    /// active license keys. Keys in their post-expiry grace period grant no
    /// headroom for creating new sites (their already-assigned sites keep
    /// working through grace).
    /// </summary>
    public Task<int> GetSiteLimitAsync() => Task.FromResult(
        _licenseState.AnyKeysActive ? _licenseState.TotalAllowance : FreeSiteLimit);

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
    public async Task UpdateSiteAsync(Site site)
    {
        await _siteRepository.UpdateAsync(site);
        _changeNotifier.NotifySitesChanged();
    }

    /// <summary>
    /// Enables or disables a secondary site. Disabling stops its monitoring
    /// collection and drops its console connection immediately and hides it from
    /// the site switcher; all of its data is kept and re-enabling restores it.
    /// </summary>
    public async Task SetSiteEnabledAsync(Site site, bool enabled)
    {
        if (site.IsDefault)
            throw new InvalidOperationException("The default site cannot be disabled.");

        site.Enabled = enabled;
        await _siteRepository.UpdateAsync(site);

        if (!enabled)
        {
            await _collectionRegistry.StopForSiteAsync(site.Slug);
            _siteConnections.RemoveFor(site.Slug);
        }
        // Re-enable needs no explicit start: the collection registry's reconcile
        // pass picks the site up within its cadence, and the next page view or
        // agent connect re-establishes the console connection.

        _changeNotifier.NotifySitesChanged();
        _logger.LogInformation("Site {Slug} {State}", site.Slug, enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Permanently removes a secondary site: stops its monitoring collection,
    /// drops its console connection, deletes its agents and registry row, and
    /// deletes its database directory. Irreversible.
    /// </summary>
    public async Task DeleteSiteAsync(Site site)
    {
        if (site.IsDefault)
            throw new InvalidOperationException("The default site cannot be removed.");

        // Disable first so the reconcile pass can't restart collection between
        // the stop below and the registry row disappearing.
        site.Enabled = false;
        await _siteRepository.UpdateAsync(site);
        await _collectionRegistry.StopForSiteAsync(site.Slug);
        _siteConnections.RemoveFor(site.Slug);

        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            await db.SiteAgents.Where(a => a.SiteId == site.Id).ExecuteDeleteAsync();
        }
        await _siteRepository.DeleteAsync(site.Id);

        // SQLite connection pooling keeps file handles open after the contexts are
        // disposed; clear the pools so the directory delete doesn't hit locked files.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            var dir = _dbPaths.GetSiteDataDir(site.Slug);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Site {Slug} was removed but its data directory could not be deleted; remove sites/{Slug} manually",
                site.Slug, site.Slug);
        }

        // Free the site's license seat.
        await _licenseState.RecomputeAsync();
        _changeNotifier.NotifySitesChanged();
        _logger.LogInformation("Removed site {Slug} (id {Id}) and its data", site.Slug, site.Id);
    }

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
                "Add a license key under Settings > Application > Licensing to unlock more sites.");

        var slug = await GenerateUniqueSlugAsync(name);
        var site = new Site { Slug = slug, Name = name.Trim() };

        await ProvisionSiteDatabaseAsync(slug);
        await _siteRepository.AddAsync(site);
        // Cover the new site with a spare seat if one is available, then recompute so the
        // Licensing card reflects the new site immediately (no manual license refresh needed).
        await _activation.AutoAssignAsync();
        await _licenseState.RecomputeAsync();
        _changeNotifier.NotifySitesChanged();
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

        // Seed the Alerts & Schedule defaults so a new site matches the main site instead
        // of showing blank lists (matches the startup seed for the main + existing sites).
        var existingPatterns = await db.AlertRules.Select(r => r.EventTypePattern).ToListAsync();
        var missingRules = NetworkOptimizer.Alerts.DefaultAlertRules.GetDefaults()
            .Where(r => !existingPatterns.Contains(r.EventTypePattern))
            .ToList();
        if (missingRules.Count > 0)
        {
            db.AlertRules.AddRange(missingRules);
            await db.SaveChangesAsync();
        }

        if (NetworkOptimizer.Core.FeatureFlags.SchedulingEnabled && !await db.ScheduledTasks.AnyAsync())
        {
            db.ScheduledTasks.Add(new NetworkOptimizer.Alerts.Models.ScheduledTask
            {
                TaskType = "audit",
                Name = "Security Audit",
                Enabled = true,
                FrequencyMinutes = 720, // 12 hours
                NextRunAt = NetworkOptimizer.Alerts.ScheduleService.CalculateNextRun(720),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        _logger.LogInformation("Provisioned site database for {Slug} at {Path}", slug, dbPath);
    }
}
