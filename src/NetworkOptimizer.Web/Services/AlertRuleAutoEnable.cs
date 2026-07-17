using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Enables disabled alert rules by source when a user configures their first
/// monitoring target of that type. Skips if the user already has enabled rules
/// for that source (meaning they've already interacted with them).
/// </summary>
public static class AlertRuleAutoEnable
{
    public static async Task EnableBySourceAsync(IServiceScope scope, string source, ILogger logger)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            var anyEnabled = await db.AlertRules
                .AnyAsync(r => r.Source == source && r.IsEnabled);
            if (anyEnabled) return;

            var disabled = await db.AlertRules
                .Where(r => r.Source == source && !r.IsEnabled)
                .ToListAsync();

            if (disabled.Count == 0) return;

            foreach (var rule in disabled) rule.IsEnabled = true;
            await db.SaveChangesAsync();

            logger.LogInformation("Auto-enabled {Count} {Source} alert rules", disabled.Count, source);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to auto-enable {Source} alert rules", source);
        }
    }

    /// <summary>
    /// Enables rules that were JUST seeded (their EventTypePattern is in
    /// <paramref name="seededPatterns"/>) for a source whose monitoring is already configured
    /// on this database. Unlike <see cref="EnableBySourceAsync"/> this only touches the
    /// freshly-inserted rules, so it never re-enables a rule the user turned off. It closes the
    /// gap where adding a new default rule to an already-active source - e.g. a new ONT alert on
    /// a site that already monitors ONTs - would otherwise land disabled and silently miss
    /// coverage its sibling rules provide. <paramref name="hasConfigs"/> is evaluated only when
    /// there is something to enable.
    /// </summary>
    public static void EnableFreshlySeeded(
        NetworkOptimizerDbContext db, string source, ISet<string> seededPatterns, Func<bool> hasConfigs)
    {
        var freshlySeeded = db.AlertRules
            .Where(r => r.Source == source && !r.IsEnabled)
            .ToList()
            .Where(r => seededPatterns.Contains(r.EventTypePattern))
            .ToList();
        if (freshlySeeded.Count > 0 && hasConfigs())
        {
            foreach (var rule in freshlySeeded) rule.IsEnabled = true;
            db.SaveChanges();
        }
    }
}
