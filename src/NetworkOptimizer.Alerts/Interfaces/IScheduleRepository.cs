using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Alerts.Interfaces;

/// <summary>
/// Repository for scheduled task CRUD operations.
/// </summary>
public interface IScheduleRepository
{
    Task<List<ScheduledTask>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<List<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default);
    Task<ScheduledTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<int> SaveAsync(ScheduledTask task, CancellationToken cancellationToken = default);
    Task UpdateAsync(ScheduledTask task, CancellationToken cancellationToken = default);
    Task UpdateRunStatusAsync(int id, DateTime lastRun, DateTime? nextRun, string status, string? error, string? summary, CancellationToken cancellationToken = default);
    Task DeleteAsync(int id, CancellationToken cancellationToken = default);
}
