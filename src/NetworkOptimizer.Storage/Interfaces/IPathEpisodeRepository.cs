using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Storage.Interfaces;

/// <summary>
/// Path Localization's episodes for the site in context: the episode rows, their degraded and control
/// targets, and every traceroute round taken while they lasted.
/// </summary>
public interface IPathEpisodeRepository
{
    /// <summary>Inserts a new episode with its targets. Returns its id.</summary>
    Task<int> AddAsync(PathEpisode episode, CancellationToken cancellationToken = default);

    /// <summary>Records one traceroute round on an episode.</summary>
    Task AddTraceAsync(PathEpisodeTrace trace, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes an episode's verdict, gate snapshot and lifecycle fields, and each target's own verdict, onto the stored rows.
    /// The episode's targets are matched by monitoring target and role; ones not already stored are added.
    /// </summary>
    Task UpdateAsync(PathEpisode episode, CancellationToken cancellationToken = default);

    /// <summary>Episodes that overlap [<paramref name="from"/>, <paramref name="to"/>] (an open one overlaps
    /// everything after it opened), newest first, with their targets but not their traces.</summary>
    Task<List<PathEpisode>> GetOverlappingAsync(DateTime from, DateTime to, int max, CancellationToken cancellationToken = default);

    /// <summary>One episode with its targets and traces, or null.</summary>
    Task<PathEpisode?> GetWithTracesAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Episodes still open (no ClosedAt).</summary>
    Task<List<PathEpisode>> GetOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Closed episodes with no recovery round recorded (metered, stopped short, or still waiting).</summary>
    Task<List<PathEpisode>> GetClosedWithoutRecoveryAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes episodes (and their targets and traces) opened before <paramref name="before"/>.</summary>
    Task<int> PruneAsync(DateTime before, CancellationToken cancellationToken = default);
}
