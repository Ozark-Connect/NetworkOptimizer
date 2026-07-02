using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for the site registry. The registry lives only in the main
/// (default site) database; per-site data lives in each site's own database file.
/// </summary>
public interface ISiteRepository
{
    /// <summary>Gets all sites ordered by sort order, then name.</summary>
    Task<List<Site>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets a site by its immutable slug, or null if not found.</summary>
    Task<Site?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default);

    /// <summary>Gets the default site (the pre-multi-site instance), or null if none is marked.</summary>
    Task<Site?> GetDefaultAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds a new site to the registry.</summary>
    Task<Site> AddAsync(Site site, CancellationToken cancellationToken = default);

    /// <summary>Updates a site's mutable fields (name, enabled, sort order, notes). The slug is never changed.</summary>
    Task UpdateAsync(Site site, CancellationToken cancellationToken = default);
}
