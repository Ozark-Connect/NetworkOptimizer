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
    /// Enables specific disabled rules (by EventTypePattern) when a capability first
    /// becomes available - e.g. attaching augmented PON polling to an SFP ONT unlocks the
    /// BIP/HEC error alerts. Skips if any of those patterns is already enabled, so it never
    /// re-enables a rule the user turned off. Complements the startup
    /// <see cref="EnableFreshlySeeded"/> path by acting immediately on the triggering save.
    /// </summary>
    public static async Task EnablePatternsAsync(IServiceScope scope, IReadOnlyCollection<string> patterns, ILogger logger)
    {
        try
        {
            var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();

            var rules = (await db.AlertRules.ToListAsync())
                .Where(r => patterns.Contains(r.EventTypePattern))
                .ToList();
            if (rules.Count == 0 || rules.Any(r => r.IsEnabled)) return;

            foreach (var rule in rules) rule.IsEnabled = true;
            await db.SaveChangesAsync();

            logger.LogInformation("Auto-enabled {Count} alert rules for patterns [{Patterns}]",
                rules.Count, string.Join(", ", patterns));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to auto-enable pattern rules");
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
    /// <summary>
    /// Enables ONE named rule that shipped disabled only because nothing ever published its event,
    /// at the moment the release that gives it a publisher lands. Its disabled state carried no
    /// user intent: the rule could not fire, so nobody chose to silence it.
    ///
    /// The trigger is a paired new rule arriving in the same seed pass, which happens exactly once
    /// per database - so this runs once and never again. A user who disables the rule afterwards is
    /// never overridden, and no other rule is touched.
    /// </summary>
    /// <param name="db">Database whose rules to update (main or a site's).</param>
    /// <param name="pattern">The single EventTypePattern to enable.</param>
    /// <param name="pairedNewPattern">Pattern whose fresh insertion marks the upgrade moment.</param>
    /// <param name="seededPatterns">Patterns inserted by this seed pass.</param>
    /// <param name="logger">Logger.</param>
    public static void EnableNowThatItHasAPublisher(
        NetworkOptimizerDbContext db,
        string pattern,
        string pairedNewPattern,
        ISet<string> seededPatterns,
        ILogger logger)
    {
        if (!seededPatterns.Contains(pairedNewPattern)) return;

        var rule = db.AlertRules.FirstOrDefault(r => r.EventTypePattern == pattern);
        if (rule == null || rule.IsEnabled) return;

        rule.IsEnabled = true;
        db.SaveChanges();

        logger.LogInformation(
            "Enabled the '{Name}' alert rule: it shipped disabled while nothing published {Pattern}, which now has a publisher",
            rule.Name, pattern);
    }

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
