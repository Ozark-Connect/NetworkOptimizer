using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing sponsorship nag display with tiered messaging.
/// Shows progressively escalating quips based on usage, limited to one per day.
/// </summary>
public class SponsorshipService : ISponsorshipService
{
    private const string GitHubSponsorUrl = "https://github.com/sponsors/tvancott42";
    private const string KofiUrl = "https://ko-fi.com/tjtuna42";
    private const int SqmEnabledBonus = 3;
    private const int MonitoringEnabledBonus = 3;
    private const int MonitoringTargetsDivisor = 5;

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SponsorshipService> _logger;

    // Earned level is derived from a ~11-query usage-count fan-out that's expensive relative to how
    // often the banner asks for it (re-runs on every page load / nav). Usage barely changes minute to
    // minute, so cache the computed level briefly. Guarded by a lock rather than Interlocked because
    // the timestamp is a DateTime (Interlocked doesn't support it).
    private static readonly TimeSpan EarnedLevelCacheTtl = TimeSpan.FromMinutes(5);
    private readonly object _earnedLevelCacheLock = new();
    private int _cachedEarnedLevel;
    private DateTime _earnedLevelCachedAtUtc = DateTime.MinValue;

    // Tiered quips with their corresponding action text
    // Order: friendly → self-deprecating → edgy → absurd
    private static readonly (string Quip, string ActionText)[] Tiers =
    [
        // Level 1: 1-3 uses - friendly intro
        ("The corgis say hi. They don't understand what GitHub Sponsors is either.", "Send some treats"),

        // Level 2: 4-7 uses - self-deprecating
        ("You've run more audits than I've had hot meals this week.", "Buy me a hot meal"),

        // Level 3: 8-15 uses - relatable UI Store dig
        ("You paid $15 to ship a patch cable from the UI Store!? I'm just saying...", "Spare $5?"),

        // Level 4: 16-20 uses - getting personal
        ("At this point you've used this more than my wife talks to me. Sponsorship is cheaper than therapy.", "Fund my therapy"),

        // Level 5: 21-30 uses - earned the edge
        ("Still free. Still no VC funding. Still powered by coffee and spite.", "Fund the spite"),

        // Level 6: 31-40 uses - stats flex
        ("217,000 lines of code. 6,100 tests. One guy on 2 acres in Arkansas. Still cheaper than UI Ground shipping.", "Buy him lunch"),

        // Level 7: 41-50 uses - former employer dig
        ("You've used this more than some employers used my code. Just saying.", "Money me"),

        // Level 8: 51-75 uses - another UI Store dig
        ("A year of sponsorship costs less than shipping one sensor from the UI store. And I won't charge you $40 for Ground.", "Combine orders, PIF"),

        // Level 9: 76-100 uses - appreciative (for heavy users)
        ("Your Watchtower is working. I see you. I appreciate you.", "Power my homelab"),

        // Level 10: 101+ uses - we're family now
        ("We've been through a lot together. I expect you at Thanksgiving. Bring a side dish. And maybe sponsor me, idk.", "Become family"),
    ];

    // Usage thresholds for each level (upper bound, inclusive)
    // Level 1: 1-3, Level 2: 4-7, Level 3: 8-15, etc.
    private static readonly int[] LevelThresholds = [3, 7, 15, 20, 30, 40, 50, 75, 100, int.MaxValue];

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

            var hoursSinceLastNag = (DateTime.UtcNow - lastNagTime).TotalHours;

            // Within 48h of last dismiss - stay hidden (unless alwaysShow for Settings preview).
            // Checked before computing the earned level so the common "nagged recently" path skips
            // the expensive usage-count query entirely - the banner re-runs on every page load.
            if (hoursSinceLastNag < 48 && lastShownLevel > 0 && !alwaysShow)
            {
                return null;
            }

            // Get earned level based on usage
            var earnedLevel = await GetEarnedLevelInternalAsync(scope);

            if (earnedLevel == 0)
            {
                // No usage yet
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
                GitHubSponsorUrl: GitHubSponsorUrl,
                KofiUrl: KofiUrl
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

        // Count all usage sources. These run sequentially rather than in parallel: the repositories
        // share a single scoped DbContext (and the factory context below is a single instance too),
        // and a DbContext does not support concurrent operations regardless of the SQLite journal
        // mode. Fanning these out with Task.WhenAll throws "A second operation was started on this
        // context instance" (or ObjectDisposedException when a disposal path wins the race). WAL would
        // permit truly concurrent reads, but only across separate connections - i.e. a distinct
        // DbContext per query - which isn't worth it for a handful of cheap COUNT queries on a ~60s nag
        // check.
        var manualAuditCount = await auditRepository.GetManualAuditCountAsync();
        var scheduledAuditCount = await auditRepository.GetScheduledAuditCountAsync();
        var speedTestCount = await speedTestRepository.GetIperf3ResultCountAsync();
        var sqmWan1 = await speedTestRepository.GetSqmWanConfigAsync(1);
        var sqmWan2 = await speedTestRepository.GetSqmWanConfigAsync(2);

        // Floor plan feature counts, perf tweaks, and monitoring via a short-lived DbContext
        int signalLogCount;
        int placedApCount;
        int plannedApCount;
        int floorCount;
        int perfTweakCount;
        int monitoringTargetCount;
        using (var db = await dbFactory.CreateDbContextAsync())
        {
            signalLogCount = await db.ClientSignalLogs.CountAsync();
            placedApCount = await db.ApLocations.CountAsync();
            plannedApCount = await db.PlannedAps.CountAsync();
            floorCount = await db.FloorPlans.CountAsync();
            perfTweakCount = await db.PerfTweakSettings.CountAsync();
            monitoringTargetCount = await db.MonitoringTargets.Where(t => t.Enabled).CountAsync();
        }

        // Manual audits count as 1, scheduled audits count as 0.2 (~2 per workweek), speed tests count as 0.5
        var count = manualAuditCount + (scheduledAuditCount / 5) + (speedTestCount / 2);

        // 50 signal points = 1 audit equivalent
        count += signalLogCount / 50;

        // 2 placed APs (real + planned) = 1 audit equivalent
        count += (placedApCount + plannedApCount) / 2;

        // 2 building-floors = 1 audit equivalent
        count += floorCount / 2;

        // Add SQM bonus if enabled on either WAN
        var sqmEnabled = sqmWan1?.Enabled == true || sqmWan2?.Enabled == true;
        if (sqmEnabled)
        {
            count += SqmEnabledBonus;
        }

        // 2 points per deployed performance tweak
        count += perfTweakCount * 2;

        // Monitoring: flat bonus if InfluxDB is connected, plus 1 per 5 enabled targets
        var influxClient = scope.ServiceProvider.GetRequiredService<MonitoringInfluxClient>();
        if (influxClient.IsConfigured)
        {
            count += MonitoringEnabledBonus;
        }
        count += monitoringTargetCount / MonitoringTargetsDivisor;

        return count;
    }

    private async Task<int> GetEarnedLevelInternalAsync(IServiceScope scope)
    {
        lock (_earnedLevelCacheLock)
        {
            if (DateTime.UtcNow - _earnedLevelCachedAtUtc < EarnedLevelCacheTtl)
            {
                return _cachedEarnedLevel;
            }
        }

        var usageCount = await GetUsageCountInternalAsync(scope);
        var earnedLevel = UsageCountToLevel(usageCount);

        lock (_earnedLevelCacheLock)
        {
            _cachedEarnedLevel = earnedLevel;
            _earnedLevelCachedAtUtc = DateTime.UtcNow;
        }

        return earnedLevel;
    }

    private static int UsageCountToLevel(int usageCount)
    {
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
