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

    /// <summary>
    /// Minimum number of clients on a radio before high retries are considered a systemic issue.
    /// A single client with high retries is likely a client-specific problem, not an AP/environment issue.
    /// </summary>
    private const int MinClientsForIssue = 2;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var highRetryRadios = ctx.AccessPoints
            .SelectMany(ap => ap.Radios
                .Where(r => r.Channel.HasValue && r.TxRetriesPct.HasValue && r.TxRetriesPct > RetryThreshold
                    && (r.ClientCount ?? 0) >= MinClientsForIssue)
                .Select(r => new { Ap = ap, Radio = r }))
            .ToList();

        if (highRetryRadios.Count == 0)
            return null;

        var totalClients = highRetryRadios.Sum(x => x.Radio.ClientCount ?? 0);

        var affectedRadios = highRetryRadios
            .Select(x => $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.TxRetriesPct:F1}%, {x.Radio.ClientCount} clients)")
            .ToList();

        // More clients affected = higher severity and score impact
        var severity = totalClients >= 10 ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning;
        var impact = totalClients >= 10 ? -12 : -8;

        return new HealthIssue
        {
            Severity = severity,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
            Title = "High TX Retry Rates",
            Description = $"{highRetryRadios.Count} radio(s) have retry rates above {RetryThreshold}% " +
                $"across {totalClients} clients. " +
                "Retries waste airtime and indicate interference, weak signals, or hidden node problems.",
            AffectedEntity = string.Join(", ", affectedRadios),
            Recommendation = "Check for sources of interference, ensure APs are on non-overlapping channels, " +
                "and verify client signal strength is adequate (-70 dBm or better).",
            ScoreImpact = impact
        };
    }
}
