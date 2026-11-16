using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>What the Path Map asks for.</summary>
/// <param name="Wans">The selected WAN keys, comma-separated.</param>
/// <param name="All">Every WAN is selected.</param>
/// <param name="Primary">The primary WAN's key.</param>
/// <param name="From">The window's start, UTC; null for a trailing window.</param>
/// <param name="To">The window's end, UTC; null for one ending now.</param>
/// <param name="RangeHours">A trailing window's length; 0 is 15 minutes.</param>
/// <param name="Trace">The map is open and on screen: its targets are re-traced while it stays so.</param>
public sealed record PathMapQuery(string? Wans, bool All, string? Primary, DateTime? From, DateTime? To, int? RangeHours, bool Trace);

/// <summary>
/// The Path Map on Monitoring - Network Performance: every routed latency target's route, placed and
/// merged into one tree, with each target's live state. Reads only; the traces it keeps fresh while
/// viewed are the app probing, which writes no audit entry.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IPathMapService
{
    /// <summary>The map for a WAN selection and window. Open to any role, like the charts it sits above.</summary>
    [RequireRole(Roles.Viewer)]
    Task<PathMapView> GetAsync(PathMapQuery query, CancellationToken ct = default);
}
