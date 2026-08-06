using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Pulls a site's unpinned targets into the vantage that measures the primary WAN, the moment one
/// exists.
/// <para>
/// An unpinned probe leaves by the box's own route, which on a failover site is the primary. Once
/// a vantage measures that WAN, those targets belong to it - left out they sit in a bucket the
/// vantage's own report cannot see, which is the opposite of what creating it was for. Their
/// history follows: the Influx series is keyed on target id, which does not change, and the
/// primary's WAN scope already ORs untagged points with its vantage's tag.
/// </para>
/// </summary>
public static class PrimaryWanVantageAdoption
{
    /// <summary>
    /// Whether an unpinned target can honestly be called this WAN's.
    /// <para>
    /// On a failover site, yes: unpinned means the primary except for the length of an outage. On
    /// a load-balancing site it means no single WAN, and only a policy route pinning the probing
    /// box makes it this one - which the caller resolves and passes as
    /// <paramref name="routePinsProbesToPrimary"/>.
    /// </para>
    /// </summary>
    public static bool ShouldAdopt(bool siteLoadBalances, bool routePinsProbesToPrimary) =>
        !siteLoadBalances || routePinsProbesToPrimary;

    /// <summary>
    /// Moves every unpinned target onto this vantage. Returns how many moved. Caller saves.
    /// <para>
    /// Fabric is left alone: it never leaves the LAN, so no WAN describes it and a vantage would
    /// only narrow which agent probes it.
    /// </para>
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="vantage">The vantage measuring the primary WAN; must already have its Id.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> AdoptUnpinnedTargetsAsync(
        NetworkOptimizerDbContext db, WanContext vantage, CancellationToken ct = default)
    {
        if (vantage.Id == 0 || string.IsNullOrEmpty(vantage.WanInterface)) return 0;

        var unpinned = await db.MonitoringTargets
            .Where(t => t.WanContextId == null
                && t.TargetType != MonitoringTargetType.Fabric
                && (t.WanInterface == null || t.WanInterface == MonitoringTarget.UnpinnedWan))
            .ToListAsync(ct);

        foreach (var target in unpinned)
            WanContextTargetStamping.ApplyAssignment(target, vantage.Id, vantage.WanInterface);

        return unpinned.Count;
    }

    /// <summary>
    /// Whether this vantage measures the site's primary WAN, which is the only one whose unpinned
    /// targets it can claim. Null when the primary is unknown, and an unknown primary claims
    /// nothing.
    /// </summary>
    public static bool MeasuresPrimaryWan(WanContext vantage, string? primaryWanKey) =>
        !string.IsNullOrEmpty(primaryWanKey)
        && !string.IsNullOrEmpty(vantage.WanInterface)
        && string.Equals(
            GatewayWanHelper.WanInterfaceKeyFromKey(vantage.WanInterface!),
            GatewayWanHelper.WanInterfaceKeyFromKey(primaryWanKey!),
            StringComparison.OrdinalIgnoreCase);
}
