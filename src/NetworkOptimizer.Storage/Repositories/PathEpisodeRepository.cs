using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for Path Localization's episodes.
/// </summary>
public class PathEpisodeRepository : IPathEpisodeRepository
{
    private readonly NetworkOptimizerDbContext _context;

    public PathEpisodeRepository(NetworkOptimizerDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<int> AddAsync(PathEpisode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        _context.PathEpisodes.Add(episode);
        await _context.SaveChangesAsync(cancellationToken);
        return episode.Id;
    }

    public async Task AddTraceAsync(PathEpisodeTrace trace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        _context.PathEpisodeTraces.Add(trace);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateAsync(PathEpisode episode, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episode);
        var row = await _context.PathEpisodes.Include(e => e.Targets)
            .FirstOrDefaultAsync(e => e.Id == episode.Id, cancellationToken);
        if (row == null) return;

        row.ClosedAt = episode.ClosedAt;
        row.GateSnapshot = episode.GateSnapshot;
        row.RecoveryTracedAt = episode.RecoveryTracedAt;
        row.Scope = episode.Scope;
        row.Disposition = episode.Disposition;
        row.BottleneckHopIp = episode.BottleneckHopIp;
        row.BottleneckHopNumber = episode.BottleneckHopNumber;
        row.BottleneckLabel = episode.BottleneckLabel;
        row.ReturnPathFinding = episode.ReturnPathFinding;
        row.Summary = episode.Summary;
        row.UpdatedAt = episode.UpdatedAt;

        foreach (var target in episode.Targets)
        {
            var existing = row.Targets.FirstOrDefault(t => t.MonitoringTargetId == target.MonitoringTargetId && t.Role == target.Role);
            if (existing == null)
            {
                row.Targets.Add(new PathEpisodeTarget
                {
                    MonitoringTargetId = target.MonitoringTargetId,
                    Role = target.Role,
                    BaselineRttMs = target.BaselineRttMs,
                    PeakRttMs = target.PeakRttMs,
                    BaselineLossPct = target.BaselineLossPct,
                    PeakLossPct = target.PeakLossPct,
                    Scope = target.Scope,
                    Disposition = target.Disposition,
                    BottleneckHopIp = target.BottleneckHopIp,
                    BottleneckHopNumber = target.BottleneckHopNumber,
                    BottleneckLabel = target.BottleneckLabel
                });
                continue;
            }
            existing.PeakRttMs = target.PeakRttMs;
            existing.PeakLossPct = target.PeakLossPct;
            existing.Scope = target.Scope;
            existing.Disposition = target.Disposition;
            existing.BottleneckHopIp = target.BottleneckHopIp;
            existing.BottleneckHopNumber = target.BottleneckHopNumber;
            existing.BottleneckLabel = target.BottleneckLabel;
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    public Task<List<PathEpisode>> GetOverlappingAsync(DateTime from, DateTime to, int max, CancellationToken cancellationToken = default) =>
        _context.PathEpisodes.AsNoTracking()
            .Include(e => e.Targets)
            .Where(e => e.OpenedAt <= to && (e.ClosedAt == null || e.ClosedAt >= from))
            .OrderByDescending(e => e.OpenedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<PathEpisode?> GetWithTracesAsync(int id, CancellationToken cancellationToken = default) =>
        _context.PathEpisodes.AsNoTracking()
            .Include(e => e.Targets)
            .Include(e => e.Traces)
            .AsSplitQuery()
            .FirstOrDefaultAsync(e => e.Id == id, cancellationToken);

    public Task<List<PathEpisode>> GetOpenAsync(CancellationToken cancellationToken = default) =>
        _context.PathEpisodes.AsNoTracking()
            .Include(e => e.Targets)
            .Where(e => e.ClosedAt == null)
            .ToListAsync(cancellationToken);

    public Task<List<PathEpisode>> GetClosedWithoutRecoveryAsync(CancellationToken cancellationToken = default) =>
        _context.PathEpisodes.AsNoTracking()
            .Include(e => e.Targets)
            .Where(e => e.ClosedAt != null && e.RecoveryTracedAt == null)
            .ToListAsync(cancellationToken);

    public async Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default)
    {
        // Children first: ExecuteDelete bypasses the change tracker, so EF's cascade never runs, and
        // SQLite's own cascade depends on the connection having foreign keys switched on.
        var ids = _context.PathEpisodes.Where(e => e.OpenedAt < before).Select(e => e.Id);
        await _context.PathEpisodeTraces.Where(t => ids.Contains(t.EpisodeId)).ExecuteDeleteAsync(cancellationToken);
        await _context.PathEpisodeTargets.Where(t => ids.Contains(t.EpisodeId)).ExecuteDeleteAsync(cancellationToken);
        return await _context.PathEpisodes.Where(e => e.OpenedAt < before).ExecuteDeleteAsync(cancellationToken);
    }
}
