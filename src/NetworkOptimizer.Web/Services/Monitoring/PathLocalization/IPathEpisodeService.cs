using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>A target on an episode, as the event row names it.</summary>
/// <param name="Category">The Network Performance chart category it is drawn under.</param>
/// <param name="BaselineJitterMs">Window jitter before the episode, for <see cref="PathShiftMatch.IsStep"/>.</param>
/// <param name="PeakJitterMs">Worst window jitter while degraded.</param>
/// <param name="AsnNumber">The monitoring target's network, for the Path shift row's label.</param>
/// <param name="IsDestination">An InternetService target, as the step detector marks its series.</param>
public sealed record PathEpisodeTargetView(int MonitoringTargetId, string TargetId, string Name, PathEpisodeTargetRole Role,
    double? BaselineRttMs, double? PeakRttMs, double? BaselineLossPct, double? PeakLossPct, string? Label,
    string Category = MonitoringLinks.CustomCategory, double? BaselineJitterMs = null, double? PeakJitterMs = null,
    int? AsnNumber = null, bool IsDestination = false);

/// <summary>One episode, for the Path &amp; Congestion Events row.</summary>
public sealed record PathEpisodeView
{
    public required int Id { get; init; }
    public required DateTime OpenedAt { get; init; }
    public DateTime? ClosedAt { get; init; }
    /// <summary>Rounds are still running.</summary>
    public bool Tracing { get; init; }
    /// <summary>Open with its rounds used up: the problem is still on, and the recovery round is next.</summary>
    public bool Ongoing => ClosedAt == null && !Tracing;
    /// <summary>Path rounds run so far, when the episode is running here; null otherwise.</summary>
    public int? RoundsTraced { get; init; }
    /// <summary>The WAN is metered: one round, and no recovery round.</summary>
    public bool Metered { get; init; }
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
/// <param name="VantageName">Where they ran from, as the page names it: "this server" or the agent's name.</param>
/// <param name="TracedAddress">The address a hostname target was pinned to for the episode; null for an IP target.</param>
public sealed record PathEpisodeHopTable(PathEpisodeTargetView Target, string Address, string Vantage, string? SourceInterface,
    int Rounds, bool HasRecovery, IReadOnlyList<HopReading> Hops, string VantageName, string? TracedAddress = null);

/// <summary>An episode with its hop tables.</summary>
/// <param name="PingedHops">Hops decided by pinging them during the episode rather than by a monitored series.</param>
/// <param name="HopPings">What each pinged hop's echoes returned, by address.</param>
/// <param name="MonitoredLoss">Each monitored hop's own series loss over the episode, by address.</param>
public sealed record PathEpisodeDetail(PathEpisodeView Episode, IReadOnlyList<PathEpisodeHopTable> Tables, IReadOnlySet<string>? PingedHops = null,
    IReadOnlyDictionary<string, PathGateSnapshot.HopPingStats>? HopPings = null, IReadOnlyDictionary<string, double>? MonitoredLoss = null);

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
