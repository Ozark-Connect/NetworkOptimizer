using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>One WAN as the outage evaluator needs it: identity, label, role, and link state.</summary>
/// <param name="WanKey">Normalized interface key ("wan", "wan2").</param>
/// <param name="Label">Display label from <see cref="GatewayWanHelper.FormatWanLabel"/> ("Acme Fiber WAN2").</param>
/// <param name="TreatAsPrimary">
/// Whether outage severity should treat this WAN as the primary. True when the console said so
/// AND when nothing has ever said (unknown role must over-alert about the connection the site
/// actually uses, not stay quiet); false only when the console recorded another WAN as primary.
/// </param>
/// <param name="ConsoleUp">The console's link state for the WAN, when a console was reachable; null when unknown.</param>
internal sealed record WanOutageWanInfo(string WanKey, string Label, bool TreatAsPrimary, bool? ConsoleUp);

/// <summary>A target's place in the persisted trace map.</summary>
internal sealed record WanOutageHopInfo(int Depth, IReadOnlySet<string> AncestorIps);

/// <summary>
/// Everything the WAN outage evaluator needs from the site's database, loaded as one snapshot
/// and cached by the evaluator. <see cref="HopsByTargetId"/> is keyed by
/// <c>MonitoringTarget.TargetId</c>; targets absent from it have no trace-map position.
/// </summary>
internal sealed record WanOutageContext(
    string PrimaryWanKey,
    IReadOnlyDictionary<string, WanOutageWanInfo> Wans,
    IReadOnlyDictionary<string, WanOutageHopInfo> HopsByTargetId,
    IReadOnlyDictionary<string, string> AccessNeighborIpByWan)
{
    public static readonly WanOutageContext Empty = new(
        GatewayWanHelper.DefaultWanKey,
        new Dictionary<string, WanOutageWanInfo>(),
        new Dictionary<string, WanOutageHopInfo>(),
        new Dictionary<string, string>());
}

/// <summary>
/// Loads the per-site WAN context the outage evaluator classifies against: WAN roles and labels
/// from <see cref="Storage.Models.WanProfile"/>, trace-map positions from
/// <see cref="Storage.Models.UpstreamDiscovery"/>, and each WAN's first-hop neighbor from
/// <see cref="Storage.Models.WanDiscoveryContext"/>. Reads the owning site's database directly
/// (the evaluator lives outside the ambient site scope), and consults the console's WAN link
/// state only for the default site, where the console connection is local; a missing console
/// just leaves the link state unknown, it never blocks evaluation.
/// </summary>
public class WanOutageContextSource
{
    private readonly SiteDbContextFactory _dbFactory;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<WanOutageContextSource> _logger;

    public WanOutageContextSource(SiteDbContextFactory dbFactory, IServiceScopeFactory scopeFactory,
        ILogger<WanOutageContextSource> logger)
    {
        _dbFactory = dbFactory;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    internal virtual async Task<WanOutageContext> LoadAsync(string siteSlug, IReadOnlyCollection<string> wanKeysInUse,
        CancellationToken ct = default)
    {
        var isDefault = siteSlug == SiteManagementService.DefaultSiteSlug;
        await using var db = _dbFactory.CreateForSite(siteSlug, isDefault);

        var profiles = await db.WanProfiles.AsNoTracking().ToListAsync(ct);
        var discoveryContexts = await db.WanDiscoveryContexts.AsNoTracking().ToListAsync(ct);
        var hopRows = await db.UpstreamDiscoveries.AsNoTracking()
            .Where(u => u.IsActive && u.MonitoringTargetId != null)
            .Select(u => new { u.MonitoringTargetId, u.HopNumber, u.AncestorHopIps })
            .ToListAsync(ct);
        var targetIdByDbId = await db.MonitoringTargets.AsNoTracking()
            .Select(t => new { t.Id, t.TargetId })
            .ToDictionaryAsync(t => t.Id, t => t.TargetId, ct);

        // The primary WAN owns every unstamped (legacy/hand-added) target. When no console has
        // ever recorded the role, the conventional first group is the documented guess.
        var primaryKey = profiles.FirstOrDefault(p => p.IsPrimary == true) is { } primary
            ? KeyFromGroup(primary.WanNetworkgroup)
            : GatewayWanHelper.DefaultWanKey;

        var consoleUp = isDefault ? await TryGetConsoleLinkStatesAsync(ct) : null;

        // Every WAN we know about, from any source: profiles, discovery contexts, and the WAN
        // keys the live targets are stamped with (a WAN can have targets before its profile row).
        var wans = new Dictionary<string, WanOutageWanInfo>(StringComparer.OrdinalIgnoreCase);
        var allKeys = profiles.Select(p => KeyFromGroup(p.WanNetworkgroup))
            .Concat(discoveryContexts.Select(d => GatewayWanHelper.WanInterfaceKeyFromKey(d.WanInterface)))
            .Concat(wanKeysInUse.Select(GatewayWanHelper.WanInterfaceKeyFromKey))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var key in allKeys)
        {
            var profile = profiles.FirstOrDefault(p =>
                string.Equals(KeyFromGroup(p.WanNetworkgroup), key, StringComparison.OrdinalIgnoreCase));
            var index = GatewayWanHelper.WanIndexFromKey(key);
            wans[key] = new WanOutageWanInfo(
                key,
                GatewayWanHelper.FormatWanLabel(profile?.Name, index, null, null),
                // Only an explicit "another WAN is primary" makes a WAN non-primary; an unknown
                // role must over-alert about the connection the site may actually be using.
                profile?.IsPrimary != false,
                consoleUp != null && consoleUp.TryGetValue(key, out var up) ? up : null);
        }

        var hops = new Dictionary<string, WanOutageHopInfo>();
        foreach (var group in hopRows.GroupBy(r => r.MonitoringTargetId!.Value))
        {
            if (!targetIdByDbId.TryGetValue(group.Key, out var targetId)) continue;
            var depths = group.Where(r => r.HopNumber > 0).Select(r => r.HopNumber).ToList();
            var ancestors = group
                .SelectMany(r => (r.AncestorHopIps ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            hops[targetId] = new WanOutageHopInfo(depths.Count > 0 ? depths.Min() : int.MaxValue, ancestors);
        }

        var accessNeighbors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dc in discoveryContexts.Where(d => !string.IsNullOrEmpty(d.L2NeighborIp)))
            accessNeighbors[GatewayWanHelper.WanInterfaceKeyFromKey(dc.WanInterface)] = dc.L2NeighborIp!;

        return new WanOutageContext(primaryKey, wans, hops, accessNeighbors);
    }

    /// <summary>
    /// The console's per-WAN link state for the default site, via the cached
    /// <see cref="MonitoringPathView"/> WAN summary. Null (unknown) whenever the console is not
    /// connected or the read fails - the outage verdict never depends on it, it only lets the
    /// notification say "the console reports the link down" instead of describing an outage.
    /// </summary>
    private async Task<Dictionary<string, bool>?> TryGetConsoleLinkStatesAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var pathView = scope.ServiceProvider.GetRequiredService<MonitoringPathView>();
            var wans = await pathView.GetWansAsync(ct);
            return wans.ToDictionary(
                w => GatewayWanHelper.WanInterfaceKeyFromKey(w.WanInterface),
                w => w.Up,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WAN link state unavailable for outage context; continuing without it");
            return null;
        }
    }

    private static string KeyFromGroup(string wanNetworkgroup) =>
        GatewayWanHelper.WanInterfaceKeyFromKey(wanNetworkgroup.ToLowerInvariant());
}
