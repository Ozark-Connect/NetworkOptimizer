using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing sponsorship nag display with tiered messaging.
/// Shows progressively escalating quips based on usage, limited to one per day.
/// </summary>
public class SponsorshipService : ISponsorshipService
{
    private const string SponsorUrl = "https://github.com/sponsors/tvancott42";
    private const int SqmEnabledBonus = 5;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SponsorshipService> _logger;

    // Tiered quips with their corresponding action text
    // Order: friendly → self-deprecating → edgy → absurd
    private static readonly (string Quip, string ActionText)[] Tiers =
    [
        // Level 1: 1-2 uses - friendly intro
        ("The corgis say hi. They don't understand what GitHub Sponsors is either.", "Sponsor some treats"),

        // Level 2: 3-5 uses - self-deprecating
        ("You've run more audits than I've had hot meals this week.", "Buy me a hot meal"),

        // Level 3: 6-10 uses - relatable UI Store dig
        ("You paid $15 to ship a patch cable from the UI Store!? I'm just saying...", "Spare $5?"),

        // Level 4: 11-15 uses - getting personal
        ("At this point you've used this more than my wife talks to me. Sponsorship is cheaper than therapy.", "Fund my therapy"),

        // Level 5: 16-20 uses - earned the edge
        ("Still free. Still no VC funding. Still powered by coffee and spite.", "Fund the spite"),

        // Level 6: 21-30 uses - stats flex
        ("147,000 lines of code. 4,084 tests. One guy on 2 acres in Arkansas. Still cheaper than UI Ground shipping.", "Buy him lunch"),

        // Level 7: 31-40 uses - former employer dig
        ("You've used this more than some employers used my code. Just saying.", "Money me"),

        // Level 8: 41-50 uses - another UI Store dig
        ("A year of sponsorship costs less than shipping one sensor from the UI store. And I won't charge you $40 for Ground.", "Combine orders, PIF"),

        // Level 9: 51-75 uses - appreciative (for heavy users)
        ("Your Watchtower is working. I see you. I appreciate you.", "Power my homelab"),

        // Level 10: 76+ uses - we're family now
        ("We've been through a lot together. I expect you at Thanksgiving. Bring a side dish. And maybe sponsor me, idk.", "Become family"),
    ];

    // Usage thresholds for each level (upper bound, inclusive)
    // Level 1: 1-2, Level 2: 3-5, Level 3: 6-10, etc.
    private static readonly int[] LevelThresholds = [2, 5, 10, 15, 20, 30, 40, 50, 75, int.MaxValue];

    public SponsorshipService(IServiceProvider serviceProvider, ILogger<SponsorshipService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<SponsorshipNag?> GetCurrentNagAsync(bool alwaysShow = false)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();

            // Check if user has already marked themselves as a sponsor
            var alreadySponsorStr = await settingsService.GetAsync(SystemSettingKeys.SponsorshipAlreadySponsor);
            if (!string.IsNullOrEmpty(alreadySponsorStr) && alreadySponsorStr.Equals("true", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Get current state
            var lastShownLevelStr = await settingsService.GetAsync(SystemSettingKeys.SponsorshipLastShownLevel);
            var lastNagTimeStr = await settingsService.GetAsync(SystemSettingKeys.SponsorshipLastNagTime);

            var lastShownLevel = int.TryParse(lastShownLevelStr, out var level) ? level : 0;
            var lastNagTime = DateTime.TryParse(lastNagTimeStr, out var time) ? time : DateTime.MinValue;

            // Get earned level based on usage
            var earnedLevel = await GetEarnedLevelInternalAsync(scope);

            if (earnedLevel == 0)
            {
                // No usage yet
                return null;
            }

            var hoursSinceLastNag = (DateTime.UtcNow - lastNagTime).TotalHours;

            // Within 24h of last dismiss - stay hidden (unless alwaysShow for Settings preview)
            if (hoursSinceLastNag < 24 && lastShownLevel > 0 && !alwaysShow)
            {
                return null;
            }

            // Determine level to show (next level after last dismissed)
            var levelToShow = lastShownLevel + 1;

            // Check if we've earned this level
            if (levelToShow > earnedLevel)
            {
                if (!alwaysShow)
                {
                    return null; // All earned levels shown
                }
                levelToShow = earnedLevel; // For Settings preview
            }

            // Return the nag
            var tierIndex = Math.Clamp(levelToShow - 1, 0, Tiers.Length - 1);
            var tier = Tiers[tierIndex];

            return new SponsorshipNag(
                Level: levelToShow,
                Quip: tier.Quip,
                ActionText: tier.ActionText,
                ActionUrl: SponsorUrl
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sponsorship nag");
            return null;
        }
    }

    public async Task MarkLevelShownAsync(int level)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();

            // Save the shown level and timestamp
            await settingsService.SetAsync(SystemSettingKeys.SponsorshipLastShownLevel, level.ToString());
            await settingsService.SetAsync(SystemSettingKeys.SponsorshipLastNagTime, DateTime.UtcNow.ToString("O"));

            _logger.LogDebug("Marked sponsorship nag level {Level} as shown", level);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking sponsorship nag level as shown");
        }
    }

    public async Task<int> GetUsageCountAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        return await GetUsageCountInternalAsync(scope);
    }

    public async Task<int> GetEarnedLevelAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        return await GetEarnedLevelInternalAsync(scope);
    }

    private async Task<int> GetUsageCountInternalAsync(IServiceScope scope)
    {
        var auditRepository = scope.ServiceProvider.GetRequiredService<IAuditRepository>();
        var speedTestRepository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NetworkOptimizerDbContext>>();

        // Count all usage sources in parallel
        var auditCountTask = auditRepository.GetAuditCountAsync();
        var speedTestCountTask = speedTestRepository.GetIperf3ResultCountAsync();
        var sqmWan1Task = speedTestRepository.GetSqmWanConfigAsync(1);
        var sqmWan2Task = speedTestRepository.GetSqmWanConfigAsync(2);

        // Floor plan feature counts via DbContext
        Task<int> signalLogCountTask;
        Task<int> placedApCountTask;
        Task<int> plannedApCountTask;
        Task<int> floorCountTask;
        using (var db = await dbFactory.CreateDbContextAsync())
        {
            signalLogCountTask = db.ClientSignalLogs.CountAsync();
            placedApCountTask = db.ApLocations.CountAsync();
            plannedApCountTask = db.PlannedAps.CountAsync();
            floorCountTask = db.FloorPlans.CountAsync();

            await Task.WhenAll(auditCountTask, speedTestCountTask, sqmWan1Task, sqmWan2Task,
                signalLogCountTask, placedApCountTask, plannedApCountTask, floorCountTask);
        }

        // Audits count as 1, speed tests count as 0.5 (integer division)
        var count = auditCountTask.Result + (speedTestCountTask.Result / 2);

        // 50 signal points = 1 audit equivalent
        count += signalLogCountTask.Result / 50;

        // 2 placed APs (real + planned) = 1 audit equivalent
        count += (placedApCountTask.Result + plannedApCountTask.Result) / 2;

        // 2 building-floors = 1 audit equivalent
        count += floorCountTask.Result / 2;

        // Add SQM bonus if enabled on either WAN
        var sqmEnabled = sqmWan1Task.Result?.Enabled == true || sqmWan2Task.Result?.Enabled == true;
        if (sqmEnabled)
        {
            count += SqmEnabledBonus;
        }

        return count;
    }

    private async Task<int> GetEarnedLevelInternalAsync(IServiceScope scope)
    {
        var usageCount = await GetUsageCountInternalAsync(scope);

        if (usageCount == 0)
        {
            return 0;
        }

        // Find the earned level based on usage thresholds
        for (var i = 0; i < LevelThresholds.Length; i++)
        {
            if (usageCount <= LevelThresholds[i])
            {
                return i + 1; // Levels are 1-indexed
            }
        }

        return Tiers.Length; // Max level
    }

    public async Task MarkAsAlreadySponsorAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            await settingsService.SetAsync(SystemSettingKeys.SponsorshipAlreadySponsor, "true");
            _logger.LogInformation("User marked as already a sponsor - nags permanently dismissed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking user as already sponsor");
        }
    }

    public async Task<bool> IsAlreadySponsorAsync()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            var value = await settingsService.GetAsync(SystemSettingKeys.SponsorshipAlreadySponsor);
            return !string.IsNullOrEmpty(value) && value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking if user is already sponsor");
            return false;
        }
    }
}
