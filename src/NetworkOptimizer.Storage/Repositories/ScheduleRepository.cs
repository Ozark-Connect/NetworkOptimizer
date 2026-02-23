using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for scheduled task CRUD operations.
/// </summary>
public class ScheduleRepository : IScheduleRepository
{
    private readonly NetworkOptimizerDbContext _context;
    private readonly ILogger<ScheduleRepository> _logger;

    public ScheduleRepository(NetworkOptimizerDbContext context, ILogger<ScheduleRepository> logger)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<List<ScheduledTask>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ScheduledTasks
                .AsNoTracking()
                .OrderBy(t => t.TaskType)
                .ThenBy(t => t.Name)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduled tasks");
            throw;
        }
    }

    public async Task<List<ScheduledTask>> GetEnabledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ScheduledTasks
                .AsNoTracking()
                .Where(t => t.Enabled)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get enabled scheduled tasks");
            throw;
        }
    }

    public async Task<ScheduledTask?> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _context.ScheduledTasks
                .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get scheduled task {TaskId}", id);
            throw;
        }
    }

    public async Task<int> SaveAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            task.CreatedAt = DateTime.UtcNow;
            _context.ScheduledTasks.Add(task);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Saved scheduled task {TaskId}: {Name}", task.Id, task.Name);
            return task.Id;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save scheduled task {Name}", task.Name);
            throw;
        }
    }

    public async Task UpdateAsync(ScheduledTask task, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.ScheduledTasks.Update(task);
            await _context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Updated scheduled task {TaskId}: {Name}", task.Id, task.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update scheduled task {TaskId}", task.Id);
            throw;
        }
    }

    public async Task UpdateRunStatusAsync(int id, DateTime lastRun, DateTime? nextRun, string status, string? error, string? summary, CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await _context.ScheduledTasks.FindAsync([id], cancellationToken);
            if (task != null)
            {
                task.LastRunAt = lastRun;
                task.NextRunAt = nextRun;
                task.LastStatus = status;
                task.LastErrorMessage = error;
                task.LastResultSummary = summary;
                await _context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update run status for task {TaskId}", id);
            throw;
        }
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var task = await _context.ScheduledTasks.FindAsync([id], cancellationToken);
            if (task != null)
            {
                _context.ScheduledTasks.Remove(task);
                await _context.SaveChangesAsync(cancellationToken);
                _logger.LogInformation("Deleted scheduled task {TaskId}: {Name}", id, task.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete scheduled task {TaskId}", id);
            throw;
        }
    }
}
