using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// A radio whose measured noise floor has sat well above its band's other radios for an hour
/// has a transmitter near it. Only the AP Agent measures the floor, so the rule sees nothing
/// on a site without agents and compares only within the covered radios on a band.
/// </summary>
public class RaisedNoiseFloorRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-NOISE-FLOOR-001";

    /// <summary>A radio must sit this far above the band's reference to be reported.</summary>
    public const int RaisedFloorDeltaDb = 8;

    /// <summary>And at or above this absolute level, so a whole quiet band is never reported against itself.</summary>
    public const int RaisedFloorAbsoluteDbm = -85;

    /// <summary>Covered radios on a band before there is something to compare against.</summary>
    private const int MinRadiosForReference = 2;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext ctx)
    {
        var bands = new[] { RadioBand.Band2_4GHz, RadioBand.Band5GHz, RadioBand.Band6GHz };
        foreach (var band in bands)
        {
            // The hour's median, not the latest sample: a raised floor is a claim about an hour.
            var measured = ctx.AccessPoints
                .SelectMany(ap => ap.Radios
                    .Where(r => r.Band == band && r.Channel.HasValue && r.MeasuredNoiseFloorHour.HasValue)
                    .Select(r => (Ap: ap, Radio: r, Floor: r.MeasuredNoiseFloorHour!.Value)))
                .ToList();
            if (measured.Count < MinRadiosForReference) continue;

            var sorted = measured.Select(m => m.Floor).OrderBy(f => f).ToList();
            var reference = sorted[sorted.Count / 2];

            foreach (var (ap, radio, floor) in measured)
            {
                var delta = floor - reference;
                if (delta < RaisedFloorDeltaDb || floor < RaisedFloorAbsoluteDbm) continue;

                var bandName = band.ToDisplayString();
                // Copy: verbiage.md NF-1-*.
                yield return new HealthIssue
                {
                    Severity = HealthIssueSeverity.Warning,
                    Dimensions = { HealthDimension.ChannelHealth, HealthDimension.SignalQuality },
                    Class = HealthIssueClass.Measured,
                    Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(ap.Mac, band)),
                    Title = $"Raised Noise Floor on {bandName}: {ap.Name}",
                    Description = $"{ap.Name}'s {bandName} radio has measured a noise floor of {floor} dBm over the last hour, " +
                        $"{delta} dB above the other {bandName} radios on this site ({reference} dBm). " +
                        "Something near it is transmitting on or next to its channel.",
                    AffectedEntity = ap.Name,
                    Recommendation = $"Look for a transmitter near {ap.Name}: another AP on an adjacent channel at high power, " +
                        "a non-Wi-Fi device (cameras, baby monitors, and microwave ovens on 2.4 GHz), or a neighbor's network in RF Environment. " +
                        "Moving the radio to a different part of the band is the quickest test.",
                    ScoreImpact = -4,
                    AffectedChannels = { radio.Channel!.Value }
                };
            }
        }
    }
}
