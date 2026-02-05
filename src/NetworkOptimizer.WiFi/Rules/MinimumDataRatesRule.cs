using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that recommends setting minimum data rates when many legacy devices
/// are on 2.4 GHz, to prevent very slow transmissions from consuming excessive airtime.
/// </summary>
public class MinimumDataRatesRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-MIN-DATA-RATES-001";

    /// <summary>
    /// Minimum number of legacy clients on 2.4 GHz to trigger this recommendation.
    /// </summary>
    private const int MinLegacyOn2gThreshold = 5;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Count legacy clients (Wi-Fi 4 or lower) on 2.4 GHz
        var legacyOn2g = ctx.Clients.Count(c =>
            c.Band == RadioBand.Band2_4GHz &&
            c.WifiGeneration.HasValue &&
            c.WifiGeneration <= 4);

        if (legacyOn2g < MinLegacyOn2gThreshold)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.AirtimeEfficiency },
            Title = "Consider Minimum Data Rates",
            Description = "Setting minimum data rates can prevent very slow legacy transmissions from " +
                "consuming excessive airtime, at the cost of reduced range for legacy devices.",
            Recommendation = "In UniFi Network: Settings > WiFi > (SSID) > Advanced > Minimum Data Rate - " +
                "try 12 Mbps for 2.4 GHz to block very slow rates.",
            ScoreImpact = -3,
            ShowOnOverview = false  // Informational, only relevant to Airtime tab
        };
    }
}
