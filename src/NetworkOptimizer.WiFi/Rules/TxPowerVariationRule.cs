using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends varied TX power levels in multi-AP deployments.
/// All APs on high power can cause interference and poor roaming.
/// </summary>
public class TxPowerVariationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-TX-POWER-VARIATION-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only relevant for multi-AP deployments
        if (ctx.AccessPoints.Count <= 1)
            return null;

        var powerModes = ctx.AccessPoints
            .SelectMany(ap => ap.Radios.Where(r => r.Channel.HasValue))
            .Select(r => r.TxPowerMode?.ToLowerInvariant() ?? "auto")
            .Distinct()
            .ToList();

        // Only flag if ALL radios across ALL APs are on high power
        if (powerModes.Count != 1 || powerModes[0] != "high")
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.ChannelHealth, HealthDimension.RoamingPerformance },
            Title = "Consider Varied TX Power Levels",
            Description = "All APs are set to high power. In multi-AP deployments, using 'Auto' or varied " +
                "power levels often improves roaming behavior and reduces co-channel interference.",
            Recommendation = "In UniFi Network: Settings > WiFi > (SSID) > Advanced > TX Power - " +
                "try 'Auto' to let the controller optimize power levels.",
            ScoreImpact = -3,
            ShowOnOverview = false  // Informational, only relevant to Channel/Roaming tabs
        };
    }
}
