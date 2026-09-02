using System.Text.Json;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi.Helpers;

/// <summary>
/// What the configuration says the operator chose, and how an advisory issue defers to it. A
/// measured issue never reads this for its severity; a hint is the most it adds.
/// </summary>
public static class RadioIntent
{
    // Copy: research/wifi-agent-insights/verbiage.md, DC-HINT-*.
    /// <summary>DC-HINT-POWER.</summary>
    public const string PowerHint =
        "These power levels are set by hand in UniFi Network, so this is a check rather than a correction.";

    /// <summary>DC-HINT-CHANNEL.</summary>
    public const string ChannelHint =
        "These channels are set by hand in UniFi Network. The overlap is real either way; the Channel Recommendation on Channels can weigh the alternatives.";

    /// <summary>DC-HINT-WIDTH.</summary>
    public static string WidthHint(RadioBand band, bool meshBackhaul) =>
        "This width looks deliberate: " + (meshBackhaul
            ? "the band carries a mesh backhaul"
            : $"it is set differently from the other {band.ToDisplayString()} radios here") + ".";

    /// <summary>
    /// An advisory issue about something the configuration shows was chosen: severity drops to
    /// Info and the hint says why, so the finding stays visible without reading as a mistake.
    /// </summary>
    public static HealthIssue MarkDeliberate(HealthIssue issue, string hint)
    {
        issue.Severity = HealthIssueSeverity.Info;
        AppendHint(issue, hint);
        return issue;
    }

    /// <summary>Appends a hint to the description without touching severity; for measured issues.</summary>
    public static void AppendHint(HealthIssue issue, string hint)
    {
        var description = issue.Description.TrimEnd();
        issue.Description = description.Length == 0 ? hint : $"{description} {hint}";
    }

    /// <summary>
    /// Whether a radio_table channel value is a fixed channel rather than "auto". The console
    /// serializes it as a number when set by hand and the string "auto" otherwise, and System.Text.Json
    /// hands either back as a JsonElement.
    /// </summary>
    public static bool IsFixedChannel(object? channel)
    {
        switch (channel)
        {
            case null:
                return false;
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Number => true,
                    JsonValueKind.String => IsFixedChannel(element.GetString()),
                    _ => false
                };
            case string s:
                return s.Trim().Length > 0 && !s.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);
            case int or long or short:
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Marks each radio whose width differs from the most common width on its band across the
    /// site's online APs. UniFi carries no per-AP width override flag, so this is the best
    /// available reading of "set differently on purpose". A band with one radio, or a tie for
    /// the most common width, marks nothing: there is no usual width to differ from.
    /// </summary>
    public static void ComputeWidthOverrides(IReadOnlyList<AccessPointSnapshot> aps)
    {
        var radiosByBand = aps
            .Where(ap => ap.IsOnline)
            .SelectMany(ap => ap.Radios)
            .Where(r => r.Channel.HasValue && r.ChannelWidth is > 0)
            .GroupBy(r => r.Band);

        foreach (var band in radiosByBand)
        {
            var radios = band.ToList();
            foreach (var r in radios) r.WidthIsOverride = false;
            if (radios.Count < 2) continue;

            var counts = radios.GroupBy(r => r.ChannelWidth!.Value)
                .Select(g => (Width: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .ToList();
            if (counts.Count > 1 && counts[0].Count == counts[1].Count) continue;

            var usual = counts[0].Width;
            foreach (var r in radios)
                r.WidthIsOverride = r.ChannelWidth!.Value != usual;
        }
    }
}
