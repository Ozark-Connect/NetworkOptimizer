using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when radios have high channel utilization (> 70%),
/// which can cause slowdowns for all clients on that radio.
/// </summary>
public class HighRadioUtilizationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-UTILIZATION-001";

    /// <summary>
    /// Utilization threshold above which to warn (percentage).
    /// </summary>
    private const int UtilizationThreshold = 70;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var highUtilRadios = ctx.AccessPoints
            .SelectMany(ap => ap.Radios
                .Where(r => r.Channel.HasValue && (r.ChannelUtilization ?? 0) > UtilizationThreshold)
                .Select(r => new { Ap = ap, Radio = r }))
            .ToList();

        if (highUtilRadios.Count == 0)
            return null;

        var affectedAps = highUtilRadios
            .Select(x => $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.ChannelUtilization}%)")
            .ToList();

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.CapacityHeadroom },
            Title = "High Radio Utilization Detected",
            Description = $"{highUtilRadios.Count} radio(s) have utilization above {UtilizationThreshold}%. " +
                "Clients may experience slow speeds and higher latency during busy periods.",
            AffectedEntity = string.Join(", ", affectedAps),
            Recommendation = "Consider: (1) spreading clients across more APs, (2) using wider channels (if interference permits), " +
                "or (3) reducing legacy device impact.",
            ScoreImpact = -8
        };
    }
}
