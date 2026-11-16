using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>A WAN selection the Path Map is drawn for, as the chart's WAN pills set it.</summary>
/// <param name="Wans">The selected WAN keys, lower case.</param>
/// <param name="All">Every WAN: the only view a target pinned to no WAN belongs in.</param>
/// <param name="PrimaryKey">The primary WAN's key.</param>
public sealed record PathMapSelection(IReadOnlySet<string> Wans, bool All, string PrimaryKey)
{
    /// <summary>A stable key for the selection, so viewers of the same one share a pass.</summary>
    public string Key => All ? "all" : string.Join(",", Wans.Order(StringComparer.Ordinal));

    public static PathMapSelection Parse(string? wans, bool all, string? primary)
    {
        var keys = (wans ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(k => k.ToLowerInvariant()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primaryKey = string.IsNullOrWhiteSpace(primary) ? "wan" : primary.Trim().ToLowerInvariant();
        if (keys.Count == 0 && !all) keys.Add(primaryKey);
        return new PathMapSelection(keys, all, primaryKey);
    }
}

/// <summary>Which monitored targets the Path Map draws and traces.</summary>
public static class PathMapScope
{
    /// <summary>
    /// A routed target: enabled, not retired or hidden, an ISP, Transit, Internet, or Custom target, and
    /// not on the local network (a LAN target has no route). Infrastructure the charts leave out stays out.
    /// </summary>
    public static bool IsMapped(MonitoringTarget t) =>
        t.Enabled && !t.IsRetired && !t.IsHidden && !string.IsNullOrEmpty(t.Address)
        && t.TargetType is MonitoringTargetType.AccessIsp or MonitoringTargetType.Transit
            or MonitoringTargetType.InternetService or MonitoringTargetType.Custom
        && !LocalTargetResolver.IsLocal(t)
        && (t.AsnNumber == null || !WellKnownAsns.NonTransitInfrastructure.Contains(t.AsnNumber.Value));

    /// <summary>Probes pinned to no WAN measure no single WAN.</summary>
    public static bool IsUnpinned(MonitoringTarget t) => MonitoringTarget.IsUnpinned(t.WanInterface);

    /// <summary>The WAN key a target is drawn under: its own, or the primary's for an unpinned one.</summary>
    public static string WanKey(MonitoringTarget t, string primaryKey) =>
        IsUnpinned(t) ? primaryKey : t.WanInterface!.ToLowerInvariant();

    /// <summary>Whether the selection shows the target. An unpinned target shows only when every WAN is selected.</summary>
    public static bool InView(MonitoringTarget t, PathMapSelection selection) =>
        IsUnpinned(t) ? selection.All : selection.All || selection.Wans.Contains(t.WanInterface!.ToLowerInvariant());

    /// <summary>An Internet or Custom target: its route ends at the target, so a pass traces it.</summary>
    public static bool IsDestination(MonitoringTarget t) =>
        t.TargetType is MonitoringTargetType.InternetService or MonitoringTargetType.Custom;
}
