using System.Collections.Concurrent;
using NetworkOptimizer.Alerts;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Turns a scheduled audit's result into the scheduler's outcome. A run without a working console
/// connection waits and retries a few times before it is reported failed, and is never a score.
/// </summary>
public sealed class ScheduledAuditRetryPolicy
{
    /// <summary>Retries of one slot before the run is reported failed.</summary>
    public const int MaxRetries = 3;

    /// <summary>Wait between retries: long enough for an agent tunnel or a console restart to recover.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(10);

    // Per site: one scheduled audit task per site. In memory, so a restart starts the count over.
    private readonly ConcurrentDictionary<string, int> _retries = new();

    /// <summary>The scheduler outcome for this site's audit result.</summary>
    public ScheduleRunOutcome Decide(string siteKey, AuditResult result, DateTime now)
    {
        if (result.RunStatus != AuditRunStatus.ConsoleUnavailable)
            _retries.TryRemove(siteKey, out _);

        switch (result.RunStatus)
        {
            case AuditRunStatus.Completed:
                var summary = result.CriticalCount > 0 || result.WarningCount > 0
                    ? $"Score: {result.Score} - {result.CriticalCount} critical, {result.WarningCount} recommended"
                    : $"Score: {result.Score}";
                return new ScheduleRunOutcome(true, summary, null);

            case AuditRunStatus.ConsoleUnavailable:
                var attempt = _retries.AddOrUpdate(siteKey, 1, (_, n) => n + 1);
                if (attempt <= MaxRetries)
                {
                    return new ScheduleRunOutcome(true,
                        $"Waiting for the UniFi Console connection (retry {attempt} of {MaxRetries})", null,
                        NextRunAt: now + RetryDelay, Notify: false, Status: "waiting", KeepSlot: true);
                }

                _retries.TryRemove(siteKey, out _);
                return new ScheduleRunOutcome(false, null,
                    $"The UniFi Console connection was unavailable for this audit and {MaxRetries} retries. Nothing was saved.");

            default:
                return new ScheduleRunOutcome(false, null,
                    result.Issues.FirstOrDefault()?.Description ?? "The audit failed");
        }
    }
}
