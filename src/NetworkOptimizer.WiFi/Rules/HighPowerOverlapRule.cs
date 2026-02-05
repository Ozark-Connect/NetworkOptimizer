using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects when multiple non-mesh APs on the same channel
/// are all using high TX power, which causes excessive co-channel interference.
/// </summary>
public class HighPowerOverlapRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-HIGH-POWER-OVERLAP-001";

    /// <summary>
    /// TX power threshold in dBm above which is considered "high".
    /// </summary>
    private const int HighPowerThreshold = 23;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };

        foreach (var band in bands)
        {
            // Group radios by channel
            var radiosByChannel = ctx.AccessPoints
                .SelectMany(ap => ap.Radios
                    .Where(r => r.Band == band && r.Channel.HasValue)
                    .Select(r => new { Ap = ap, Radio = r }))
                .GroupBy(x => x.Radio.Channel!.Value)
                .Where(g => g.Count() > 1);

            foreach (var group in radiosByChannel)
            {
                var channel = group.Key;
                var apsOnChannel = group.Select(x => x.Ap).Distinct().ToList();

                // Filter out mesh pairs - they MUST be on the same channel
                var nonMeshAps = WiFiAnalysisHelpers.FilterOutMeshPairs(apsOnChannel, band, channel);
                if (nonMeshAps.Count < 2)
                    continue;

                // Check if multiple non-mesh APs have high power
                var highPowerAps = nonMeshAps
                    .Where(ap => ap.Radios.Any(r =>
                        r.Band == band &&
                        r.Channel == channel &&
                        (r.TxPowerMode?.Equals("high", StringComparison.OrdinalIgnoreCase) == true ||
                         (r.TxPower.HasValue && r.TxPower >= HighPowerThreshold))))
                    .ToList();

                if (highPowerAps.Count > 1)
                {
                    yield return new HealthIssue
                    {
                        Severity = HealthIssueSeverity.Warning,
                        Dimensions = { HealthDimension.SignalQuality, HealthDimension.ChannelHealth },
                        Title = $"High Power Overlap on {band.ToDisplayString()} Channel {channel}",
                        Description = $"{string.Join(", ", highPowerAps.Select(x => x.Name))} are all using high TX power on the same channel, which may cause co-channel interference.",
                        Recommendation = "Consider reducing TX power on some APs or changing channels to reduce overlap.",
                        ScoreImpact = -5
                    };
                }
            }
        }
    }

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Use EvaluateAll for multi-issue rules
        return null;
    }
}
