using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Services;

/// <summary>
/// Creates DbContexts for an explicit site, for callers outside the ambient
/// site context: cross-origin result posts carrying a site slug, background
/// fan-out over sites, and provisioning/migration.
/// </summary>
public class SiteDbContextFactory
{
    private readonly SiteDatabasePaths _paths;

    public SiteDbContextFactory(SiteDatabasePaths paths)
    {
        _paths = paths;
    }

    /// <summary>Whether a non-default site's database has been provisioned.</summary>
    public bool SiteDbExists(string slug) => File.Exists(_paths.GetSiteDbPath(slug, isDefault: false));

    /// <summary>Creates a context bound to the given site's database. Caller disposes.</summary>
    public NetworkOptimizerDbContext CreateForSite(string slug, bool isDefault = false)
    {
        var options = new DbContextOptionsBuilder<NetworkOptimizerDbContext>()
            .UseSqlite($"Data Source={_paths.GetSiteDbPath(slug, isDefault)}")
            .Options;
        return new NetworkOptimizerDbContext(options);
    }
}
