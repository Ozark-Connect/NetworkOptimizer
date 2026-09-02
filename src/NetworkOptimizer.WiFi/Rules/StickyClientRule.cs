using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Clients that joined an AP at a weak signal and have sat there for hours while another AP on the
/// band was there to go to. Agent evidence only: the join signal and the association age come from
/// the AP Agent, so a console-only site yields nothing. Reports the clients the roaming
/// configuration would have helped; the configuration rules themselves are unchanged.
/// </summary>
public class StickyClientRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-STICKY-CLIENT-001";

    /// <summary>How long a client must have stayed before it is sticky rather than arriving.</summary>
    public static readonly TimeSpan MinAssociation = TimeSpan.FromHours(2);

    /// <summary>Sticky clients an AP needs before the issue is raised.</summary>
    public const int MinStickyClients = 2;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext context)
    {
        var online = context.AccessPoints.Where(ap => ap.IsOnline).ToList();
        var bandsByAp = online.ToDictionary(
            ap => ap.Mac.ToLowerInvariant(),
            ap => ap.Radios.Where(r => r.Channel.HasValue).Select(r => r.Band).ToHashSet());

        foreach (var ap in online)
        {
            var apMac = ap.Mac.ToLowerInvariant();
            var sticky = context.Clients
                .Where(c => c.IsOnline
                    && c.ApMac.Equals(ap.Mac, StringComparison.OrdinalIgnoreCase)
                    && c.JoinSignal is { } join && c.Signal is { } now
                    && c.AssociatedFor is { } age && age >= MinAssociation
                    && SignalClassification.IsWeakSignal(join, c.Band)
                    && SignalClassification.IsWeakSignal(now, c.Band)
                    && bandsByAp.Any(kv => kv.Key != apMac && kv.Value.Contains(c.Band)))
                .OrderBy(c => c.Signal)
                .ToList();
            if (sticky.Count < MinStickyClients) continue;

            var hours = (int)Math.Floor(sticky.Min(c => c.AssociatedFor!.Value).TotalHours);
            var list = string.Join(", ", sticky.Select(c =>
                $"{(string.IsNullOrEmpty(c.Name) ? c.Mac : c.Name)} (joined at {c.JoinSignal} dBm, now {c.Signal} dBm)"));
            var ignored = sticky.Count(c => c.RoamNudges is > 0 && c.RoamNudgesAccepted is 0);

            var description = $"{sticky.Count} client(s) joined {ap.Name} at a weak signal and have stayed for over {hours} hour(s) without roaming: {list}.";
            if (ignored > 0)
                description += $" {ignored} of them ignored a roam nudge (BSS transition request).";

            yield return new HealthIssue
            {
                Severity = HealthIssueSeverity.Warning,
                Dimensions = { HealthDimension.RoamingPerformance, HealthDimension.SignalQuality },
                Class = HealthIssueClass.Measured,
                Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Macs(new[] { ap.Mac })),
                Title = $"Sticky Clients on {ap.Name}",
                Description = description,
                AffectedEntity = ap.Name,
                Recommendation = $"These clients need a push. Enable Roaming Assistant on the SSID with a threshold around -72 dBm, and if they still ignore the nudge, set Minimum RSSI on {ap.Name} so it disconnects them at -80 dBm.",
                ScoreImpact = -4
            };
        }
    }
}
