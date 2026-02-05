using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when legacy clients are consuming disproportionate airtime.
/// Legacy devices use slower modulation rates, taking 5-10x longer to transmit the same data.
/// </summary>
public class LegacyClientAirtimeRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-LEGACY-AIRTIME-001";

    /// <summary>
    /// Minimum number of legacy clients to trigger this warning.
    /// </summary>
    private const int MinLegacyClientsThreshold = 3;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.Clients.Count == 0)
            return null;

        // Legacy clients: Wi-Fi 4 or lower, or 2.4GHz only with no 5GHz support
        var legacyClients = ctx.Clients
            .Where(c =>
                (c.WifiGeneration.HasValue && c.WifiGeneration <= 4) ||
                (c.Band == RadioBand.Band2_4GHz && !c.Capabilities.Supports5GHz))
            .ToList();

        if (legacyClients.Count < MinLegacyClientsThreshold)
            return null;

        var legacyPct = (double)legacyClients.Count / ctx.Clients.Count * 100;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.BandSteering },
            Title = "Legacy Client Airtime Impact",
            Description = $"{legacyClients.Count} legacy clients ({legacyPct:F0}% of total) are consuming " +
                "disproportionate airtime. Legacy devices use slower modulation rates, taking 5-10x longer " +
                "to transmit the same data.",
            Recommendation = "Increase the minimum data rate on 2.4 GHz (e.g., 12 Mbps) to force higher modulation. " +
                "Note: very old devices may disconnect if they can't maintain the rate.",
            ScoreImpact = -8
        };
    }
}
