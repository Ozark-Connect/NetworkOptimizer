using NetworkOptimizer.Core.Helpers;
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
            .Select(x => new IssueLink(
                $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.TxRetriesPct:F1}%, {x.Radio.ClientCount} clients, threshold {Thresholds(x.Radio.Band).Warning:F0}%)",
                IssueLinks.Clients(x.Ap.Mac, x.Radio.Band)))
            .ToList();

        // Where the agent measured the air, the retries have a cause: other transmitters holding
        // the airtime is contention, quiet air is signal or a hidden node.
        var air = highRetryRadios.Select(x => AirOf(x.Radio)).ToList();
        var attribution = string.Concat(highRetryRadios.Select((x, i) =>
        {
            var r = x.Radio;
            var bandName = r.Band.ToDisplayString();
            return air[i] switch
            {
                Air.Busy => $" {x.Ap.Name} {bandName}: other devices use {r.MeasuredInterference}% of this channel's airtime.",
                Air.Quiet => $" {x.Ap.Name} {bandName}: the channel is quiet ({r.MeasuredUtilization}% busy), so the cause is weak signal, or clients too far apart to hear each other that transmit at the same time.",
                _ => string.Empty
            };
        }));

        // The general causes are said only when some radio has no measured cause: said beside a
        // measured one, they repeat it.
        var count = highRetryRadios.Count;
        var description = $"{StringUtilities.Pluralize(count, "radio")} {(count == 1 ? "has" : "have")} more retries than normal " +
            $"for {(count == 1 ? "its" : "their")} band, across {StringUtilities.Pluralize(totalClients, "client")}. Retries waste airtime.";
        if (air.Any(a => a == Air.Unknown))
            description += " The usual causes are interference, weak signal, or clients too far apart to hear each other, " +
                "which then transmit at the same time.";
        description += attribution;

        var critical = anyCritical || totalClients >= CriticalClientCount;

        return new HealthIssue
        {
            Severity = critical ? HealthIssueSeverity.Critical : HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
            Title = "High TX Retry Rates",
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Names(highRetryRadios.Select(x => HealthIssueKeys.Radio(x.Ap.Mac, x.Radio.Band)))),
            Description = description,
            Recommendation = Recommendation(air),
            ScoreImpact = critical ? -12 : -8
        }.WithLinks(affectedRadios);
    }

    private enum Air { Unknown, Busy, Quiet }

    private static Air AirOf(RadioSnapshot r) =>
        r.MeasuredInterference is { } interf && interf >= ContentionAirtimePct ? Air.Busy
        : r.MeasuredUtilization is { } busy && busy <= QuietAirtimePct ? Air.Quiet
        : Air.Unknown;

    /// <summary>Advice that follows the measured cause when every radio shares one, else the general checks.</summary>
    private static string Recommendation(IReadOnlyList<Air> air)
    {
        var radios = air.Count == 1 ? "this radio" : "these radios";
        if (air.All(a => a == Air.Quiet))
            return $"Check the weak-signal clients on {radios} first. If their signal is fine, add an AP closer to them.";
        if (air.All(a => a == Air.Busy))
            return $"Move {radios} to {(air.Count == 1 ? "a quieter channel" : "quieter channels")}: run the Channel Recommendation on Channels.";
        return "Check these radios for interference and overlapping channels, and address clients with weak signal.";
    }
}
