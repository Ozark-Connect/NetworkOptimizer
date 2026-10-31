using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>A target on an episode, as the event row names it.</summary>
public sealed record PathEpisodeTargetView(int MonitoringTargetId, string TargetId, string Name, PathEpisodeTargetRole Role,
    double? BaselineRttMs, double? PeakRttMs, double? BaselineLossPct, double? PeakLossPct, string? Label);

/// <summary>One episode, for the Path &amp; Congestion Events row.</summary>
public sealed record PathEpisodeView
{
    public required int Id { get; init; }
    public required DateTime OpenedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    /// <summary>Rounds are still running.</summary>
    public bool Tracing { get; init; }
    /// <summary>Last write, so a hop table that is open re-reads when a round lands.</summary>
    public DateTime UpdatedAt { get; init; }
    public int? WanContextId { get; init; }
    public string? WanInterface { get; init; }
    public CongestionScope Scope { get; init; }
    public CongestionDisposition Disposition { get; init; }
    public string? Label { get; init; }
    public string? Summary { get; init; }
    public ReturnPathFinding ReturnFinding { get; init; }
    public IReadOnlyList<PathEpisodeTargetView> Degraded { get; init; } = Array.Empty<PathEpisodeTargetView>();
    public IReadOnlyList<PathEpisodeTargetView> Controls { get; init; } = Array.Empty<PathEpisodeTargetView>();
}

/// <summary>One degraded target's hop table, and the trace a Re-run would repeat.</summary>
/// <param name="Address">The address the rounds traced.</param>
/// <param name="Vantage">"server" or "agent-N": where they ran from.</param>
/// <param name="SourceInterface">The bind they used, or null for the default route.</param>
public sealed record PathEpisodeHopTable(PathEpisodeTargetView Target, string Address, string Vantage, string? SourceInterface,
    int Rounds, bool HasRecovery, IReadOnlyList<HopReading> Hops);

/// <summary>An episode with its hop tables.</summary>
public sealed record PathEpisodeDetail(PathEpisodeView Episode, IReadOnlyList<PathEpisodeHopTable> Tables);

/// <summary>
/// Path Localization's episodes for the site in context. Reads only: episodes are evidence the app
/// captures on its own, with nothing for a user to change.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IPathEpisodeService
{
    /// <summary>
    /// Episodes overlapping a window, newest first. Open to any role, like the rest of ISP Health.
    /// </summary>
    /// <param name="wanKey">The WAN key ISP Health is grading ("wan2"), when it names one.</param>
    /// <param name="isPrimary">Whether that WAN is the primary, which owns every default-path episode.
    /// Episodes on another WAN's context are left out.</param>
    [RequireRole(Roles.Viewer)]
    Task<IReadOnlyList<PathEpisodeView>> GetEpisodesAsync(DateTime from, DateTime to, string? wanKey, bool isPrimary,
        CancellationToken ct = default);

    /// <summary>One episode with each degraded target's hop table, rebuilt from its stored rounds; null when it is gone.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PathEpisodeDetail?> GetEpisodeAsync(int id, CancellationToken ct = default);
}
