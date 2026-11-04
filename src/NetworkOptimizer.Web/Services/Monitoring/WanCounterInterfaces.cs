using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// The SNMP counter interface behind each WAN, resolved one way for every chart that draws WAN
/// throughput: the WAN live chart per WAN, and Device Stats' WAN Throughput as the sum of the
/// WANs the live chart's All WANs view stacks. Keep both on this class, or the two totals drift.
/// </summary>
public class WanCounterInterfaces
{
    private readonly UniFiConnectionService _connectionService;
    private readonly MonitoringPathView _pathView;
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;

    // Per site: AllAsync asks the console once per WAN, and Device Stats polls every few seconds.
    private static readonly ConcurrentDictionary<string, (DateTime At, string? GatewayMac, List<string> IfNames)> AllCache = new();
    private static readonly TimeSpan AllCacheLife = TimeSpan.FromSeconds(60);

    public WanCounterInterfaces(
        UniFiConnectionService connectionService,
        MonitoringPathView pathView,
        SiteDbContextFactory siteDb,
        SiteContextService siteContext)
    {
        _connectionService = connectionService;
        _pathView = pathView;
        _siteDb = siteDb;
        _siteContext = siteContext;
    }

    /// <summary>
    /// One WAN's counter interface: live from the console, else that WAN's own remembered profile.
    /// Never another WAN's - an empty answer is honest for a WAN nothing has recorded, where
    /// borrowing the primary's would draw someone else's traffic under this WAN's name.
    /// </summary>
    /// <param name="wanKey">A UniFi WAN key such as "wan2".</param>
    /// <param name="needGatewayMac">
    /// True when the caller has no gateway MAC from the console, so the profile is read for its MAC
    /// even when the console gave the counter. False reads the profile only for a missing counter.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The counter interface, and the profile's gateway MAC (raw, as stored) when the profile was read
    /// and has one.
    /// </returns>
    public async Task<(string? CounterIfName, string? ProfileGatewayMac)> ForWanAsync(
        string wanKey, bool needGatewayMac, CancellationToken ct)
    {
        var group = GatewayWanHelper.WanNetworkGroupFromKey(wanKey);
        string? counter = null;
        string? profileMac = null;
        try
        {
            var ifaces = await _connectionService.GetWanInterfacesForGroupAsync(group, ct);
            counter = ifaces?.CounterIfName;
        }
        catch { }
        if (!string.IsNullOrEmpty(counter) && !needGatewayMac)
            return (counter, null);
        try
        {
            await using var db = _siteDb.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);
            var profile = await db.WanProfiles.AsNoTracking()
                .Where(w => w.WanNetworkgroup == group)
                .OrderByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            counter = string.IsNullOrEmpty(counter) ? profile?.CounterInterface : counter;
            profileMac = profile?.GatewayMac;
        }
        catch { }
        return (counter, profileMac);
    }

    /// <summary>
    /// Every WAN the WAN live chart's filter offers (the selectable WANs plus any a WAN context
    /// names, as Monitoring builds it), each through <see cref="ForWanAsync"/>: the interfaces its
    /// All WANs view stacks. The gateway MAC comes from the console, else from a WAN's profile.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(string? GatewayMac, List<string> CounterIfNames)> AllAsync(CancellationToken ct)
    {
        var slug = _siteContext.Slug;
        if (AllCache.TryGetValue(slug, out var cached) && DateTime.UtcNow - cached.At < AllCacheLife)
            return (cached.GatewayMac, cached.IfNames.ToList());

        var keys = new List<string>();
        try
        {
            foreach (var wan in await _pathView.GetSelectableWansAsync(ct))
                keys.Add(wan.WanInterface.ToLowerInvariant());
        }
        catch { }
        try
        {
            await using var db = _siteDb.CreateForSite(slug, _siteContext.IsDefault);
            foreach (var key in await db.WanContexts.AsNoTracking()
                         .Where(c => c.WanInterface != null).Select(c => c.WanInterface!).ToListAsync(ct))
            {
                var lower = key.ToLowerInvariant();
                if (!keys.Contains(lower)) keys.Add(lower);
            }
        }
        catch { }

        string? gatewayMac = null;
        try
        {
            var gw = (await _connectionService.GetDiscoveredDevicesAsync(ct))?.FirstOrDefault(d =>
                d.Type == Core.Enums.DeviceType.Gateway || d.HardwareType == Core.Enums.DeviceType.Gateway);
            gatewayMac = gw?.Mac;
        }
        catch { }

        var ifNames = new List<string>();
        foreach (var key in keys)
        {
            var (counter, profileMac) = await ForWanAsync(key, needGatewayMac: string.IsNullOrEmpty(gatewayMac), ct);
            if (!string.IsNullOrEmpty(counter) && !ifNames.Contains(counter, StringComparer.OrdinalIgnoreCase))
                ifNames.Add(counter);
            if (string.IsNullOrEmpty(gatewayMac) && profileMac != null)
                gatewayMac = profileMac.Replace("-", ":").ToLowerInvariant();
        }

        AllCache[slug] = (DateTime.UtcNow, gatewayMac, ifNames);
        return (gatewayMac, ifNames.ToList());
    }
}
