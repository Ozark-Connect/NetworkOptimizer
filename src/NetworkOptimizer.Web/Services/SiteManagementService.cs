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
public class SiteManagementService : ISiteManagementService
{
    /// <summary>Slug reserved for the default site (the pre-multi-site instance).</summary>
    public const string DefaultSiteSlug = "main";

    private readonly ISiteRepository _siteRepository;
    private readonly Authorization.IEffectiveSiteRoleResolver _siteRoles;
    private readonly AgentTunnelRegistry _tunnelRegistry;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDbFactory;
    private readonly IDbContextFactory<NetworkOptimizer.Storage.Models.Identity.AuthDbContext> _authDbFactory;
    private readonly SiteDatabasePaths _dbPaths;
    private readonly Licensing.LicenseStateService _licenseState;
    private readonly Licensing.LicenseActivationService _activation;
    private readonly SiteConnectionRegistry _siteConnections;
    private readonly IEnumerable<ISiteScopedRegistry> _siteRegistries;
    private readonly MonitoringCollectionRegistry _collectionRegistry;
    private readonly SiteRegistryChangeNotifier _changeNotifier;
    private readonly ILogger<SiteManagementService> _logger;
    private readonly Authorization.ISiteAccessFilter _siteAccess;

    public SiteManagementService(
        ISiteRepository siteRepository,
        Authorization.IEffectiveSiteRoleResolver siteRoles,
        AgentTunnelRegistry tunnelRegistry,
        IDbContextFactory<NetworkOptimizerDbContext> mainDbFactory,
        IDbContextFactory<NetworkOptimizer.Storage.Models.Identity.AuthDbContext> authDbFactory,
        SiteDatabasePaths dbPaths,
        Licensing.LicenseStateService licenseState,
        Licensing.LicenseActivationService activation,
        SiteConnectionRegistry siteConnections,
        IEnumerable<ISiteScopedRegistry> siteRegistries,
        MonitoringCollectionRegistry collectionRegistry,
        SiteRegistryChangeNotifier changeNotifier,
        Authorization.ISiteAccessFilter siteAccess,
        ILogger<SiteManagementService> logger)
    {
        _siteAccess = siteAccess;
        _siteRepository = siteRepository;
        _siteRoles = siteRoles;
        _tunnelRegistry = tunnelRegistry;
        _mainDbFactory = mainDbFactory;
        _authDbFactory = authDbFactory;
        _dbPaths = dbPaths;
        _licenseState = licenseState;
        _activation = activation;
        _siteConnections = siteConnections;
        _siteRegistries = siteRegistries;
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
        _siteRoles.InvalidateAll();
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
        // Instance-wide licensing arithmetic: counts every site, not the caller's slice.
        var count = (await GetAllSitesUnfilteredAsync()).Count;
        return Math.Max(0, limit - count);
    }

    /// <summary>
    /// Gets the registered sites the CALLER may see, with the default (main) site always first.
    ///
    /// Narrowing happens here rather than at the call sites deliberately. Component-level filtering
    /// is not a boundary - a live circuit can call the service directly - and doing it per caller
    /// means every future caller re-decides it. The filter returns everything for system scopes,
    /// auth-disabled installs, and any scope with no caller, so background fan-out and single-admin
    /// installs are unaffected.
    /// </summary>
    public async Task<List<Site>> GetSitesAsync()
    {
        var sites = await GetAllSitesUnfilteredAsync();
        return await _siteAccess.FilterAsync(sites, s => s.Slug);
    }

    /// <summary>
    /// Every registered site regardless of caller. For instance-level questions - licensing counts,
    /// registry maintenance - where narrowing to one caller's view would give the wrong answer.
    /// </summary>
    private async Task<List<Site>> GetAllSitesUnfilteredAsync()
    {
        var sites = await _siteRepository.GetAllAsync();
        return sites.OrderByDescending(s => s.IsDefault).ToList();
    }

    /// <summary>Updates a site's mutable fields (name, enabled, sort order, notes).</summary>
    public async Task UpdateSiteAsync(Site site)
    {
        await _siteRepository.UpdateAsync(site);
        _siteRoles.InvalidateAll();
        _changeNotifier.NotifySitesChanged();
    }

    /// <summary>
    /// Closes any agent tunnel still open for a site that is going away or going quiet. An agent
    /// streams results until something stops it, and removing a site deletes the database those
    /// results are written to - so without this the next batch lands on a file that no longer
    /// exists, and the failure escapes the tunnel handler as an unhandled error rather than a close.
    /// </summary>
    private void DropTunnels(string slug, string reason)
    {
        foreach (var connection in _tunnelRegistry.GetForSite(slug))
        {
            _logger.LogInformation("Dropping tunnel for agent {Agent} on site {Slug}: {Reason}",
                connection.AgentName, slug, reason);
            connection.Drop();
        }
    }

    /// <inheritdoc />
    public async Task RenameSiteAsync(string siteSlug, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
            return;

        var sites = await _siteRepository.GetAllAsync();
        var site = sites.FirstOrDefault(s => string.Equals(s.Slug, siteSlug, StringComparison.OrdinalIgnoreCase));
        if (site is null)
            return;

        // The slug is what the caller was authorized against, so the row is looked up by slug here
        // rather than taken from the caller - a Site object off the page could name a different one.
        site.Name = trimmed;
        await _siteRepository.UpdateAsync(site);
        _siteRoles.InvalidateAll();
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
            DropTunnels(site.Slug, "site disabled");
        }
        // Re-enable needs no explicit start: the collection registry's reconcile
        // pass picks the site up within its cadence, and the next page view or
        // agent connect re-establishes the console connection.

        _siteRoles.InvalidateAll();
        _changeNotifier.NotifySitesChanged();
        _logger.LogInformation("Site {Slug} {State}", site.Slug, enabled ? "enabled" : "disabled");
    }

    /// <summary>
    /// Permanently removes a secondary site: stops its monitoring collection,
    /// drops its console connection, deletes its agents and registry row, and
    /// deletes its database directory. Irreversible.
    /// </summary>
    /// <summary>
    /// Drops everything that grants access to a slug, because the slug is all any of it stores. A slug
    /// is derived from the site name and only made unique against sites that EXIST, so deleting a site
    /// and later creating one by the same name reuses the slug - and every stale grant would come back
    /// to life on a site the operator believes is new. Removal is a revocation; it has to stick.
    ///
    /// Covers direct memberships, the site's place in any group, and the federation mappings that would
    /// re-grant it at the next SSO login.
    /// </summary>
    private async Task RemoveAccessGrantsAsync(string slug)
    {
        await using var auth = await _authDbFactory.CreateDbContextAsync();

        var memberships = await auth.SiteMemberships
            .Where(m => m.TargetType == NetworkOptimizer.Storage.Models.Identity.MembershipTargetType.Site
                && m.TargetId == slug)
            .ExecuteDeleteAsync();

        var groupRows = await auth.SiteGroupMembers
            .Where(g => g.SiteSlug == slug)
            .ExecuteDeleteAsync();

        var mappings = await auth.FederationSiteMappings
            .Where(m => m.TargetType == NetworkOptimizer.Storage.Models.Identity.MembershipTargetType.Site
                && m.TargetValue == slug)
            .ExecuteDeleteAsync();

        if (memberships + groupRows + mappings > 0)
        {
            _logger.LogInformation(
                "Removed access to site {Slug}: {Memberships} membership(s), {GroupRows} group entry(ies), "
                + "{Mappings} federation mapping(s)",
                slug, memberships, groupRows, mappings);
        }
    }

    public async Task DeleteSiteAsync(Site site)
    {
        if (site.IsDefault)
            throw new InvalidOperationException("The default site cannot be removed.");

        // Disable first so the reconcile pass can't restart collection between
        // the stop below and the registry row disappearing.
        site.Enabled = false;
        await _siteRepository.UpdateAsync(site);
        await _collectionRegistry.StopForSiteAsync(site.Slug);
        await SweepSiteRegistriesAsync(site.Slug);
        DropTunnels(site.Slug, "site removed");

        await using (var db = await _mainDbFactory.CreateDbContextAsync())
        {
            await db.SiteAgents.Where(a => a.SiteId == site.Id).ExecuteDeleteAsync();
        }
        await RemoveAccessGrantsAsync(site.Slug);
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
        _siteRoles.InvalidateAll();
        _changeNotifier.NotifySitesChanged();
        _logger.LogInformation("Removed site {Slug} (id {Id}) and its data", site.Slug, site.Id);
    }

    /// <summary>
    /// Empties every per-site registry of a site being removed.
    ///
    /// Two passes, because the instances hold one another: the tracer holds the site's console
    /// connection, the ISP Health service holds its InfluxDB client. Evicting and disposing in one
    /// pass hands a live holder a disposed dependency - which is how a re-created site ended up
    /// with an ObjectDisposedException on every ISP Health pass and a discovery run that saw an
    /// empty device list. So: evict everything first, then tear down.
    ///
    /// Loop owners are torn down before the rest so a running collection pass cannot reach a
    /// client that has just been disposed.
    /// </summary>
    private async Task SweepSiteRegistriesAsync(string slug)
    {
        var loopTeardowns = new List<Func<ValueTask>>();
        var teardowns = new List<Func<ValueTask>>();

        foreach (var registry in _siteRegistries)
        {
            try
            {
                var teardown = registry.EvictSite(slug);
                if (teardown == null) continue;
                (registry is BackgroundService ? loopTeardowns : teardowns).Add(teardown);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not evict site {Slug} from {Registry}", slug, registry.GetType().Name);
            }
        }

        foreach (var teardown in loopTeardowns.Concat(teardowns))
        {
            try
            {
                await teardown();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Teardown for removed site {Slug} failed", slug);
            }
        }
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
        _siteRoles.InvalidateAll();
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
