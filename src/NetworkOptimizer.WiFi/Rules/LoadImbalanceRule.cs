using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when there is significant load imbalance across APs,
/// which can cause some APs to be overloaded while others are underutilized.
/// </summary>
public class LoadImbalanceRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-LOAD-IMBALANCE-001";

    /// <summary>
    /// Coefficient of variation threshold (percentage) above which to warn.
    /// </summary>
    private const double ImbalanceThreshold = 50;

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

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.CapacityHeadroom, HealthDimension.ClientSatisfaction },
            Title = "Significant Load Imbalance",
            Description = $"{maxAp.Name} has {maxAp.TotalClients} clients while {minAp.Name} has only {minAp.TotalClients}. " +
                "This imbalance ({imbalance:F0}%) can cause performance issues on overloaded APs.",
            AffectedEntity = $"{maxAp.Name} ({maxAp.TotalClients}), {minAp.Name} ({minAp.TotalClients})",
            Recommendation = "Consider enabling band steering or adjusting TX power to balance load across APs.",
            ScoreImpact = -8
        };
    }
}
