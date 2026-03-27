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
    /// Signal strength (dBm) at or above which a client is considered well-connected.
    /// </summary>
    private const int StrongSignalThreshold = -65;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only relevant for multi-AP deployments
        if (ctx.AccessPoints.Count <= 1)
            return null;

        var totalClients = ctx.Clients.Count;
        var avgClientsPerAp = (double)totalClients / ctx.AccessPoints.Count;

        if (avgClientsPerAp <= 0)
            return null;

        // Calculate load imbalance as coefficient of variation (stddev / mean * 100)
        var clientCounts = ctx.AccessPoints.Select(ap => (double)ap.TotalClients).ToList();
        var stdDev = Math.Sqrt(clientCounts.Average(c => Math.Pow(c - avgClientsPerAp, 2)));
        var imbalance = Math.Min(100, (stdDev / avgClientsPerAp) * 100);

        if (imbalance < ImbalanceThreshold)
            return null;

        var maxAp = ctx.AccessPoints.OrderByDescending(a => a.TotalClients).First();
        var minAp = ctx.AccessPoints.OrderBy(a => a.TotalClients).First();

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

                    var allStrongSignal = clientsOnMaxAp.Count > 0 &&
                        clientsOnMaxAp.All(c => c.Signal.HasValue && c.Signal.Value >= StrongSignalThreshold);

                    if (allStrongSignal)
                    {
                        // All clients on the busy AP have strong signal and the quiet AP is
                        // far away - this is definitively a separate coverage zone, suppress entirely
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
