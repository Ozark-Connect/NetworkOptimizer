using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for the site registry in the main database.
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

    /// <inheritdoc />
    public async Task<List<Site>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Sites
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        return await _context.Sites.FirstOrDefaultAsync(s => s.Slug == slug, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Sites.FirstOrDefaultAsync(s => s.IsDefault, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site> AddAsync(Site site, CancellationToken cancellationToken = default)
    {
        site.CreatedAt = DateTime.UtcNow;
        site.UpdatedAt = DateTime.UtcNow;
        _context.Sites.Add(site);
        await _context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Added site {Slug} ({Name})", site.Slug, site.Name);
        return site;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Site site, CancellationToken cancellationToken = default)
    {
        var existing = await _context.Sites.FindAsync(new object[] { site.Id }, cancellationToken)
            ?? throw new InvalidOperationException($"Site {site.Id} not found");

        existing.Name = site.Name;
        existing.Enabled = site.Enabled;
        existing.SortOrder = site.SortOrder;
        existing.Notes = site.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
