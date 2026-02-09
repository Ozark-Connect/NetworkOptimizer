using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when radios have high TX retry rates (> 15%),
/// which indicates interference, weak signals, or hidden node problems.
/// </summary>
public class HighTxRetryRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-TX-RETRY-001";

    /// <summary>
    /// TX retry percentage threshold above which to warn.
    /// </summary>
    private const double RetryThreshold = 15;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var highRetryRadios = ctx.AccessPoints
            .SelectMany(ap => ap.Radios
                .Where(r => r.Channel.HasValue && r.TxRetriesPct.HasValue && r.TxRetriesPct > RetryThreshold)
                .Select(r => new { Ap = ap, Radio = r }))
            .ToList();

        if (highRetryRadios.Count == 0)
            return null;

        var affectedRadios = highRetryRadios
            .Select(x => $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.TxRetriesPct:F1}%)")
            .ToList();

        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
            Title = "High TX Retry Rates",
            Description = $"{highRetryRadios.Count} radio(s) have retry rates above {RetryThreshold}%. " +
                "Retries waste airtime and indicate interference, weak signals, or hidden node problems.",
            AffectedEntity = string.Join(", ", affectedRadios),
            Recommendation = "Check for sources of interference, ensure APs are on non-overlapping channels, " +
                "and verify client signal strength is adequate (-70 dBm or better).",
            ScoreImpact = -8
        };
    }
}
