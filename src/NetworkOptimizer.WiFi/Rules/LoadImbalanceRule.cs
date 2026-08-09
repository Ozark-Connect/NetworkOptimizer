using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;
using NetworkOptimizer.WiFi.Services;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when there is significant load imbalance across APs,
/// which can cause some APs to be overloaded while others are underutilized.
/// Uses RF propagation modeling (when available) to suppress warnings for APs
/// that are too far apart to share clients.
/// </summary>
public class LoadImbalanceRule : IWiFiOptimizerRule
{
    private readonly PropagationService _propagationService;

    public LoadImbalanceRule(PropagationService propagationService)
    {
        _propagationService = propagationService;
    }

    public string RuleId => "WIFI-LOAD-IMBALANCE-001";

    /// <summary>
    /// Coefficient of variation threshold (percentage) above which to warn.
    /// </summary>
    private const double ImbalanceThreshold = 50;

    /// <summary>
    /// Clients the busiest AP must carry before an imbalance is worth raising. The coefficient of
    /// variation is scale-free, so 3 clients against 1 scores exactly as 30 against 10 - and an AP
    /// with three clients is not overloaded by any measure the recommendation could act on.
    /// </summary>
    private const int MinClientsOnBusiestAp = 8;

    /// <summary>
    /// How far apart the busiest and quietest AP must be. Guards the other end of the same problem:
    /// a gap of one or two clients is a single device roaming, not a distribution worth steering.
    /// </summary>
    private const int MinClientGap = 4;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only relevant for multi-AP deployments
        if (ctx.AccessPoints.Count <= 1)
            return null;

        // Mean and deviations must come from the SAME population. This measured spread about
        // ctx.Clients.Count / APs - every client on the site, wired included - while deviating
        // per-AP wireless counts, so identical APs still scored: two APs of 6 against 24 site
        // clients gave a mean of 12, a deviation of 6 each, and exactly the 50% threshold, then
        // reported one AP as overloaded against another with the same count.
        var clientCounts = ctx.AccessPoints.Select(ap => (double)ap.TotalClients).ToList();
        var avgClientsPerAp = clientCounts.Average();

        if (avgClientsPerAp <= 0)
            return null;

        // Calculate load imbalance as coefficient of variation (stddev / mean * 100)
        var stdDev = Math.Sqrt(clientCounts.Average(c => Math.Pow(c - avgClientsPerAp, 2)));
        var imbalance = Math.Min(100, (stdDev / avgClientsPerAp) * 100);

        if (imbalance < ImbalanceThreshold)
            return null;

        // Stable tie-breaking: when multiple APs have the same client count, use MAC to
        // guarantee maxAp and minAp are different APs (opposite MAC sort direction).
        var maxAp = ctx.AccessPoints.OrderByDescending(a => a.TotalClients).ThenBy(a => a.Mac).First();
        var minAp = ctx.AccessPoints.OrderBy(a => a.TotalClients).ThenByDescending(a => a.Mac).First();

        // Safety: if they resolved to the same AP (e.g., single AP after filtering), bail
        if (maxAp.Mac.Equals(minAp.Mac, StringComparison.OrdinalIgnoreCase))
            return null;

        // A relative measure alone fires on counts too small to mean anything. Both floors are
        // absolute so the claim the issue makes - one AP is carrying too much - has to be true in
        // clients, not just in ratio.
        if (maxAp.TotalClients < MinClientsOnBusiestAp
            || maxAp.TotalClients - minAp.TotalClients < MinClientGap)
            return null;

        // RF distance check: if both APs are placed on the floor plan, use propagation
        // modeling to determine if they're in separate coverage zones. If the APs are
        // too far apart for clients to roam between them, load imbalance is expected.
        if (ctx.PropagationContext != null)
        {
            var maxMac = maxAp.Mac.ToLowerInvariant();
            var minMac = minAp.Mac.ToLowerInvariant();

            if (ctx.PropagationContext.ApsByMac.TryGetValue(maxMac, out var maxProp) &&
                ctx.PropagationContext.ApsByMac.TryGetValue(minMac, out var minProp))
            {
                // Check if the APs can reach each other on any common band.
                // Use the same interference threshold as co-channel checks (-70 dBm).
                var bands = new[] { "5", "2.4", "6" };
                var apsInterfere = false;
                foreach (var band in bands)
                {
                    // Only check bands both APs have active radios on
                    var bandEnum = band switch
                    {
                        "2.4" => RadioBand.Band2_4GHz,
                        "5" => RadioBand.Band5GHz,
                        "6" => RadioBand.Band6GHz,
                        _ => RadioBand.Band5GHz
                    };
                    if (!maxAp.Radios.Any(r => r.Band == bandEnum && r.Channel.HasValue) ||
                        !minAp.Radios.Any(r => r.Band == bandEnum && r.Channel.HasValue))
                        continue;

                    if (_propagationService.DoApsInterfere(maxProp, minProp, band,
                        ctx.PropagationContext.WallsByFloor, ctx.PropagationContext.Buildings))
                    {
                        apsInterfere = true;
                        break;
                    }
                }

                if (!apsInterfere)
                {
                    // APs are RF-distant (separate coverage zones) - imbalance is expected.
                    // Additionally confirm: if clients on the overloaded AP all have strong
                    // signals, they're well-placed and shouldn't be steered elsewhere.
                    var clientsOnMaxAp = ctx.Clients
                        .Where(c => c.ApMac.Equals(maxAp.Mac, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    // Suppress unless a client on the busy AP is genuinely weak FOR ITS BAND, using
                    // the same per-band scale as the client list coloring. Higher bands tolerate
                    // weaker signal: -75 is weak on 2.4 GHz (< -73) but fine on 5/6 GHz (weak only
                    // below -78 / -87). Clients with no reported signal are ignored - a missing
                    // reading usually just means an offline/idle device, not a weak one.
                    var hasWeakClient = clientsOnMaxAp.Any(c => c.Signal.HasValue &&
                        SignalClassification.IsWeakSignal(c.Signal.Value, c.Band));

                    if (!hasWeakClient)
                    {
                        // Separate coverage zone with no genuinely weak clients - suppress entirely.
                        return null;
                    }

                    // APs are distant but some clients have weak signal - could indicate
                    // a coverage gap rather than a load balancing issue. Downgrade to Info.
                    return new HealthIssue
                    {
                        Severity = HealthIssueSeverity.Info,
                        Dimensions = { HealthDimension.CapacityHeadroom },
                        Title = "Significant Load Imbalance",
                        Description = $"{maxAp.Name} has {maxAp.TotalClients} clients while {minAp.Name} has only {minAp.TotalClients}. " +
                            $"These APs are in separate coverage zones so some imbalance is expected, " +
                            $"but some clients on {maxAp.Name} have weak signal.",
                        AffectedEntity = $"{maxAp.Name} ({maxAp.TotalClients}), {minAp.Name} ({minAp.TotalClients})",
                        Recommendation = "Check if weak-signal clients on the busy AP could benefit from additional coverage in that zone.",
                        ScoreImpact = -2
                    };
                }
            }
        }

        var recommendation = "Consider lowering TX power on the overloaded AP or tightening minimum RSSI to encourage roaming to nearby APs.";

        // Hint about floor plan placement if propagation context isn't available
        if (ctx.PropagationContext == null)
            recommendation += " Place your APs on the Signal Map to enable RF distance analysis - this issue may be suppressed if the APs are in separate coverage zones.";

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.CapacityHeadroom },
            Title = "Significant Load Imbalance",
            Description = $"{maxAp.Name} has {maxAp.TotalClients} clients while {minAp.Name} has only {minAp.TotalClients}. " +
                $"This imbalance ({imbalance:F0}%) can cause performance issues on overloaded APs.",
            AffectedEntity = $"{maxAp.Name} ({maxAp.TotalClients}), {minAp.Name} ({minAp.TotalClients})",
            Recommendation = recommendation,
            ScoreImpact = -8
        };
    }
}
