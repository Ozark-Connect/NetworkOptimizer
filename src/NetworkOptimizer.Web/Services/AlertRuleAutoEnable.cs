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
}
