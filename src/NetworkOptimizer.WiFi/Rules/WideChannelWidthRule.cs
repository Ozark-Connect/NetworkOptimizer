using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Rules;

/// <summary>
/// Rule that flags wide channel widths on 5 GHz and 6 GHz radios.
/// - 6 GHz 320 MHz: always suggest 160 MHz (better client performance + AP co-channel separation)
/// - 5 GHz/6 GHz 160 MHz with weak-signal clients: suggest narrowing to 80 MHz
/// Severity escalates from Info to Warning when clients have poor signal.
/// </summary>
public class WideChannelWidthRule : IWiFiOptimizerRule
{
    public string RuleId => "WIFI-WIDE-CHANNEL-WIDTH-001";

    private const int WeakSignalThreshold = -70;
    private const double WeakClientPctThreshold = 25;
    private const int MinClientsForSignalCheck = 2;

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
                    weakClients = clients.Count(c => c.Signal < WeakSignalThreshold);
                    weakPct = (double)weakClients / clients.Count * 100;
                    hasWeakSignal = weakPct >= WeakClientPctThreshold;
                }

                // 6 GHz 320 MHz: always flag (unconditional - better performance + co-channel separation)
                if (radio.Band == RadioBand.Band6GHz && currentWidth >= 320)
                {
                    yield return hasWeakSignal
                        ? BuildWeakSignalIssue(ap.Name, bandName, currentWidth, 160, weakClients, totalClients, weakPct)
                        : BuildInfoIssue(ap.Name, bandName, currentWidth, 160);
                    continue;
                }

                // 5 GHz or 6 GHz 160 MHz: only flag if weak signal clients
                if (currentWidth >= 160 && hasWeakSignal)
                {
                    yield return BuildWeakSignalIssue(ap.Name, bandName, currentWidth, 80, weakClients, totalClients, weakPct);
                }
            }
        }
    }

    /// <summary>
    /// Info-level issue for unconditionally wide channels (6 GHz 320 MHz).
    /// </summary>
    private static HealthIssue BuildInfoIssue(string apName, string bandName, int currentWidth, int suggestedWidth)
    {
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Info,
            Dimensions = { HealthDimension.ChannelHealth },
            Title = $"{bandName} {currentWidth} MHz: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}. " +
                $"Narrowing to {suggestedWidth} MHz can improve performance on some devices and gives better co-channel separation between APs.",
            AffectedEntity = apName,
            Recommendation = $"In UniFi Network: Settings > WiFi > (SSID) > Advanced > Channel Width - " +
                $"consider reducing {bandName} from {currentWidth} MHz to {suggestedWidth} MHz.",
            ScoreImpact = -2
        };
    }

    /// <summary>
    /// Warning-level issue when clients have poor signal on wide channels.
    /// </summary>
    private static HealthIssue BuildWeakSignalIssue(
        string apName, string bandName, int currentWidth, int suggestedWidth,
        int weakClients, int totalClients, double weakPct)
    {
        return new HealthIssue
        {
            Severity = HealthIssueSeverity.Warning,
            Dimensions = { HealthDimension.SignalQuality, HealthDimension.ChannelHealth },
            Title = $"Wide Channel with Weak Clients on {bandName}: {apName}",
            Description = $"{apName} is using {currentWidth} MHz on {bandName}, " +
                $"and {weakClients} of {totalClients} clients ({weakPct:F0}%) have signal below {WeakSignalThreshold} dBm. " +
                $"Wider channels raise the noise floor and reduce effective range. " +
                $"Narrowing to {suggestedWidth} MHz should improve signal quality and reliability.",
            AffectedEntity = apName,
            Recommendation = $"In UniFi Network: Settings > WiFi > (SSID) > Advanced > Channel Width - " +
                $"reduce {bandName} from {currentWidth} MHz to {suggestedWidth} MHz.",
            ScoreImpact = -5
        };
    }
}
