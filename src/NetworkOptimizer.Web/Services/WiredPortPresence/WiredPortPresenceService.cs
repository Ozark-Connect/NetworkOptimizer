using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.WiredPortPresence;

/// <summary>
/// Answers wired port presence from what the collector already keeps in memory: the console's
/// placements of clients on ports and the unicast movement on each port, both refreshed on every
/// poll. The console's cached device and client lists supply the rest. The one store read is a
/// single cold-start fill of the placements made before the app started. Answers are held for
/// <see cref="CacheFor"/> per site, static because the service is scoped and every open page on
/// a site asks the same question.
/// </summary>
public class WiredPortPresenceService : IWiredPortPresenceService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan IfNamesFor = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<WiredPortPresence> List)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, (DateTime At, Dictionary<(string, int), List<string>> Map)> IfNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);

    private readonly UniFiConnectionService _connection;
    private readonly MonitoringLiveStatsRegistry _liveStats;
    private readonly MonitoringInfluxClient _influx;
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<WiredPortPresenceService> _logger;

    public WiredPortPresenceService(
        UniFiConnectionService connection,
        MonitoringLiveStatsRegistry liveStats,
        MonitoringInfluxClient influx,
        SiteDbContextFactory siteDb,
        SiteContextService siteContext,
        ILogger<WiredPortPresenceService> logger)
    {
        _connection = connection;
        _liveStats = liveStats;
        _influx = influx;
        _siteDb = siteDb;
        _siteContext = siteContext;
        _logger = logger;
    }

    public async Task<WiredPortPresence?> ResolveAsync(string clientMac)
    {
        var mac = NormalizeMac(clientMac);
        if (mac.Length == 0) return null;
        var list = await ListAsync();
        return list.FirstOrDefault(p => string.Equals(p.ClientMac, mac, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<WiredPortPresence>> ListAsync()
    {
        var slug = _siteContext.Slug;
        if (Cache.TryGetValue(slug, out var hit) && DateTime.UtcNow - hit.At < CacheFor)
            return hit.List;

        var gate = Locks.GetOrAdd(slug, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (Cache.TryGetValue(slug, out hit) && DateTime.UtcNow - hit.At < CacheFor)
                return hit.List;
            var list = await BuildAsync();
            Cache[slug] = (DateTime.UtcNow, list);
            return list;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<WiredPortPresence>> BuildAsync()
    {
        // No console: nothing vouches, and the console's verdict stands.
        if (!_connection.IsConnected || _connection.Client == null)
            return Array.Empty<WiredPortPresence>();

        try
        {
            var now = DateTime.UtcNow;
            var live = _liveStats.GetFor(_siteContext.Slug);
            await SeedAsync(live, now);

            var sightings = live.GetPortOccupants()
                .Select(o => new PortSighting(o.DeviceMac, o.Port, o.ClientMac, o.LastSeen, o.Ip, o.Name))
                .ToList();
            if (sightings.Count == 0) return Array.Empty<WiredPortPresence>();

            var devices = await _connection.Client.GetDevicesAsync() ?? new List<UniFiDeviceResponse>();
            var clients = await _connection.Client.GetClientsAsync() ?? new List<UniFiClientResponse>();
            var ifNamesByPort = await IfNamesByPortAsync();

            var listed = new HashSet<string>(clients.Select(c => NormalizeMac(c.Mac)), StringComparer.OrdinalIgnoreCase);
            var occupied = new HashSet<(string, int)>(clients
                .Where(c => c.IsWired && !string.IsNullOrEmpty(c.SwMac) && c.SwPort is > 0)
                .Select(c => (NormalizeMac(c.SwMac), c.SwPort!.Value)));
            // A port another device hangs off, by that device's own uplink record. Gateway WAN
            // ports and LAG members are not client ports either.
            var uplinks = new HashSet<(string, int)>(devices
                .Where(d => d.Uplink != null && !string.IsNullOrEmpty(d.Uplink.UplinkMac) && d.Uplink.UplinkRemotePort > 0)
                .Select(d => (NormalizeMac(d.Uplink!.UplinkMac), d.Uplink.UplinkRemotePort)));
            var portsByDevice = devices
                .Where(d => !string.IsNullOrEmpty(d.Mac) && d.PortTable != null)
                .ToDictionary(d => NormalizeMac(d.Mac), d => d.PortTable!, StringComparer.OrdinalIgnoreCase);
            foreach (var (mac, ports) in portsByDevice)
                foreach (var p in ports.Where(p => p.IsUplink || p.AggregatedBy is > 0))
                    uplinks.Add((mac, p.PortIdx));

            // The switch's own link state from the last SNMP sample leads, since it is what the
            // unicast count is read against; the console's port table answers where SNMP has not.
            bool? PortUp(string switchMac, int port)
            {
                if (ifNamesByPort.TryGetValue((switchMac, port), out var ifNames))
                {
                    var snapshot = live.GetPortStatsSnapshot(new[] { switchMac });
                    foreach (var ifName in ifNames)
                    {
                        var row = snapshot.FirstOrDefault(r => string.Equals(r.IfName, ifName, StringComparison.OrdinalIgnoreCase));
                        if (row?.OperStatus is { } oper) return oper == 1;
                    }
                }
                if (!portsByDevice.TryGetValue(switchMac, out var ports)) return null;
                return ports.FirstOrDefault(p => p.PortIdx == port)?.Up;
            }

            bool? UnicastFlowing(string switchMac, int port)
            {
                if (!ifNamesByPort.TryGetValue((switchMac, port), out var ifNames)) return null;
                bool? flowing = null;
                foreach (var ifName in ifNames)
                {
                    if (live.GetPortUnicastIn(switchMac, ifName) is not { } unicast) continue;
                    var recent = now - unicast.At <= WiredPortPresenceRule.UnicastWindow
                        && unicast.Packets >= WiredPortPresenceRule.UnicastFloorPackets;
                    flowing = (flowing ?? false) || recent;
                }
                return flowing;
            }

            var result = WiredPortPresenceRule.Evaluate(sightings, listed, occupied, PortUp, UnicastFlowing, uplinks, now);
            if (result.Count > 0)
                _logger.LogDebug("Wired port presence [{Site}]: {Count} client(s) online by port link that the console does not list",
                    _siteContext.Slug, result.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wired port presence unavailable for site {Site}", _siteContext.Slug);
            return Array.Empty<WiredPortPresence>();
        }
    }

    /// <summary>
    /// One-time fill of the placements the console made before the app started, from the
    /// port-tagged samples the collector wrote. The collector keeps the table current from here.
    /// </summary>
    private async Task SeedAsync(MonitoringLiveStats live, DateTime now)
    {
        if (live.PortOccupantsSeeded) return;
        if (!_influx.IsConfigured)
        {
            live.SeedPortOccupants(Array.Empty<MonitoringLiveStats.PortOccupant>());
            return;
        }
        var sightings = await _influx.QueryWiredPortSightingsAsync(now - WiredPortPresenceRule.Lookback, now);
        live.SeedPortOccupants(sightings.Select(s =>
            new MonitoringLiveStats.PortOccupant(s.DeviceMac, s.Port, s.ClientMac, s.ClientIp, s.ClientName, s.LastSeen)));
        _logger.LogDebug("Wired port presence [{Site}]: seeded {Count} placement(s) from the store", _siteContext.Slug, sightings.Count);
    }

    /// <summary>The SNMP interface names behind each console port number, from the site's name maps.</summary>
    private async Task<Dictionary<(string, int), List<string>>> IfNamesByPortAsync()
    {
        var slug = _siteContext.Slug;
        if (IfNames.TryGetValue(slug, out var hit) && DateTime.UtcNow - hit.At < IfNamesFor)
            return hit.Map;

        var result = new Dictionary<(string, int), List<string>>();
        await using var db = _siteDb.CreateForSite(slug, _siteContext.IsDefault);
        var maps = await db.InterfaceNameMaps.AsNoTracking()
            .Where(m => m.PortNumber > 0)
            .Select(m => new { m.DeviceMac, m.PortNumber, m.IfName })
            .ToListAsync();
        foreach (var m in maps)
        {
            var key = (NormalizeMac(m.DeviceMac), m.PortNumber!.Value);
            if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>();
            list.Add(m.IfName);
        }
        IfNames[slug] = (DateTime.UtcNow, result);
        return result;
    }

    private static string NormalizeMac(string? mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace("-", ":");
}
