using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when Minimum RSSI is enabled on APs.
/// Min RSSI hard-disconnects clients when signal drops, which can cause issues with sticky clients.
/// </summary>
public class MinRssiEnabledRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-MIN-RSSI-ENABLED-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var apsWithMinRssi = ctx.AccessPoints
            .Where(ap => ap.Radios.Any(r => r.Channel.HasValue && r.MinRssiEnabled))
            .ToList();

        if (apsWithMinRssi.Count == 0)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.RoamingPerformance },
            Title = "Minimum RSSI Enabled",
            Description = $"{apsWithMinRssi.Count} AP(s) have Minimum RSSI enabled. " +
                "This hard-disconnects clients below the threshold, which can help roaming but may cause " +
                "unexpected disconnects with sticky or poorly-behaved clients.",
            AffectedEntity = string.Join(", ", apsWithMinRssi.Select(ap => ap.Name)),
            Recommendation = "If clients are dropping unexpectedly, consider disabling or lowering " +
                "the threshold to -80 dBm. Monitor for complaints about disconnects.",
            ScoreImpact = -2,
            ShowOnOverview = false  // Informational, only relevant to Roaming tab
        };
    }
}
