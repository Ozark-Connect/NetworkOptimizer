using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// A radio whose clients have good signal and slow service: the AP's own transmit latency toward
/// them is high, or their TCP connections keep stalling. Agent evidence only, from the last hour;
/// a console-only site yields nothing. The latency is measured at the AP, so this is airtime, not
/// the internet.
/// </summary>
public class LatencyDespiteSignalRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-LATENCY-001";

    /// <summary>Covered clients a radio needs before its median means anything.</summary>
    public const int MinClients = 3;

    /// <summary>Median AP transmit latency, in ms, at or above which the radio is slow.</summary>
    public const double HighLatencyMs = 50;

    /// <summary>TCP stalls across the radio's clients in the hour at or above which it is slow.</summary>
    public const int StallFloor = 20;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext context)
    {
        foreach (var ap in context.AccessPoints.Where(a => a.IsOnline))
        {
            foreach (var radio in ap.Radios.Where(r => r.Channel.HasValue))
            {
                var clients = context.Clients
                    .Where(c => c.IsOnline && c.Band == radio.Band
                        && c.ApMac.Equals(ap.Mac, StringComparison.OrdinalIgnoreCase)
                        && c.MeasuredLatencyAvgMs.HasValue && c.Signal.HasValue)
                    .ToList();
                if (clients.Count < MinClients) continue;

                var medianSignal = (int)Math.Round(Median(clients.Select(c => (double)c.Signal!.Value)));
                if (SignalClassification.IsWeakSignal(medianSignal, radio.Band)) continue;

                var medianLatency = Median(clients.Select(c => c.MeasuredLatencyAvgMs!.Value));
                var stalls = clients.Sum(c => c.MeasuredTcpStalls ?? 0);
                if (medianLatency < HighLatencyMs && stalls < StallFloor) continue;

                var bandName = radio.Band.ToDisplayString();
                // Copy: verbiage.md CL-LAT-T / CL-LAT-D / CL-LAT-R.
                yield return new HealthIssue
                {
                    Severity = HealthIssueSeverity.Warning,
                    Dimensions = { HealthDimension.AirtimeEfficiency, HealthDimension.ChannelHealth },
                    Class = HealthIssueClass.Measured,
                    Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(ap.Mac, radio.Band)),
                    Title = $"High Latency Despite Good Signal on {ap.Name}",
                    Description = $"{clients.Count} client(s) on {ap.Name}'s {bandName} radio have strong signal but slow service: " +
                        $"a median transmit latency of {medianLatency:F0} ms at the AP and {stalls} TCP stalls between them in the last hour. " +
                        "Signal is not the problem; airtime is.",
                    AffectedEntity = ap.Name,
                    Recommendation = "Look at what shares this radio's air: co-channel APs on Channels, neighbor networks on RF Environment, and heavy clients on Client Performance.",
                    ScoreImpact = -6
                };
            }
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }
}
