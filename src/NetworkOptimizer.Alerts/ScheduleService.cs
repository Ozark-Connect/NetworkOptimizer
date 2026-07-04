using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Background service that evaluates scheduled tasks every 60 seconds and executes those that are due.
/// Uses IServiceScopeFactory for scoped services (repositories, AuditService) and injects singletons directly.
/// </summary>
public class ScheduleService : BackgroundService
{
    private readonly ILogger<ScheduleService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAlertEventBus _alertEventBus;
    private readonly IScheduleSiteContext? _siteContext;

    // Track which tasks are currently executing. Task ids are per-site
    // database sequences, so the key must carry the site too.
    private readonly HashSet<(string SiteKey, int TaskId)> _runningTasks = new();
    private readonly object _runningLock = new();

    // Delegate types for task executors (resolved from DI in ExecuteTaskAsync)
    // This avoids coupling to concrete service types in the Alerts project.
    // Every executor receives the site key first so the host can route the
    // work to that site's services and data.

    /// <summary>
    /// Delegate that the Web project registers to execute audit tasks.
    /// Takes (siteKey) and returns (success, summary, error).
    /// </summary>
    public Func<string, CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? AuditExecutor { get; set; }

    /// <summary>
    /// Delegate that the Web project registers to execute WAN speed test tasks.
    /// Takes (siteKey, taskId, targetId, targetConfig) and returns (success, summary, error).
    /// The taskId allows the executor to update the schedule (e.g., reconcile stale WAN metadata).
    /// </summary>
    public Func<string, int, string?, string?, CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? WanSpeedTestExecutor { get; set; }

    /// <summary>
    /// Delegate that the Web project registers to execute LAN speed test tasks.
    /// Takes (siteKey, targetId, targetConfig) and returns (success, summary, error).
    /// </summary>
    public Func<string, string?, string?, CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? LanSpeedTestExecutor { get; set; }

    public ScheduleService(
        ILogger<ScheduleService> logger,
        IServiceScopeFactory scopeFactory,
        IAlertEventBus alertEventBus,
        IScheduleSiteContext? siteContext = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _alertEventBus = alertEventBus;
        _siteContext = siteContext;
    }

    private string DefaultSiteKey => _siteContext?.DefaultKey ?? "";

    private async Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct)
    {
        if (_siteContext == null)
            return new[] { DefaultSiteKey };
        try
        {
            var keys = await _siteContext.GetSiteKeysAsync(ct);
            return keys.Count > 0 ? keys : new[] { DefaultSiteKey };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Site enumeration failed; evaluating the default site's schedules only");
            return new[] { DefaultSiteKey };
        }
    }

    private IServiceScope CreatePinnedScope(string siteKey)
    {
        var scope = _scopeFactory.CreateScope();
        _siteContext?.PinScope(scope, siteKey);
        return scope;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!FeatureFlags.SchedulingEnabled)
        {
            _logger.LogInformation("Scheduling feature is disabled");
            return;
        }

        _logger.LogInformation("ScheduleService started");

        // Initial delay to let other services start up
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EvaluateSchedulesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error evaluating schedules");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("ScheduleService stopped");
    }

    private async Task EvaluateSchedulesAsync(CancellationToken ct)
    {
        foreach (var siteKey in await GetSiteKeysAsync(ct))
        {
            if (ct.IsCancellationRequested) break;
            await EvaluateSiteSchedulesAsync(siteKey, ct);
        }
    }

    private async Task EvaluateSiteSchedulesAsync(string siteKey, CancellationToken ct)
    {
        using var scope = CreatePinnedScope(siteKey);
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();

        var enabledTasks = await repo.GetEnabledAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var task in enabledTasks)
        {
            if (ct.IsCancellationRequested) break;

            // Skip if not yet due
            if (task.NextRunAt.HasValue && task.NextRunAt.Value > now)
                continue;

            // If the task is stale (overdue by more than 2 minutes), it was likely disabled
            // for a while. Advance NextRunAt to the next future slot without executing.
            if (task.NextRunAt.HasValue && task.NextRunAt.Value < now.AddMinutes(-2))
            {
                var nextRun = CalculateNextRun(task.FrequencyMinutes, task.CustomMorningHour,
                    task.CustomMorningMinute, task.NextRunAt);
                _logger.LogInformation(
                    "Advancing stale task {TaskId} ({TaskType}) from {OldNextRun} to {NewNextRun} without executing",
                    task.Id, task.TaskType, task.NextRunAt, nextRun);
                try
                {
                    await repo.UpdateNextRunAsync(task.Id, nextRun, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to advance stale task {TaskId}", task.Id);
                }
                continue;
            }

            // Skip if already running
            if (IsTaskRunning(task.Id, siteKey))
                continue;

            // Execute in background (don't block the evaluation loop)
            var taskId = task.Id;
            var taskType = task.TaskType;
            var targetId = task.TargetId;
            var targetConfig = task.TargetConfig;
            var frequencyMinutes = task.FrequencyMinutes;
            var startHour = task.CustomMorningHour;
            var startMinute = task.CustomMorningMinute;
            var scheduledRunTime = task.NextRunAt;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteScheduledTaskAsync(siteKey, taskId, taskType, targetId, targetConfig, frequencyMinutes, startHour, startMinute, scheduledRunTime, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error executing scheduled task {TaskId} ({TaskType})", taskId, taskType);
                }
            }, ct);
        }
    }

    private async Task ExecuteScheduledTaskAsync(string siteKey, int taskId, string taskType, string? targetId, string? targetConfig, int frequencyMinutes, int? startHour, int? startMinute, DateTime? scheduledRunTime, CancellationToken ct)
    {
        lock (_runningLock)
        {
            if (!_runningTasks.Add((siteKey, taskId)))
                return; // Already running
        }

        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Executing scheduled task {TaskId} ({TaskType}) for site {SiteKey}", taskId, taskType, siteKey);

        // Site-qualify alert titles only when this isn't the default site, so
        // single-site installs read exactly as before.
        var siteSuffix = siteKey == DefaultSiteKey ? "" : $" (site {siteKey})";

        try
        {
            var (success, summary, error) = taskType switch
            {
                "audit" => AuditExecutor != null
                    ? await AuditExecutor(siteKey, ct)
                    : (false, null, "Audit executor not registered"),
                "wan_speedtest" => WanSpeedTestExecutor != null
                    ? await WanSpeedTestExecutor(siteKey, taskId, targetId, targetConfig, ct)
                    : (false, null, "WAN speed test executor not registered"),
                "lan_speedtest" => LanSpeedTestExecutor != null
                    ? await LanSpeedTestExecutor(siteKey, targetId, targetConfig, ct)
                    : (false, null, "LAN speed test executor not registered"),
                _ => (false, (string?)null, $"Unknown task type: {taskType}")
            };

            var status = success ? "success" : "failed";
            var nextRun = CalculateNextRun(frequencyMinutes, startHour, startMinute, scheduledRunTime);

            // DB update - failure here shouldn't change the task's reported status
            try
            {
                using var scope = CreatePinnedScope(siteKey);
                var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
                await repo.UpdateRunStatusAsync(taskId, startTime, nextRun, status, error, summary, ct);
            }
            catch (Exception dbEx)
            {
                _logger.LogError(dbEx, "Failed to update run status for task {TaskId}", taskId);
            }

            _logger.LogInformation("Scheduled task {TaskId} ({TaskType}) completed: {Status} - {Summary}",
                taskId, taskType, status, summary ?? "no summary");

            // Alert publishing - based on actual task result, not DB update success
            if (success)
            {
                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "schedule.task_completed",
                    Severity = AlertSeverity.Info,
                    Source = "schedule",
                    SiteSlug = siteKey == DefaultSiteKey ? null : siteKey,
                    Title = $"Scheduled {FormatTaskType(taskType)} completed{siteSuffix}",
                    Message = summary ?? "Task completed successfully",
                    SourceUrl = "/alerts?tab=schedule"
                });
            }
            else
            {
                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "schedule.task_failed",
                    Severity = AlertSeverity.Error,
                    Source = "schedule",
                    SiteSlug = siteKey == DefaultSiteKey ? null : siteKey,
                    Title = $"Scheduled {FormatTaskType(taskType)} failed{siteSuffix}",
                    Message = error ?? "Task failed with no error message",
                    SourceUrl = "/alerts?tab=schedule"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing scheduled task {TaskId}", taskId);

            try
            {
                using var scope = CreatePinnedScope(siteKey);
                var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
                var nextRun = CalculateNextRun(frequencyMinutes, startHour, startMinute, scheduledRunTime);
                await repo.UpdateRunStatusAsync(taskId, startTime, nextRun, "failed", ex.Message, null, ct);
            }
            catch (Exception updateEx)
            {
                _logger.LogError(updateEx, "Failed to update task status after error");
            }

            try
            {
                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "schedule.task_failed",
                    Severity = AlertSeverity.Error,
                    Source = "schedule",
                    SiteSlug = siteKey == DefaultSiteKey ? null : siteKey,
                    Title = $"Scheduled {FormatTaskType(taskType)} failed{siteSuffix}",
                    Message = ex.Message,
                    SourceUrl = "/alerts?tab=schedule"
                });
            }
            catch { /* Don't let alert publishing failure cascade */ }
        }
        finally
        {
            lock (_runningLock)
            {
                _runningTasks.Remove((siteKey, taskId));
            }
        }
    }

    /// <summary>
    /// Trigger immediate execution of a scheduled task (Run Now button).
    /// Callers in a site-aware host pass the site the task belongs to;
    /// null means the default site.
    /// </summary>
    public async Task<bool> RunNowAsync(int scheduledTaskId, string? siteKey = null)
    {
        var key = siteKey ?? DefaultSiteKey;
        if (IsTaskRunning(scheduledTaskId, key))
            return false;

        using var scope = CreatePinnedScope(key);
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
        var task = await repo.GetByIdAsync(scheduledTaskId);
        if (task == null)
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteScheduledTaskAsync(key, task.Id, task.TaskType, task.TargetId, task.TargetConfig, task.FrequencyMinutes, task.CustomMorningHour, task.CustomMorningMinute, null, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RunNow for task {TaskId}", task.Id);
            }
        });

        return true;
    }

    /// <summary>
    /// Check if a task is currently executing. Null site = the default site.
    /// </summary>
    public bool IsTaskRunning(int scheduledTaskId, string? siteKey = null)
    {
        lock (_runningLock)
        {
            return _runningTasks.Contains((siteKey ?? DefaultSiteKey, scheduledTaskId));
        }
    }

    /// <summary>
    /// Calculate next run time. If startHour/startMinute are set, anchors runs to that
    /// time-of-day (UTC). E.g., startHour=6, frequency=720 (12h) → runs at 06:00 and 18:00 UTC.
    /// When scheduledRunTime is provided (from a scheduled execution), the next run is calculated
    /// relative to that time to prevent drift from execution duration.
    /// </summary>
    public static DateTime CalculateNextRun(int frequencyMinutes, int? startHour = null,
        int? startMinute = null, DateTime? scheduledRunTime = null)
    {
        if (startHour == null)
        {
            if (frequencyMinutes <= 0)
                return DateTime.UtcNow.AddMinutes(60);

            var baseTime = scheduledRunTime ?? DateTime.UtcNow;
            // Truncate to the minute to prevent sub-minute drift from accumulating
            // (e.g., first run uses DateTime.UtcNow which has fractional seconds)
            baseTime = new DateTime(baseTime.Year, baseTime.Month, baseTime.Day,
                baseTime.Hour, baseTime.Minute, 0, DateTimeKind.Utc);
            var next = baseTime.AddMinutes(frequencyMinutes);
            // If calculated time is in the past (task was very delayed), walk forward
            var now = DateTime.UtcNow;
            while (next <= now)
                next = next.AddMinutes(frequencyMinutes);
            return next;
        }

        // Find the next occurrence anchored to startHour:startMinute
        var now2 = DateTime.UtcNow;
        var today = now2.Date;
        var anchor = today.AddHours(startHour.Value).AddMinutes(startMinute ?? 0);

        // Walk forward from anchor by frequency until we find a time in the future
        // (with 1-minute buffer to avoid re-triggering immediately)
        if (frequencyMinutes <= 0)
            return now2.AddMinutes(60);

        // Walk backward from anchor to find a starting point before now,
        // then walk forward to the next slot. Without this, an anchor later
        // today (e.g., 23:45) would be returned directly, skipping earlier
        // hourly slots (e.g., 20:45, 21:45, 22:45).
        var candidate = anchor;
        while (candidate > now2)
            candidate = candidate.AddMinutes(-frequencyMinutes);
        while (candidate <= now2.AddMinutes(1))
            candidate = candidate.AddMinutes(frequencyMinutes);

        return candidate;
    }

    private static string FormatTaskType(string taskType) => taskType switch
    {
        "audit" => "security audit",
        "wan_speedtest" => "WAN speed test",
        "lan_speedtest" => "LAN speed test",
        _ => taskType
    };
}
