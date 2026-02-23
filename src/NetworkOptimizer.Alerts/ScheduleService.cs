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

    // Track which tasks are currently executing (by task ID)
    private readonly HashSet<int> _runningTasks = new();
    private readonly object _runningLock = new();

    // Delegate types for task executors (resolved from DI in ExecuteTaskAsync)
    // This avoids coupling to concrete service types in the Alerts project

    /// <summary>
    /// Delegate that the Web project registers to execute audit tasks.
    /// Returns (success, summary, error).
    /// </summary>
    public Func<CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? AuditExecutor { get; set; }

    /// <summary>
    /// Delegate that the Web project registers to execute WAN speed test tasks.
    /// Takes (targetId, targetConfig) and returns (success, summary, error).
    /// </summary>
    public Func<string?, string?, CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? WanSpeedTestExecutor { get; set; }

    /// <summary>
    /// Delegate that the Web project registers to execute LAN speed test tasks.
    /// Takes (targetId, targetConfig) and returns (success, summary, error).
    /// </summary>
    public Func<string?, string?, CancellationToken, Task<(bool Success, string? Summary, string? Error)>>? LanSpeedTestExecutor { get; set; }

    public ScheduleService(
        ILogger<ScheduleService> logger,
        IServiceScopeFactory scopeFactory,
        IAlertEventBus alertEventBus)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _alertEventBus = alertEventBus;
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
        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();

        var enabledTasks = await repo.GetEnabledAsync(ct);
        var now = DateTime.UtcNow;

        foreach (var task in enabledTasks)
        {
            if (ct.IsCancellationRequested) break;

            // Skip if not yet due
            if (task.NextRunAt.HasValue && task.NextRunAt.Value > now)
                continue;

            // Skip if already running
            if (IsTaskRunning(task.Id))
                continue;

            // Execute in background (don't block the evaluation loop)
            var taskId = task.Id;
            var taskType = task.TaskType;
            var targetId = task.TargetId;
            var targetConfig = task.TargetConfig;
            var frequencyMinutes = task.FrequencyMinutes;
            var startHour = task.CustomMorningHour;
            var startMinute = task.CustomMorningMinute;

            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteScheduledTaskAsync(taskId, taskType, targetId, targetConfig, frequencyMinutes, startHour, startMinute, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled error executing scheduled task {TaskId} ({TaskType})", taskId, taskType);
                }
            }, ct);
        }
    }

    private async Task ExecuteScheduledTaskAsync(int taskId, string taskType, string? targetId, string? targetConfig, int frequencyMinutes, int? startHour, int? startMinute, CancellationToken ct)
    {
        lock (_runningLock)
        {
            if (!_runningTasks.Add(taskId))
                return; // Already running
        }

        var startTime = DateTime.UtcNow;
        _logger.LogInformation("Executing scheduled task {TaskId} ({TaskType})", taskId, taskType);

        try
        {
            var (success, summary, error) = taskType switch
            {
                "audit" => AuditExecutor != null
                    ? await AuditExecutor(ct)
                    : (false, null, "Audit executor not registered"),
                "wan_speedtest" => WanSpeedTestExecutor != null
                    ? await WanSpeedTestExecutor(targetId, targetConfig, ct)
                    : (false, null, "WAN speed test executor not registered"),
                "lan_speedtest" => LanSpeedTestExecutor != null
                    ? await LanSpeedTestExecutor(targetId, targetConfig, ct)
                    : (false, null, "LAN speed test executor not registered"),
                _ => (false, (string?)null, $"Unknown task type: {taskType}")
            };

            var status = success ? "success" : "failed";
            var nextRun = CalculateNextRun(frequencyMinutes, startHour, startMinute);

            // Update task status in DB
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
            await repo.UpdateRunStatusAsync(taskId, startTime, nextRun, status, error, summary, ct);

            _logger.LogInformation("Scheduled task {TaskId} ({TaskType}) completed: {Status} - {Summary}",
                taskId, taskType, status, summary ?? "no summary");

            // Publish alert events
            if (success)
            {
                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "schedule.task_completed",
                    Severity = AlertSeverity.Info,
                    Source = "schedule",
                    Title = $"Scheduled {FormatTaskType(taskType)} completed",
                    Message = summary ?? "Task completed successfully"
                });
            }
            else
            {
                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "schedule.task_failed",
                    Severity = AlertSeverity.Error,
                    Source = "schedule",
                    Title = $"Scheduled {FormatTaskType(taskType)} failed",
                    Message = error ?? "Task failed with no error message"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing scheduled task {TaskId}", taskId);

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
                var nextRun = CalculateNextRun(frequencyMinutes, startHour, startMinute);
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
                    Title = $"Scheduled {FormatTaskType(taskType)} failed",
                    Message = ex.Message
                });
            }
            catch { /* Don't let alert publishing failure cascade */ }
        }
        finally
        {
            lock (_runningLock)
            {
                _runningTasks.Remove(taskId);
            }
        }
    }

    /// <summary>
    /// Trigger immediate execution of a scheduled task (Run Now button).
    /// </summary>
    public async Task<bool> RunNowAsync(int scheduledTaskId)
    {
        if (IsTaskRunning(scheduledTaskId))
            return false;

        using var scope = _scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IScheduleRepository>();
        var task = await repo.GetByIdAsync(scheduledTaskId);
        if (task == null)
            return false;

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteScheduledTaskAsync(task.Id, task.TaskType, task.TargetId, task.TargetConfig, task.FrequencyMinutes, task.CustomMorningHour, task.CustomMorningMinute, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in RunNow for task {TaskId}", task.Id);
            }
        });

        return true;
    }

    /// <summary>
    /// Check if a task is currently executing.
    /// </summary>
    public bool IsTaskRunning(int scheduledTaskId)
    {
        lock (_runningLock)
        {
            return _runningTasks.Contains(scheduledTaskId);
        }
    }

    /// <summary>
    /// Calculate next run time. If startHour/startMinute are set, anchors runs to that
    /// time-of-day (UTC). E.g., startHour=6, frequency=720 (12h) → runs at 06:00 and 18:00 UTC.
    /// </summary>
    private static DateTime CalculateNextRun(int frequencyMinutes, int? startHour = null, int? startMinute = null)
    {
        if (startHour == null)
            return DateTime.UtcNow.AddMinutes(frequencyMinutes);

        // Find the next occurrence anchored to startHour:startMinute
        var now = DateTime.UtcNow;
        var today = now.Date;
        var anchor = today.AddHours(startHour.Value).AddMinutes(startMinute ?? 0);

        // Walk forward from anchor by frequency until we find a time in the future
        // (with 1-minute buffer to avoid re-triggering immediately)
        var candidate = anchor;
        while (candidate <= now.AddMinutes(1))
        {
            candidate = candidate.AddMinutes(frequencyMinutes);
        }

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
