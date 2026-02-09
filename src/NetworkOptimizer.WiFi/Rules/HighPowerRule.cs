using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when APs have all radios set to high TX power,
/// which can cause excessive coverage overlap and interference.
/// </summary>
public class HighPowerRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-POWER-001";

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Find APs where ALL active radios are on high power
        var highPowerAps = ctx.AccessPoints
            .Where(ap =>
            {
                var activeRadios = ap.Radios.Where(r => r.Channel.HasValue).ToList();
                if (activeRadios.Count == 0) return false;

                var highPowerRadios = activeRadios.Where(r =>
                    r.TxPowerMode?.Equals("high", StringComparison.OrdinalIgnoreCase) == true).ToList();

                return highPowerRadios.Count == activeRadios.Count;
            })
            .ToList();

        if (highPowerAps.Count == 0)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.ChannelHealth, HealthDimension.SignalQuality },
            Title = highPowerAps.Count == 1
                ? $"All Radios on High Power: {highPowerAps[0].Name}"
                : $"{highPowerAps.Count} APs with All Radios on High Power",
            Description = highPowerAps.Count == 1
                ? $"{highPowerAps[0].Name} has all radios set to high TX power, which can cause excessive coverage overlap and interference with neighboring APs."
                : $"{highPowerAps.Count} access points have all radios set to high TX power. This can cause excessive coverage overlap and co-channel interference.",
            AffectedEntity = string.Join(", ", highPowerAps.Select(ap => ap.Name)),
            Recommendation = "In UniFi Network: Settings > WiFi > (SSID) > Advanced > TX Power - " +
                (WiFiAnalysisHelpers.SupportsAutoPowerLeveling
                    ? "consider 'Medium' or 'Auto' for balanced coverage."
                    : "consider 'Medium' for balanced coverage."),
            ScoreImpact = -5 * highPowerAps.Count
        };
    }
}
