using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
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
    /// Moves every unpinned target that actually leaves by a WAN onto this vantage. Returns how
    /// many moved. Caller saves.
    /// <para>
    /// LAN targets are left alone, and there are two kinds. Fabric is the discovered gateway,
    /// switches and APs. The other is a hand-added target pointing at a private address - a
    /// controller, a NAS, anything on the same network - which is every bit as much a LAN
    /// measurement even though its type says Custom. Neither traverses a WAN, so filing them under
    /// one would say their latency is that WAN's.
    /// </para>
    /// <para>
    /// A LAN host named by hostname rather than address is not recognised here and will be
    /// adopted; the address is all this has to go on.
    /// </para>
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="vantage">The vantage measuring the primary WAN; must already have its Id.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<int> AdoptUnpinnedTargetsAsync(
        NetworkOptimizerDbContext db, WanContext vantage, CancellationToken ct = default)
    {
        if (vantage.Id == 0 || string.IsNullOrEmpty(vantage.WanInterface)) return 0;

        // Two kinds of row belong to this vantage, and nothing on screen told them apart: one that
        // names no WAN, and one the primary's own discovery already stamped with this WAN but
        // never gave a vantage. Both read as "Default path" in the target list, because that
        // control shows the CONTEXT; taking only the first left the other sitting there looking
        // identical and untouched.
        var vantageKey = GatewayWanHelper.WanInterfaceKeyFromKey(vantage.WanInterface!);
        var unpinned = (await db.MonitoringTargets
            .Where(t => t.WanContextId == null && t.TargetType != MonitoringTargetType.Fabric)
            .ToListAsync(ct))
            .Where(t => !IsLanTarget(t)
                && (MonitoringTarget.IsUnpinned(t.WanInterface)
                    || string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(t.WanInterface!),
                        vantageKey, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var target in unpinned)
            WanContextTargetStamping.ApplyAssignment(target, vantage.Id, vantage.WanInterface);

        return unpinned.Count;
    }

    /// <summary>
    /// Whether a target measures something on this network rather than out through a WAN. Answered
    /// by <see cref="LocalTargetResolver"/>, which prefers what was resolved and settled for this
    /// target over what its address happens to look like.
    /// </summary>
    public static bool IsLanTarget(MonitoringTarget target) => LocalTargetResolver.IsLocal(target);

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
