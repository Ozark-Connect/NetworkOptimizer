using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public sealed class WanSteerRuleService : IWanSteerRuleService
{
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<WanSteerRuleService> _logger;

    public WanSteerRuleService(
        SiteDbContextFactory siteDb,
        SiteContextService siteContext,
        ILogger<WanSteerRuleService> logger)
    {
        _siteDb = siteDb;
        _siteContext = siteContext;
        _logger = logger;
    }

    private NetworkOptimizerDbContext ForCurrentSite()
        => _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <inheritdoc />
    public async Task<List<WanSteerTrafficClass>> ListAsync()
    {
        await using var db = ForCurrentSite();
        return await db.WanSteerTrafficClasses
            .OrderBy(r => r.SortOrder)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task SaveAsync(WanSteerTrafficClass rule)
    {
        await using var db = ForCurrentSite();

        if (rule.Id == 0)
        {
            // Appended to the end of the evaluation order. Counted in the database rather than taken
            // from the page's list, so a stale view cannot collide two rules onto one position.
            rule.SortOrder = await db.WanSteerTrafficClasses.CountAsync();
            db.WanSteerTrafficClasses.Add(rule);
        }
        else
        {
            var existing = await db.WanSteerTrafficClasses.FindAsync(rule.Id);
            if (existing is null)
                return;

            existing.Name = rule.Name;
            existing.Enabled = rule.Enabled;
            existing.Probability = rule.Probability;
            existing.TargetWanKey = rule.TargetWanKey;
            existing.SrcCidrsJson = rule.SrcCidrsJson;
            existing.SrcMacsJson = rule.SrcMacsJson;
            existing.DstCidrsJson = rule.DstCidrsJson;
            existing.Protocol = rule.Protocol;
            existing.SrcPortsJson = rule.SrcPortsJson;
            existing.DstPortsJson = rule.DstPortsJson;
        }

        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task DeleteAsync(int ruleId)
    {
        await using var db = ForCurrentSite();
        var entity = await db.WanSteerTrafficClasses.FindAsync(ruleId);
        if (entity is null)
            return;

        db.WanSteerTrafficClasses.Remove(entity);
        await db.SaveChangesAsync();

        // Close the gap: evaluation order is positional, so leaving a hole would be harmless today and
        // confusing the moment anything reasons about the numbers.
        var remaining = await db.WanSteerTrafficClasses.OrderBy(r => r.SortOrder).ToListAsync();
        for (var i = 0; i < remaining.Count; i++)
            remaining[i].SortOrder = i;
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(int ruleId, bool enabled)
    {
        await using var db = ForCurrentSite();
        var entity = await db.WanSteerTrafficClasses.FindAsync(ruleId);
        if (entity is null)
            return;

        entity.Enabled = enabled;
        await db.SaveChangesAsync();
    }

    /// <inheritdoc />
    public async Task SwapSortOrderAsync(int firstRuleId, int secondRuleId)
    {
        await using var db = ForCurrentSite();
        var first = await db.WanSteerTrafficClasses.FindAsync(firstRuleId);
        var second = await db.WanSteerTrafficClasses.FindAsync(secondRuleId);
        if (first is null || second is null)
            return;

        (first.SortOrder, second.SortOrder) = (second.SortOrder, first.SortOrder);
        await db.SaveChangesAsync();
    }
}
