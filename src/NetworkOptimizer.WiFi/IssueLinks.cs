using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.WiFi;

/// <summary>One affected thing on a health issue, and where in Wi-Fi Optimizer it is shown.</summary>
public sealed record IssueLink(string Text, string Url);

/// <summary>
/// Wi-Fi Optimizer deep links for health issues. The query keys are the ones the page reads:
/// tab, client, ap, band ("2.4", "5", "6"), channel, and range.
/// </summary>
public static class IssueLinks
{
    private const string Page = "./wifi-optimizer";

    /// <summary>Client Stats, filtered to an AP and/or a band.</summary>
    public static string Clients(string? apMac = null, RadioBand? band = null)
    {
        var url = $"{Page}?tab=client";
        if (!string.IsNullOrEmpty(apMac)) url += "&ap=" + Uri.EscapeDataString(apMac);
        if (band.HasValue) url += "&band=" + band.Value.ToPropagationBand();
        return url;
    }

    /// <summary>One client in Client Stats.</summary>
    public static string Client(string mac) => $"{Page}?tab=client&client={Uri.EscapeDataString(mac)}";

    /// <summary>Metrics for one AP radio over the last hour.</summary>
    public static string Metrics(string apMac, RadioBand band) =>
        $"{Page}?tab=metrics&ap={Uri.EscapeDataString(apMac)}&band={band.ToPropagationBand()}&range=1h";

    /// <summary>Channels, on a band and focused on a channel when one is given.</summary>
    public static string Channel(RadioBand band, int? channel = null) =>
        $"{Page}?tab=channels&band={band.ToPropagationBand()}" + (channel.HasValue ? $"&channel={channel}" : "");

    /// <summary>Power &amp; Coverage.</summary>
    public static string Power() => $"{Page}?tab=power";

    /// <summary>Sets the issue's links and its <see cref="HealthIssue.AffectedEntity"/> to their joined text.</summary>
    public static HealthIssue WithLinks(this HealthIssue issue, IEnumerable<IssueLink> links)
    {
        issue.AffectedLinks = links.ToList();
        if (issue.AffectedLinks.Count > 0)
            issue.AffectedEntity = string.Join(", ", issue.AffectedLinks.Select(l => l.Text));
        return issue;
    }
}
