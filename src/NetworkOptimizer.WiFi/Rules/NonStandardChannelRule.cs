using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that detects 2.4 GHz APs using non-standard channels (not 1, 6, or 11).
/// Non-standard channels overlap with adjacent channels causing interference.
/// </summary>
public class NonStandardChannelRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-NONSTANDARD-CHANNEL-001";

    private static readonly HashSet<int> StandardChannels = new() { 1, 6, 11 };

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var radiosOn24GHz = ctx.AccessPoints
            .SelectMany(ap => ap.Radios.Where(r => r.Band == RadioBand.Band2_4GHz && r.Channel.HasValue))
            .ToList();

        var nonStandardChannels = radiosOn24GHz
            .Where(r => !StandardChannels.Contains(r.Channel!.Value))
            .GroupBy(r => r.Channel!.Value)
            .ToList();

        foreach (var group in nonStandardChannels)
        {
            var apNames = ctx.AccessPoints
                .Where(ap => ap.Radios.Any(r => r.Band == RadioBand.Band2_4GHz && r.Channel == group.Key))
                .Select(ap => ap.Name)
                .ToList();

            yield return new HealthIssue
            {
                Severity = HealthIssueSeverity.Info,
                Dimensions = { HealthDimension.ChannelHealth },
                Title = $"Non-Standard 2.4 GHz Channel {group.Key}",
                Description = $"APs ({string.Join(", ", apNames)}) are using channel {group.Key}, which overlaps with adjacent channels.",
                Recommendation = "For best performance, use only channels 1, 6, or 11 on 2.4 GHz to avoid overlap.",
                ScoreImpact = -2
            };
        }
    }

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx)
    {
        // Use EvaluateAll for multi-issue rules
        return null;
    }
}
