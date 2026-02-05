using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends enabling Roaming Assistant on 5 GHz radios.
/// Unlike Minimum RSSI, Roaming Assistant uses BSS transition frames (soft nudge)
/// instead of hard-disconnecting clients.
/// </summary>
public class RoamingAssistantRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-ROAMING-ASSISTANT-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only relevant for multi-AP deployments
        if (ctx.AccessPoints.Count <= 1)
            return null;

        var apsWithout5gRoamingAssistant = ctx.AccessPoints
            .Where(ap => ap.Radios.Any(r =>
                r.Band == RadioBand.Band5GHz &&
                r.Channel.HasValue &&
                !r.RoamingAssistantEnabled))
            .ToList();

        if (apsWithout5gRoamingAssistant.Count == 0)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.RoamingPerformance },
            Title = "Enable Roaming Assistant (Recommended)",
            Description = $"{apsWithout5gRoamingAssistant.Count} AP(s) don't have Roaming Assistant enabled on 5 GHz. " +
                "Unlike Minimum RSSI, this uses BSS transition frames (soft nudge) instead of hard-disconnecting clients.",
            AffectedEntity = string.Join(", ", apsWithout5gRoamingAssistant.Select(ap => ap.Name)),
            Recommendation = "Per AP: Devices > (AP) > Settings > Radios > 5 GHz > Roaming Assistant. " +
                "Or globally: Settings > WiFi > 5 GHz Roaming Assistant with 'Override All APs'. " +
                "Recommended threshold: -70 to -75 dBm.",
            ScoreImpact = -3,
            ShowOnOverview = false  // Informational, only relevant to Roaming tab
        };
    }
}
