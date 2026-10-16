using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Threats.Models;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>
/// A device on the site reaching an address on one of its public block lists: a likely infected device
/// calling a command server or a known-bad network. Allowed connections are the serious case; a blocked
/// attempt still says the device tried.
/// </summary>
public static class PublicListContact
{
    /// <summary>The event category that marks a public block list contact.</summary>
    public const string Category = "public-list";

    /// <summary>How close in time a CyberSecure (Suricata) event on the same pair counts as the same contact.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A flow from a device on the site out to a public address. Judged by direction, not by a private
    /// source address: a LAN client on IPv6 uses a global one.
    /// </summary>
    public static bool IsCandidate(ThreatEvent e) =>
        e.EventSource == EventSource.TrafficFlow
        && string.Equals(e.Direction, "outgoing", StringComparison.OrdinalIgnoreCase)
        && NetworkUtilities.IsPublicIpAddress(e.DestIp);

    /// <summary>
    /// Marks <paramref name="e"/> as a contact with <paramref name="list"/> (null when the list is not known,
    /// as for a drop a gateway agent reported after the list changed).
    /// </summary>
    public static void Tag(ThreatEvent e, PublicBlockList? list)
    {
        var name = list?.Name ?? "a public block list";
        e.Category = Category;
        e.SignatureName = e.Action == ThreatAction.Blocked
            ? $"Tried to reach an address on {name}"
            : $"Reached an address on {name}";
        e.KillChainStage = KillChainStage.PostExploitation;
        e.Severity = e.Action == ThreatAction.Blocked ? 4 : 5;
    }
}
