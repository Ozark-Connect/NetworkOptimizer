using NetworkOptimizer.WiFi.Helpers;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when a significant percentage of all clients have weak signal,
/// indicating overall coverage gaps in the deployment.
/// Uses band-aware thresholds (2.4 GHz needs stronger signal than 6 GHz).
/// </summary>
public class WeakSignalPopulationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-WEAK-SIGNAL-POP-001";

    /// <summary>
    /// Percentage of weak signal clients to trigger this recommendation.
    /// </summary>
    private const double WeakSignalPctThreshold = 30;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.Clients.Count == 0)
            return null;

        var clientsWithSignal = ctx.Clients.Where(c => c.Signal.HasValue).ToList();
        if (clientsWithSignal.Count == 0)
            return null;

        var weakClients = clientsWithSignal.Count(c =>
            SignalClassification.IsWeakSignal(c.Signal!.Value, c.Band));
        var weakPct = (double)weakClients / clientsWithSignal.Count * 100;

        if (weakPct < WeakSignalPctThreshold)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality },
            Title = "Significant Weak Signal Population",
            Description = $"{weakClients} clients ({weakPct:F0}% of total) have weak signal for their band. " +
                "This indicates coverage gaps in your deployment.",
            Recommendation = "Review AP placement and consider adding access points in areas with weak coverage.",
            ScoreImpact = -10
        };
    }
}
