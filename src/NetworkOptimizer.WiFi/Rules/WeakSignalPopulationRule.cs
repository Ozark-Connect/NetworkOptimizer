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

    /// <summary>Below this many clients a share is noise: 2 weak of 5 is two phones, not a deployment problem.</summary>
    internal const int MinClients = 10;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var clientsWithSignal = ctx.Clients.Where(c => c.Signal.HasValue).ToList();
        if (clientsWithSignal.Count < MinClients)
            return null;

        var weak = clientsWithSignal
            .Where(c => SignalClassification.IsWeakSignal(c.Signal!.Value, c.Band))
            .ToList();
        var weakClients = weak.Count;
        var weakPct = (double)weakClients / clientsWithSignal.Count * 100;

        if (weakPct < WeakSignalPctThreshold)
            return null;

        // Coverage Gap already names the APs; this issue adds something only when weak clients are elsewhere too.
        var gapMacs = CoverageGapRule.GapAps(ctx).Select(x => x.Ap.Mac).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (weak.All(c => gapMacs.Contains(c.ApMac)))
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality },
            Title = "Significant Weak Signal Population",
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId),
            Description = $"{weakClients} clients ({weakPct:F0}% of total) have weak signal for their band. " +
                "This indicates coverage gaps in your deployment.",
            Recommendation = "Review AP placement and consider adding access points in areas with weak coverage.",
            ScoreImpact = -10
        };
    }
}
