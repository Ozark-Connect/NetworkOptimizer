using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>
/// Thins snapshots (spec "Retention"): every snapshot for the first tier, the last per day to the
/// second, the last per month after that. Pinned snapshots, the Baseline, and the newest snapshot
/// are never thinned. The change history is never thinned; only snapshots are.
/// </summary>
public static class SnapshotRetention
{
    /// <summary>Snapshot IDs to delete.</summary>
    public static List<int> ToDelete(IReadOnlyList<ConfigSnapshot> snapshots, ChangeControlSettings settings, DateTime nowUtc,
        IReadOnlySet<int>? protectedIds = null)
    {
        var keepAllAfter = nowUtc.AddDays(-Math.Max(1, settings.KeepEveryChangeDays));
        var keepDailyAfter = nowUtc.AddDays(-Math.Max(settings.KeepEveryChangeDays, settings.KeepDailyDays));
        DateTime? keepMonthlyAfter = settings.KeepMonthlyMonths is { } months ? nowUtc.AddMonths(-months) : null;
        var newest = snapshots.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id).FirstOrDefault()?.Id;

        bool Protected(ConfigSnapshot s) => s.Pinned || s.IsBaseline || s.Id == newest || protectedIds?.Contains(s.Id) == true;

        var delete = new List<int>();
        var old = snapshots.Where(s => s.TakenAt < keepAllAfter && !Protected(s)).ToList();

        // Daily tier: keep the last of each day.
        foreach (var day in old.Where(s => s.TakenAt >= keepDailyAfter).GroupBy(s => s.TakenAt.Date))
            delete.AddRange(day.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id).Skip(1).Select(s => s.Id));

        // Monthly tier: keep the last of each month, unless the month is past the monthly limit.
        foreach (var month in old.Where(s => s.TakenAt < keepDailyAfter).GroupBy(s => (s.TakenAt.Year, s.TakenAt.Month)))
        {
            var ordered = month.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Id).ToList();
            var keepLast = keepMonthlyAfter == null || ordered[0].TakenAt >= keepMonthlyAfter;
            delete.AddRange(ordered.Skip(keepLast ? 1 : 0).Select(s => s.Id));
        }
        return delete;
    }
}
