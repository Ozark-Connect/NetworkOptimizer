using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for the site registry. Uses the main-database context factory
/// (never the scoped, site-routed context): the registry is instance-wide and
/// lives only in the main database, regardless of which site a scope targets.
/// </summary>
public class SiteRepository : ISiteRepository
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _contextFactory;
    private readonly ILogger<SiteRepository> _logger;

    public SiteRepository(IDbContextFactory<NetworkOptimizerDbContext> contextFactory, ILogger<SiteRepository> logger)
    {
        _contextFactory = contextFactory ?? throw new ArgumentNullException(nameof(contextFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<List<Site>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Sites
            .OrderBy(s => s.SortOrder)
            .ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site?> GetBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Sites.FirstOrDefaultAsync(s => s.Slug == slug, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site?> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Sites.FirstOrDefaultAsync(s => s.IsDefault, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Site> AddAsync(Site site, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        site.CreatedAt = DateTime.UtcNow;
        site.UpdatedAt = DateTime.UtcNow;
        context.Sites.Add(site);
        await context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Added site {Slug} ({Name})", site.Slug, site.Name);
        return site;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(Site site, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Sites.FindAsync(new object[] { site.Id }, cancellationToken)
            ?? throw new InvalidOperationException($"Site {site.Id} not found");

        existing.Name = site.Name;
        existing.Enabled = site.Enabled;
        existing.SortOrder = site.SortOrder;
        existing.Notes = site.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await context.Sites.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null)
            return;

        context.Sites.Remove(existing);
        await context.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Deleted site {Slug} (id {Id}) from the registry", existing.Slug, id);
    }
}
