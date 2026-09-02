using NetworkOptimizer.WiFi.Helpers;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that flags wide channel widths on 5 GHz and 6 GHz radios.
/// - 6 GHz 320 MHz: always suggest 160 MHz (better client performance + AP co-channel separation)
/// - 5 GHz >= 160 MHz with weak-signal clients: suggest narrowing to 80 MHz
/// 6 GHz 160 MHz is not flagged (less co-channel interference than 5 GHz).
/// A width the configuration shows was chosen (a per-AP override, or a band carrying a mesh
/// backhaul) is reported at Info with a hint rather than as a correction.
/// </summary>
public class WideChannelWidthRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-WIDE-CHANNEL-WIDTH-001";

    private const double WeakClientPctThreshold = 35;
    private const int MinClientsForSignalCheck = 3;

    public HealthIssue? Evaluate(WiFiOptimizerContext ctx) => null;

    public IEnumerable<HealthIssue> EvaluateAll(WiFiOptimizerContext context)
    {
        var clientsByApBand = context.Clients
            .Where(c => c.IsOnline && c.Signal.HasValue)
            .GroupBy(c => (ApMac: c.ApMac.ToLowerInvariant(), c.Band))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var ap in context.AccessPoints)
        {
            foreach (var radio in ap.Radios.Where(r => r.Channel.HasValue))
            {
                if (radio.Band == RadioBand.Band2_4GHz)
                    continue;

                var currentWidth = radio.ChannelWidth ?? 0;
                if (currentWidth < 160)
                    continue;

                var bandName = radio.Band.ToDisplayString();
                var key = (ApMac: ap.Mac.ToLowerInvariant(), radio.Band);
                clientsByApBand.TryGetValue(key, out var clients);

                // Check for weak signal clients
                var hasWeakSignal = false;
                var weakClients = 0;
                var totalClients = clients?.Count ?? 0;
                double weakPct = 0;

                if (clients != null && clients.Count >= MinClientsForSignalCheck)
                {
                    weakClients = clients.Count(c =>
                        SignalClassification.IsWeakSignal(c.Signal!.Value, c.Band));
                    weakPct = (double)weakClients / clients.Count * 100;
                    hasWeakSignal = weakPct >= WeakClientPctThreshold;
                }

                // 6 GHz 320 MHz: always flag (unconditional - better performance + co-channel
                // separation) - EXCEPT when the band carries a mesh backhaul: 320 MHz there is
                // paying for backhaul capacity, and "Apply to All APs" would narrow both ends.
                if (radio.Band == RadioBand.Band6GHz && currentWidth >= 320)
                {
                    if (ap.MeshBackhaulUsesBand(RadioBand.Band6GHz))
                        continue;
                    yield return WithIntent(hasWeakSignal
                        ? BuildWeakSignalIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 160, weakClients, totalClients, weakPct)
                        : BuildInfoIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 160), ap, radio);
                    continue;
                }

                // 5 GHz >= 160 MHz (160, 240): only flag if weak signal clients
                // 6 GHz 160 MHz is fine - less co-channel interference than 5 GHz
                if (radio.Band == RadioBand.Band5GHz && currentWidth >= 160 && hasWeakSignal)
                {
                    yield return WithIntent(
                        BuildWeakSignalIssue(ap.Mac, ap.Name, radio.Band, currentWidth, 80, weakClients, totalClients, weakPct), ap, radio);
                }
            }
        }
    }

    /// <summary>
    /// A width that was chosen (set differently from the band's other radios, or paying for a
    /// mesh backhaul) is a check, not a correction: Info, with the hint saying which.
    /// </summary>
    private static HealthIssue WithIntent(HealthIssue issue, AccessPointSnapshot ap, RadioSnapshot radio)
    {
        if (ap.MeshBackhaulUsesBand(radio.Band))
            return RadioIntent.MarkDeliberate(issue, RadioIntent.WidthHint(radio.Band, meshBackhaul: true));
        if (radio.WidthIsOverride)
            return RadioIntent.MarkDeliberate(issue, RadioIntent.WidthHint(radio.Band, meshBackhaul: false));
        return issue;
    }

    /// <summary>
    /// Info-level issue for unconditionally wide channels (6 GHz 320 MHz).
    /// </summary>
    private HealthIssue BuildInfoIssue(string apMac, string apName, RadioBand band, int currentWidth, int suggestedWidth)
    {
        var bandName = band.ToDisplayString();
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Advisory,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(apMac, band)),
            Title = $"{bandName} {currentWidth} MHz: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}. " +
                $"Narrowing to {suggestedWidth} MHz can improve performance on some devices and gives better co-channel separation between APs.",
            AffectedEntity = apName,
            Recommendation = $"In UniFi Network: Settings > WiFi > Default WiFi Speeds > Channel Width - " +
                $"consider setting {bandName} to {suggestedWidth} MHz, then Save and Apply to All APs.",
            ScoreImpact = -2
        };
    }

    /// <summary>
    /// Warning-level issue when clients have poor signal on wide channels.
    /// </summary>
    private HealthIssue BuildWeakSignalIssue(
        string apMac, string apName, RadioBand band, int currentWidth, int suggestedWidth,
        int weakClients, int totalClients, double weakPct)
    {
        var bandName = band.ToDisplayString();
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality, HealthDimension.ChannelHealth },
            Class = HealthIssueClass.Advisory,
            Key = HealthIssueKeys.For(RuleId, HealthIssueKeys.Radio(apMac, band)),
            Title = $"Wide Channel with Weak Clients on {bandName}: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}, " +
                $"and {weakClients} of {totalClients} clients ({weakPct:F0}%) have weak signal for their band. " +
                $"Wider channels raise the noise floor and reduce effective range. " +
                $"Narrowing to {suggestedWidth} MHz should improve signal quality and reliability.",
            AffectedEntity = apName,
            Recommendation = $"In UniFi Network: Settings > WiFi > Default WiFi Speeds > Channel Width - " +
                $"set {bandName} to {suggestedWidth} MHz, then Save and Apply to All APs.",
            ScoreImpact = -5
        };
    }
}
