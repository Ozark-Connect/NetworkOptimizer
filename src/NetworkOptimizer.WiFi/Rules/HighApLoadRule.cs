using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when specific APs have significantly higher client load than average,
/// which may cause performance degradation for clients on those APs.
/// </summary>
public class HighApLoadRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-AP-LOAD-001";

    /// <summary>
    /// Multiplier above average to consider "high load" (2x = 200% of average).
    /// </summary>
    private const double HighLoadMultiplier = 2.0;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only relevant for multi-AP deployments
        if (ctx.AccessPoints.Count <= 1)
            return null;

        var totalClients = ctx.Clients.Count;
        var avgClientsPerAp = (double)totalClients / ctx.AccessPoints.Count;

        if (avgClientsPerAp <= 0)
            return null;

        // Find APs with more than 2x average clients
        var overloadedAps = ctx.AccessPoints
            .Where(ap => ap.TotalClients > avgClientsPerAp * HighLoadMultiplier)
            .ToList();

        if (overloadedAps.Count == 0)
            return null;

        if (overloadedAps.Count == 1)
        {
            var ap = overloadedAps[0];
            return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.CapacityHeadroom },
                Title = $"High Load on {ap.Name}",
                Description = $"This AP has {ap.TotalClients} clients, which is more than 2x the average ({avgClientsPerAp:F0}). " +
                    "Clients may experience degraded performance.",
                AffectedEntity = ap.Name,
                Recommendation = "Consider adjusting TX power, enabling load balancing features, or adding APs to the area.",
                ScoreImpact = -8
            };
        }

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.CapacityHeadroom },
            Title = $"{overloadedAps.Count} APs with High Client Load",
            Description = $"{overloadedAps.Count} access points have more than 2x the average client count ({avgClientsPerAp:F0}). " +
                "This may indicate coverage or load balancing issues.",
            AffectedEntity = string.Join(", ", overloadedAps.Select(ap => $"{ap.Name} ({ap.TotalClients})")),
            Recommendation = "Consider adjusting TX power, enabling load balancing features, or adding APs to busy areas.",
            ScoreImpact = -8 * overloadedAps.Count
        };
    }
}
