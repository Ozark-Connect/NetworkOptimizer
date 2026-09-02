using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that warns when radios have high channel utilization (> 70%),
/// which can cause slowdowns for all clients on that radio.
/// On radios the AP Agent measures, the airtime is attributed: mostly the radio's own clients
/// (add capacity) or mostly other transmitters (move channel), each its own issue with its own
/// recommendation. Radios nobody could attribute keep the original issue.
/// </summary>
public class HighRadioUtilizationRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-UTILIZATION-001";

    /// <summary>
    /// Utilization threshold above which to warn (percentage).
    /// </summary>
    private const int UtilizationThreshold = 70;

    /// <summary>Share of the measured airtime one side must hold to be called the cause.</summary>
    private const double DominantSharePct = 60;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var highUtilRadios = ctx.AccessPoints
            .SelectMany(ap => ap.Radios
                .Where(r => r.Channel.HasValue && (r.ChannelUtilization ?? 0) > UtilizationThreshold)
                .Select(r => new { Ap = ap, Radio = r }))
            .ToList();

        if (highUtilRadios.Count == 0)
            yield break;

        // Attribution needs the agent's split, and a total to take the share of.
        bool Attributable(RadioSnapshot r) =>
            r.MeasuredUtilization is > 0 && r.MeasuredSelfAirtime.HasValue && r.MeasuredInterference.HasValue;
        double Share(int? part, int total) => 100.0 * (part ?? 0) / total;

        var ownTraffic = highUtilRadios
            .Where(x => Attributable(x.Radio) && Share(x.Radio.MeasuredSelfAirtime, x.Radio.MeasuredUtilization!.Value) >= DominantSharePct)
            .ToList();
        var interference = highUtilRadios
            .Where(x => Attributable(x.Radio) && Share(x.Radio.MeasuredInterference, x.Radio.MeasuredUtilization!.Value) >= DominantSharePct)
            .Except(ownTraffic)
            .ToList();
        var unattributed = highUtilRadios.Except(ownTraffic).Except(interference).ToList();

        var emitted = 0;
        if (ownTraffic.Count > 0)
        {
            var list = string.Join(", ", ownTraffic.Select(x =>
                $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.MeasuredUtilization}% busy, {x.Radio.MeasuredSelfAirtime}% own)"));
            yield return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.CapacityHeadroom, HealthDimension.ChannelHealth },
                Class = HealthIssueClass.Measured,
                Key = HealthIssueKeys.For(RuleId, "own", HealthIssueKeys.Names(ownTraffic.Select(x => HealthIssueKeys.Radio(x.Ap.Mac, x.Radio.Band)))),
                Title = "High Radio Utilization From Own Clients",
                Description = $"{ownTraffic.Count} radio(s) are busier than {UtilizationThreshold}%, and most of that airtime is their own clients' traffic: {list}.",
                AffectedEntity = string.Join(", ", ownTraffic.Select(x => x.Ap.Name)),
                Recommendation = "Add capacity where these radios are: spread clients across more APs, steer capable clients to 5 GHz or 6 GHz, and check Client Performance for one client doing most of the talking.",
                ScoreImpact = -8
            };
            emitted++;
        }

        if (interference.Count > 0)
        {
            var list = string.Join(", ", interference.Select(x =>
                $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.MeasuredUtilization}% busy, {x.Radio.MeasuredInterference}% from other transmitters)"));
            var description = $"{interference.Count} radio(s) are busier than {UtilizationThreshold}%, and most of that airtime is not theirs: {list}.";
            if (emitted > 0)
                description += " Counted once against the health score with the issue above.";
            yield return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.CapacityHeadroom, HealthDimension.ChannelHealth },
                Class = HealthIssueClass.Measured,
                Key = HealthIssueKeys.For(RuleId, "other", HealthIssueKeys.Names(interference.Select(x => HealthIssueKeys.Radio(x.Ap.Mac, x.Radio.Band)))),
                Title = "High Radio Utilization From Interference",
                Description = description,
                AffectedEntity = string.Join(", ", interference.Select(x => x.Ap.Name)),
                Recommendation = "Move these radios away from the traffic: run the Channel Recommendation on Channels, and check RF Environment for the neighbor networks on their channels.",
                ScoreImpact = emitted > 0 ? 0 : -8
            };
            emitted++;
        }

        if (unattributed.Count == 0)
            yield break;

        var affectedAps = unattributed
            .Select(x => $"{x.Ap.Name} ({x.Radio.Band.ToDisplayString()} {x.Radio.ChannelUtilization}%)")
            .ToList();
        var generic = $"{unattributed.Count} radio(s) have utilization above {UtilizationThreshold}%. " +
            "Clients may experience slow speeds and higher latency during busy periods.";
        if (emitted > 0)
            generic += " Counted once against the health score with the issue above.";

        yield return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.CapacityHeadroom, HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Measured,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Names(unattributed.Select(x => HealthIssueKeys.Radio(x.Ap.Mac, x.Radio.Band)))),
            Title = "High Radio Utilization Detected",
            Description = generic,
            AffectedEntity = string.Join(", ", affectedAps),
            Recommendation = "Consider: (1) spreading clients across more APs, (2) using wider channels (if interference permits), " +
                "or (3) reducing legacy device impact.",
            ScoreImpact = emitted > 0 ? 0 : -8
        };
    }
}
