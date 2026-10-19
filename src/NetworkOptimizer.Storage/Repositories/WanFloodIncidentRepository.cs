using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Repositories;

/// <summary>
/// Repository for WAN Flood Protection's episode history.
/// </summary>
public class WanFloodIncidentRepository : IWanFloodIncidentRepository
{
    private readonly NetworkOptimizerDbContext _context;

    public WanFloodIncidentRepository(NetworkOptimizerDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<bool> UpsertAsync(WanFloodIncident incident, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incident);
        var existing = await _context.WanFloodIncidents.FirstOrDefaultAsync(i => i.IncidentId == incident.IncidentId, cancellationToken);
        if (existing == null)
        {
            _context.WanFloodIncidents.Add(incident);
        }
        else
        {
            existing.Wan = incident.Wan;
            existing.Kind = incident.Kind;
            existing.StartedAt = incident.StartedAt;
            existing.EndedAt = incident.EndedAt;
            existing.PeakUnsolicitedPps = Math.Max(existing.PeakUnsolicitedPps, incident.PeakUnsolicitedPps);
            existing.PeakSoftirqPercent = Math.Max(existing.PeakSoftirqPercent, incident.PeakSoftirqPercent);
            if (incident.HotCpu >= 0) existing.HotCpu = incident.HotCpu;
            existing.PeakConntrackPercent = Math.Max(existing.PeakConntrackPercent, incident.PeakConntrackPercent);
            existing.SoftnetDropped = Math.Max(existing.SoftnetDropped, incident.SoftnetDropped);
            existing.TableFull |= incident.TableFull;
            existing.ActionsJson = incident.ActionsJson;
            existing.Delivery = incident.Delivery;
            existing.Mode = incident.Mode;
            existing.UpdatedAt = incident.UpdatedAt;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return existing == null;
    }

    public Task<List<WanFloodIncident>> GetSinceAsync(DateTime since, int max, CancellationToken cancellationToken = default) =>
        _context.WanFloodIncidents.AsNoTracking()
            .Where(i => i.StartedAt >= since)
            .OrderByDescending(i => i.StartedAt)
            .Take(max)
            .ToListAsync(cancellationToken);

    public Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default) =>
        _context.WanFloodIncidents.Where(i => i.StartedAt < before).ExecuteDeleteAsync(cancellationToken);
}
