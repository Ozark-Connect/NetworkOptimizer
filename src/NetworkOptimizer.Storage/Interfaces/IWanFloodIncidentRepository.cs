using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// WAN Flood Protection's episode history for the site in context.
/// </summary>
public interface IWanFloodIncidentRepository
{
    /// <summary>Inserts the episode, or updates the row with its <see cref="WanFloodIncident.IncidentId"/>. True when inserted.</summary>
    Task<bool> UpsertAsync(WanFloodIncident incident, CancellationToken cancellationToken = default);

    /// <summary>Episodes that started at or after <paramref name="since"/>, newest first.</summary>
    Task<List<WanFloodIncident>> GetSinceAsync(DateTime since, int max, CancellationToken cancellationToken = default);

    /// <summary>Deletes episodes that started before <paramref name="before"/>.</summary>
    Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default);
}
