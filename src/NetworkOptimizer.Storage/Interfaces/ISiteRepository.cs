using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Repository for site management
/// </summary>
public interface ISiteRepository
{
    /// <summary>Get a site by ID</summary>
    Task<Site?> GetSiteAsync(int siteId, CancellationToken cancellationToken = default);

    /// <summary>Get all sites</summary>
    Task<List<Site>> GetAllSitesAsync(bool includeDisabled = false, CancellationToken cancellationToken = default);

    /// <summary>Get count of sites</summary>
    Task<int> GetSiteCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Create a new site and return its ID</summary>
    Task<int> CreateSiteAsync(Site site, CancellationToken cancellationToken = default);

    /// <summary>Update an existing site</summary>
    Task UpdateSiteAsync(Site site, CancellationToken cancellationToken = default);

    /// <summary>Delete a site and all its related data (cascade)</summary>
    Task DeleteSiteAsync(int siteId, CancellationToken cancellationToken = default);

    /// <summary>Get a site with its connection settings loaded</summary>
    Task<Site?> GetSiteWithConnectionSettingsAsync(int siteId, CancellationToken cancellationToken = default);

    /// <summary>Get a site with all settings loaded (connection, SSH, gateway)</summary>
    Task<Site?> GetSiteWithAllSettingsAsync(int siteId, CancellationToken cancellationToken = default);
}
