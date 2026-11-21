using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Decides which Transit targets are far (see <see cref="MonitoringTarget.FarTransitAt"/>): an RTT
/// floor more than <see cref="FarAboveMedianMs"/> above the median floor of the transit targets on
/// the same WAN.
/// <para>
/// Hop order cannot decide this. MPLS hides interior hops, so a distant POP can answer at the same
/// TTL as a near one, and a far handoff reads the same as a deep backbone hop. The median is the
/// anchor rather than the lowest floor, so one local exchange point does not make every regional
/// POP read as far, and the far target itself cannot drag the anchor toward it.
/// </para>
/// </summary>
public static class FarTransit
{
    /// <summary>A target is judged far once its floor sits this far above the WAN's median floor.</summary>
    public const double FarAboveMedianMs = 20;

    /// <summary>A far target is judged near again only below this, so a floor near the line does not flip each sweep.</summary>
    public const double NearAboveMedianMs = 15;

    /// <summary>Fewer measured targets than this on a WAN leave no median worth trusting, and nothing there is far.</summary>
    public const int MinTargets = 3;

    /// <summary>How far back a floor reads.</summary>
    public static readonly TimeSpan FloorWindow = TimeSpan.FromHours(24);

    /// <summary>Aggregate window of the floor read.</summary>
    public static readonly TimeSpan FloorAggregate = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Aggregate points a target needs before its floor counts (15 minutes at <see cref="FloorAggregate"/>),
    /// so a new or re-enabled target is judged before it skews the figures for long. The hysteresis
    /// band keeps a short, noisy first floor from flipping a verdict back and forth.
    /// </summary>
    public const int MinFloorPoints = 3;

    /// <summary>One Transit target as the classifier sees it.</summary>
    /// <param name="TargetId">The target's id.</param>
    /// <param name="WanKey">The WAN it measures, with unpinned read as the primary.</param>
    /// <param name="Floor">Its RTT floor, or null with too little data to say.</param>
    /// <param name="WasFar">Whether the last sweep judged it far.</param>
    public readonly record struct Candidate(string TargetId, string WanKey, double? Floor, bool WasFar);

    /// <summary>A target's floor: the median of its aggregate RTT points, or null with fewer than <see cref="MinFloorPoints"/>.</summary>
    public static double? Floor(IReadOnlyList<double> rtts) =>
        rtts.Count < MinFloorPoints ? null : SeriesStats.Median(rtts);

    /// <summary>
    /// The targets that are far after this sweep. A target with no floor keeps its last verdict:
    /// a hop that stops answering for a while has not moved.
    /// </summary>
    public static HashSet<string> Classify(IEnumerable<Candidate> candidates)
    {
        var far = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wan in candidates.GroupBy(c => c.WanKey, StringComparer.OrdinalIgnoreCase))
        {
            var measured = wan.Where(c => c.Floor.HasValue).ToList();
            if (measured.Count < MinTargets) continue;
            var median = SeriesStats.Median(measured.Select(c => c.Floor!.Value).ToList())!.Value;
            foreach (var c in wan)
            {
                var isFar = c.Floor is double floor
                    ? floor - median > (c.WasFar ? NearAboveMedianMs : FarAboveMedianMs)
                    : c.WasFar;
                if (isFar) far.Add(c.TargetId);
            }
        }
        return far;
    }

    /// <summary>The WAN a target is grouped under: its own key, or the primary's when unpinned.</summary>
    public static string WanKeyOf(string? wanInterface) => MonitoringTarget.IsUnpinned(wanInterface)
        ? GatewayWanHelper.DefaultWanKey
        : GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface!);

    /// <summary>
    /// Reads every enabled Transit target's floor and stores the verdicts. Returns how many targets
    /// changed. Anycast and exchange infrastructure stays out, as it does everywhere transit is read.
    /// </summary>
    public static async Task<int> SweepAsync(NetworkOptimizerDbContext db, MonitoringInfluxClient influx,
        ILogger logger, CancellationToken ct)
    {
        // A target retyped away from Transit keeps no verdict: only Transit is judged here.
        var retyped = await db.MonitoringTargets
            .Where(t => t.FarTransitAt != null && t.TargetType != MonitoringTargetType.Transit)
            .ToListAsync(ct);
        foreach (var t in retyped) t.FarTransitAt = null;

        var targets = await db.MonitoringTargets
            .Where(t => t.Enabled && t.TargetType == MonitoringTargetType.Transit)
            .ToListAsync(ct);
        targets = targets
            .Where(t => !(t.AsnNumber is int a && WellKnownAsns.NonTransitInfrastructure.Contains(a)))
            .ToList();
        if (targets.Count == 0)
        {
            if (retyped.Count > 0) await db.SaveChangesAsync(ct);
            return retyped.Count;
        }

        var now = DateTime.UtcNow;
        var series = await influx.QueryLatencyByTargetTypeAsync(
            MonitoringTargetType.Transit, now - FloorWindow, now, FloorAggregate, ct: ct);
        double? FloorOf(string id) => series.TryGetValue(id, out var pts)
            ? Floor(pts.Where(p => p.RttAvgMs.HasValue).Select(p => p.RttAvgMs!.Value).ToList())
            : null;

        var candidates = targets
            .Select(t => new Candidate(t.TargetId, WanKeyOf(t.WanInterface), FloorOf(t.TargetId), t.FarTransitAt != null))
            .ToList();
        var far = Classify(candidates);

        var changed = retyped.Count;
        foreach (var t in targets)
        {
            var isFar = far.Contains(t.TargetId);
            if (isFar == (t.FarTransitAt != null)) continue;
            t.FarTransitAt = isFar ? now : null;
            changed++;
            logger.LogInformation("Transit target {Name} ({Address}) is now {Verdict} (floor {Floor} ms)",
                t.Name, t.Address, isFar ? "far" : "near", FloorOf(t.TargetId)?.ToString("0.0") ?? "unknown");
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}
