using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when radios have high TX retry rates, which indicate interference, weak
/// signals, or hidden node problems. The bar is per band: 2.4 GHz retries in the high teens on a
/// healthy night, and a 6 GHz radio at the same rate is already in trouble. On an agent-covered
/// radio the measured airtime split says which kind of retry it is.
/// </summary>
public class HighTxRetryRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-TX-RETRY-001";

    /// <summary>Retry percent at which a band's radio is raised, and at which it is critical.</summary>
    public static (double Warning, double Critical) Thresholds(RadioBand band) => band switch
    {
        RadioBand.Band2_4GHz => (25, 40),
        RadioBand.Band6GHz => (10, 20),
        _ => (15, 30)
    };

    /// <summary>
    /// Minimum number of clients on a radio before high retries are considered a systemic issue.
    /// Two clients cannot tell a radio problem from one bad device.
    /// </summary>
    private const int MinClientsForIssue = 3;

    /// <summary>Clients across the raised radios at or above which the issue is critical regardless of rate.</summary>
    private const int CriticalClientCount = 10;

    /// <summary>Measured airtime from other transmitters at or above which retries are contention.</summary>
    private const int ContentionAirtimePct = 20;

    /// <summary>Measured busy airtime at or under which the air is quiet, so retries are not contention.</summary>
    private const int QuietAirtimePct = 25;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        var highRetryRadios = ctx.AccessPoints
            .SelectMany(ap => ap.Radios
                .Where(r => r.Channel.HasValue && r.TxRetriesPct.HasValue && r.TxRetriesPct > Thresholds(r.Band).Warning
                    && (r.ClientCount ?? 0) >= MinClientsForIssue)
                .Select(r => new { Ap = ap, Radio = r }))
            .ToList();

        if (highRetryRadios.Count == 0)
            return null;

        var totalClients = highRetryRadios.Sum(x => x.Radio.ClientCount ?? 0);
        var anyCritical = highRetryRadios.Any(x => x.Radio.TxRetriesPct >= Thresholds(x.Radio.Band).Critical);

        var affectedRadios = highRetryRadios
            .Select(x => $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.TxRetriesPct:F1}%, {x.Radio.ClientCount} clients, threshold {Thresholds(x.Radio.Band).Warning:F0}%)")
            .ToList();

        // Where the agent measured the air, the retries have a cause: other transmitters holding
        // the airtime is contention, quiet air is signal or a hidden node.
        var attribution = string.Concat(highRetryRadios.Select(x =>
        {
            var r = x.Radio;
            var bandName = r.Band.ToDisplayString();
            if (r.MeasuredInterference is { } interf && interf >= ContentionAirtimePct)
                return $" {x.Ap.Name} {bandName}: other transmitters hold {interf}% of the airtime, so these retries are contention; a channel move is the fix.";
            if (r.MeasuredUtilization is { } busy && busy <= QuietAirtimePct)
                return $" {x.Ap.Name} {bandName}: the air is quiet ({busy}% busy), so these retries are weak signal or a hidden node, not contention.";
            return string.Empty;
        }));

        var critical = anyCritical || totalClients >= CriticalClientCount;

        return new HealthIssue
        {
            Severity = critical ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
            Title = "High TX Retry Rates",
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Names(highRetryRadios.Select(x => HealthIssueKeys.Radio(x.Ap.Mac, x.Radio.Band)))),
            Description = $"{highRetryRadios.Count} radio(s) have retry rates above their band's threshold " +
                $"across {totalClients} clients. " +
                "Retries waste airtime and indicate interference, weak signals, or hidden node problems." + attribution,
            AffectedEntity = string.Join(", ", affectedRadios),
            Recommendation = "Check for sources of interference, ensure APs are on non-overlapping channels, " +
                "and verify client signal strength is adequate (-70 dBm or better).",
            ScoreImpact = critical ? -12 : -8
        };
    }
}
