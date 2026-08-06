using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Shared pieces of the chart event marks, so the tabs that draw them agree on what an event is
/// called and how bad it looks. The marks themselves are assembled per endpoint, because what
/// identifies a series differs by tab (a device MAC, a device and port, an ONT config).
/// </summary>
internal static class ChartEventMarks
{
    /// <summary>ONT alert types. Raised against an attached module on SFP Stats, a standalone one on ONT Stats.</summary>
    internal static readonly HashSet<string> OntEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ont.rx_power_low",
        "ont.high_temperature",
        "ont.pon_link_down",
        "ont.bip_errors",
        "ont.fec_errors",
        "ont.hec_errors",
    };

    /// <summary>Severity as the mark layer colours it.</summary>
    internal static string Severity(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical or AlertSeverity.Error => "critical",
        AlertSeverity.Warning => "warning",
        _ => "info",
    };

    /// <summary>
    /// Short label for an ONT mark: the alert's own title with the ONT name and any site suffix
    /// taken off the ends, since the tooltip carries the ONT on its own row. Derived from the
    /// stored copy rather than reworded, so the two cannot drift.
    /// </summary>
    internal static string OntEventLabel(string title, string? ontName)
    {
        var label = title;

        var siteSuffix = label.LastIndexOf(" (site ", StringComparison.Ordinal);
        if (siteSuffix > 0 && label.EndsWith(')')) label = label[..siteSuffix];

        if (!string.IsNullOrEmpty(ontName) && label.StartsWith(ontName + " ", StringComparison.OrdinalIgnoreCase))
            label = label[(ontName.Length + 1)..];

        if (label.Length == 0) return title;
        label = char.ToUpperInvariant(label[0]) + label[1..];

        // The ONT copy words this one the other way round from every other mark. Normalized here
        // rather than at the source so the alert itself keeps the wording it has always had, and
        // only the mark is brought into line. Everything else the ONT titles produce - "RX power
        // low", "PON link down", the error spikes - already matches.
        return label.Equals("Temperature high", StringComparison.OrdinalIgnoreCase)
            ? "High temperature"
            : label;
    }
}
