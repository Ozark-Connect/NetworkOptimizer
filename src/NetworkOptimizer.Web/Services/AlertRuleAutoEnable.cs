using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Enables disabled alert rules by source when a user configures their first
/// monitoring target of that type. Called from each modem service's save path.
/// </summary>
public static class AlertRuleAutoEnable
{
    public static async Task EnableBySourceAsync(IServiceScope scope, string source, ILogger logger)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
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
