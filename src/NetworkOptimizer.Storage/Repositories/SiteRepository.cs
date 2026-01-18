using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for site management
/// </summary>
public class SiteRepository : ISiteRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<SiteRepository> _logger;

    public SiteRepository(NetworkOptimizerDbContext context, ILogger<SiteRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get a site by ID.
    /// </summary>
    public async Task<Site?> GetSiteAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Sites
                .AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Get all sites, optionally including disabled sites.
    /// </summary>
    public async Task<List<Site>> GetAllSitesAsync(bool includeDisabled = false, CancellationToken cancellationToken = default)
    {
        try
        {
            var query = _context.Sites.AsNoTracking();

            if (!includeDisabled)
            {
                query = query.Where(s => s.Enabled);
            }

            return await query
                .OrderBy(s => s.SortOrder)
                .ThenBy(s => s.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get all sites");
            throw;
        }
    }

    /// <summary>
    /// Get count of sites.
    /// </summary>
    public async Task<int> GetSiteCountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Sites
                .Where(s => s.Enabled)
                .CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get site count");
            throw;
        }
    }

    /// <summary>
    /// Create a new site and return its ID.
    /// </summary>
    public async Task<int> CreateSiteAsync(Site site, CancellationToken cancellationToken = default)
    {
        try
        {
            site.CreatedAt = DateTime.UtcNow;
            site.UpdatedAt = DateTime.UtcNow;

            _context.Sites.Add(site);
            await _context.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("Created site {SiteId} '{SiteName}'", site.Id, site.Name);
            return site.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create site '{SiteName}'", site.Name);
            throw;
        }
    }

    /// <summary>
    /// Update an existing site.
    /// </summary>
    public async Task UpdateSiteAsync(Site site, CancellationToken cancellationToken = default)
    {
        try
        {
            var existing = await _context.Sites.FindAsync(new object[] { site.Id }, cancellationToken);
            if (existing == null)
            {
                throw new InvalidOperationException($"Site {site.Id} not found");
            }

            existing.Name = site.Name;
            existing.DisplayName = site.DisplayName;
            existing.Enabled = site.Enabled;
            existing.SortOrder = site.SortOrder;
            existing.Notes = site.Notes;
            existing.UpdatedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated site {SiteId} '{SiteName}'", site.Id, site.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update site {SiteId}", site.Id);
            throw;
        }
    }

    /// <summary>
    /// Delete a site and all its related data (cascade delete configured in DbContext).
    /// </summary>
    public async Task DeleteSiteAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            var site = await _context.Sites.FindAsync(new object[] { siteId }, cancellationToken);
            if (site != null)
            {
                _context.Sites.Remove(site);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted site {SiteId} '{SiteName}'", siteId, site.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete site {SiteId}", siteId);
            throw;
        }
    }

    /// <summary>
    /// Get a site with its connection settings loaded.
    /// </summary>
    public async Task<Site?> GetSiteWithConnectionSettingsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Sites
                .AsNoTracking()
                .Include(s => s.ConnectionSettings)
                .FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get site {SiteId} with connection settings", siteId);
            throw;
        }
    }

    /// <summary>
    /// Get a site with all settings loaded (connection, SSH, gateway).
    /// </summary>
    public async Task<Site?> GetSiteWithAllSettingsAsync(int siteId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.Sites
                .AsNoTracking()
                .Include(s => s.ConnectionSettings)
                .Include(s => s.UniFiSshSettings)
                .Include(s => s.GatewaySshSettings)
                .FirstOrDefaultAsync(s => s.Id == siteId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get site {SiteId} with all settings", siteId);
            throw;
        }
    }
}
