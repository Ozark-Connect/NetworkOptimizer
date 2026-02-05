using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that flags when too many clients are on 2.4 GHz even though some
/// support higher bands. This indicates band steering isn't effective.
/// </summary>
public class High2GHzConcentrationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-2GHZ-CONCENTRATION-001";

    /// <summary>
    /// Minimum percentage of clients on 2.4 GHz to trigger this recommendation.
    /// </summary>
    private const double Min2GHzPctThreshold = 50;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        if (ctx.Clients.Count == 0)
            return null;

        var clientsOn2g = ctx.Clients.Count(c => c.Band == RadioBand.Band2_4GHz);
        var pct2g = (double)clientsOn2g / ctx.Clients.Count * 100;

        // Only flag if >50% on 2.4GHz AND some are steerable
        if (pct2g <= Min2GHzPctThreshold || ctx.SteerableClients.Count == 0)
            return null;

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.ChannelHealth },
            Title = "High 2.4 GHz Concentration",
            Description = $"{pct2g:F0}% of clients are on 2.4 GHz, but {ctx.SteerableClients.Count} of them " +
                "support higher bands. This leads to congestion and slower speeds on 2.4 GHz.",
            Recommendation = "Consider: (1) enabling band steering, (2) reducing 2.4 GHz TX power to " +
                "encourage clients to use 5 GHz, or (3) creating a separate IoT SSID for 2.4 GHz-only devices.",
            ScoreImpact = -10
        };
    }
}
