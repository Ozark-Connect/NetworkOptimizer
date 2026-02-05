using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects APs with a high percentage of weak-signal clients,
/// indicating coverage gaps near those APs.
/// </summary>
public class CoverageGapRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-COVERAGE-GAP-001";

    /// <summary>
    /// Minimum clients on an AP to evaluate (avoid flagging APs with few clients).
    /// </summary>
    private const int MinClientsThreshold = 3;

    /// <summary>
    /// Percentage of weak signal clients to trigger this recommendation.
    /// </summary>
    private const double WeakSignalPctThreshold = 50;

    /// <summary>
    /// Signal strength below which a client is considered "weak".
    /// </summary>
    private const int WeakSignalThreshold = -70;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var coverageGapAps = new List<(AccessPointSnapshot Ap, int ClientCount, int WeakCount, double WeakPct)>();

        foreach (var ap in ctx.AccessPoints)
        {
            var apClients = ctx.Clients.Where(c => c.ApMac == ap.Mac).ToList();
            if (apClients.Count < MinClientsThreshold)
                continue;

            var clientsWithSignal = apClients.Where(c => c.Signal.HasValue).ToList();
            if (clientsWithSignal.Count == 0)
                continue;

            var weakCount = clientsWithSignal.Count(c => c.Signal < WeakSignalThreshold);
            var weakPct = (double)weakCount / clientsWithSignal.Count * 100;

            if (weakPct >= WeakSignalPctThreshold)
            {
                coverageGapAps.Add((ap, apClients.Count, weakCount, weakPct));
            }
        }

        if (coverageGapAps.Count == 0)
            return null;

        if (coverageGapAps.Count == 1)
        {
            var (ap, clientCount, weakCount, weakPct) = coverageGapAps[0];
            return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.SignalQuality },
                Title = $"Coverage Gap Near {ap.Name}",
                Description = $"{weakPct:F0}% of clients ({weakCount} of {clientCount}) connected to {ap.Name} have weak signal " +
                    $"(<{WeakSignalThreshold} dBm). These clients may be too far from the AP or experiencing obstruction.",
                AffectedEntity = ap.Name,
                Recommendation = "Consider: (1) increasing TX power on this AP, (2) adding an AP closer to weak clients, " +
                    "or (3) checking for physical obstructions.",
                ScoreImpact = -8
            };
        }

        // Multiple APs with coverage gaps
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality },
            Title = $"Coverage Gaps Near {coverageGapAps.Count} APs",
            Description = $"{coverageGapAps.Count} access points have >=50% of clients with weak signal (<{WeakSignalThreshold} dBm). " +
                "This indicates significant coverage gaps in your deployment.",
            AffectedEntity = string.Join(", ", coverageGapAps.Select(x => $"{x.Ap.Name} ({x.WeakPct:F0}%)")),
            Recommendation = "Review AP placement and consider increasing TX power or adding APs in areas with weak coverage.",
            ScoreImpact = -8 * coverageGapAps.Count
        };
    }
}
