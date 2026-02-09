using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends considering Minimum RSSI for roaming.
/// Only triggers when there are steerable clients and no APs have Min RSSI enabled.
/// </summary>
public class MinRssiRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-MIN-RSSI-001";

    /// <summary>
    /// Minimum number of steerable clients to trigger this recommendation.
    /// </summary>
    private const int MinSteerableClientsThreshold = 5;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Only applies if there are enough steerable clients
        if (ctx.SteerableClients.Count <= MinSteerableClientsThreshold)
            return null;

        // Check if any AP has 5 GHz or 6 GHz coverage
        var has5gOr6g = ctx.AccessPoints.Any(ap => ap.Radios.Any(r =>
            (r.Band == RadioBand.Band5GHz || r.Band == RadioBand.Band6GHz) && r.Channel.HasValue));

        if (!has5gOr6g)
            return null;

        // Check if any AP already has Minimum RSSI enabled
        var hasMinRssi = ctx.AccessPoints.Any(ap => ap.Radios.Any(r => r.MinRssiEnabled));

        if (hasMinRssi)
            return null; // Already configured

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.RoamingPerformance, HealthDimension.ChannelHealth },
            Title = "Consider Minimum RSSI (With Caution)",
            Description = "Minimum RSSI can help sticky clients roam by hard-disconnecting them when signal drops. " +
                "Use cautiously as it can cause issues with some clients.",
            Recommendation = "In UniFi Network: Devices > (AP) > Settings > Radios > Minimum RSSI (per band). " +
                "Use a conservative threshold like -75 to -80 dBm. Consider setting lower (e.g., -80 dBm) for perimeter APs.",
            ScoreImpact = -3,
            ShowOnOverview = false  // Informational, only relevant to Roaming tab
        };
    }
}
