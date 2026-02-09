using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects co-channel interference where multiple non-mesh APs
/// are using the same channel on the same band.
/// </summary>
public class CoChannelInterferenceRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-COCHANNEL-001";

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };

        foreach (var band in bands)
        {
            var radiosInBand = ctx.AccessPoints
                .SelectMany(ap => ap.Radios.Where(r => r.Band == band && r.Channel.HasValue))
                .ToList();

            var channelGroups = radiosInBand.GroupBy(r => r.Channel!.Value).Where(g => g.Count() > 1).ToList();

            foreach (var group in channelGroups)
            {
                var apsOnChannel = ctx.AccessPoints
                    .Where(ap => ap.Radios.Any(r => r.Band == band && r.Channel == group.Key))
                    .ToList();

                // Filter out mesh pairs - they MUST be on the same channel
                var nonMeshAps = WiFiAnalysisHelpers.FilterOutMeshPairs(apsOnChannel, band, group.Key);

                // Only report co-channel if there are 2+ APs that aren't mesh pairs
                if (nonMeshAps.Count > 1)
                {
                    var apNames = nonMeshAps.Select(ap => ap.Name).ToList();

                    yield return new HealthIssue
                    {
                        Severity = HealthIssueSeverity.Warning,
                        Dimensions = { HealthDimension.ChannelHealth },
                        Title = $"Co-Channel Interference on {band.ToDisplayString()} Channel {group.Key}",
                        Description = $"{nonMeshAps.Count} APs ({string.Join(", ", apNames)}) are using the same channel.",
                        Recommendation = "Consider changing one or more APs to a different channel to reduce interference.",
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
