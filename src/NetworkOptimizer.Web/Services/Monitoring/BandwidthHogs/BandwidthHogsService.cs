using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.LanFlowMap;

namespace NetworkOptimizer.Web.Services.Monitoring.BandwidthHogs;

/// <summary>
/// The Bandwidth Hogs list: every client's throughput at an instant, or its data usage over a
/// window, each split into what left through the WAN and everything through its port or radio.
/// <para>
/// Throughput reads the per-client link rates the LAN flow map draws, live or at the playhead, so
/// the list and the map never disagree. Data usage follows the Client Performance Data tab's own
/// rules: WAN from UniFi Network's site-wide DPI report, LAN + WAN from our counters, with the
/// hourly rollup taking over past six hours.
/// </para>
/// </summary>
public class BandwidthHogsService
{
    private readonly LanFlowMapService _map;
    private readonly ClientDashboardService _dashboard;
    private readonly MonitoringInfluxClient _influx;
    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _site;
    private readonly ILogger<BandwidthHogsService> _logger;

    /// <summary>How far back the DPI report is read to weight a live WAN split.</summary>
    private static readonly TimeSpan DpiRecentWindow = TimeSpan.FromMinutes(15);

    /// <summary>The Data tab's rule: counters answer up to here, the rollup past it.</summary>
    private static readonly TimeSpan CounterWindow = TimeSpan.FromHours(6);

    /// <summary>Past this, no rollup means no answer - a counter scan over days is minutes.</summary>
    private static readonly TimeSpan CounterFallbackMax = TimeSpan.FromHours(48);

    private static readonly TimeSpan RollupTopUpMax = TimeSpan.FromHours(2);

    public BandwidthHogsService(
        LanFlowMapService map,
        ClientDashboardService dashboard,
        MonitoringInfluxClient influx,
        SiteDbContextFactory siteDb,
        SiteContextService site,
        ILogger<BandwidthHogsService> logger)
    {
        _map = map;
        _dashboard = dashboard;
        _influx = influx;
        _siteDb = siteDb;
        _site = site;
        _logger = logger;
    }

    /// <summary>
    /// Every client moving traffic at <paramref name="at"/> (null = now), with its WAN share
    /// reconciled against the selected WANs' rate.
    /// </summary>
    public async Task<HogsResult> GetThroughputAsync(
        DateTime? at, double? wanDownBps, double? wanUpBps, IReadOnlyCollection<string> wanKeys, CancellationToken ct = default)
    {
        var snapshot = await _map.BuildSnapshotAsync(ct);
        List<LanNode> nodes;
        List<LanLink> links;
        IReadOnlyDictionary<string, LinkLiveRates> rates;
        if (at == null)
        {
            var live = await _map.GetLiveUpdateAsync(ct);
            nodes = snapshot.Nodes.Concat(live.AddedClientNodes).ToList();
            links = snapshot.Links.Concat(live.AddedClientLinks).ToList();
            rates = live.LinkRates;
        }
        else
        {
            var historic = await _map.GetHistoricUpdateAsync(at.Value, ct);
            nodes = snapshot.Nodes.Concat(historic.AddedClientNodes).ToList();
            links = snapshot.Links.Concat(historic.AddedClientLinks).ToList();
            rates = historic.LinkRates;
        }

        var nodeById = new Dictionary<string, LanNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in nodes) nodeById.TryAdd(n.Id, n);
        var linksInto = links.GroupBy(l => l.ToNodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        // A shared port is the map's hub node: the port's rate lives on it, and the interfaces
        // behind it are zero-rate leaves. The card lists the hub, not the leaves - the leaves'
        // only rate is the console's per-MAC figure, a different measurement from the port's.
        var membersByHub = nodes
            .Where(n => n.Kind == LanNodeKind.WiredClient && !string.IsNullOrEmpty(n.Mac) && n.ParentId != null
                && nodeById.TryGetValue(n.ParentId, out var parent) && parent.Kind == LanNodeKind.VirtualHub)
            .GroupBy(n => n.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var measured = new List<(LanNode Node, double Down, double Up, double? CapDown, double? CapUp)>();
        foreach (var node in nodes)
        {
            var isHub = node.Kind == LanNodeKind.VirtualHub;
            if (!isHub && (node.Kind is not (LanNodeKind.WifiClient or LanNodeKind.WiredClient) || string.IsNullOrEmpty(node.Mac))) continue;
            if (!isHub && node.ParentId != null && membersByHub.ContainsKey(node.ParentId)) continue;
            if (!linksInto.TryGetValue(node.Id, out var into)) continue;
            var link = into.FirstOrDefault(l => l.Kind is LanLinkKind.WifiClient or LanLinkKind.WiredClient);
            if (link == null || !rates.TryGetValue(link.Id, out var rate)) continue;
            if (rate.DownstreamBps <= 0 && rate.UpstreamBps <= 0) continue;
            var (capDown, capUp) = ChainCap(node.ParentId, nodeById, linksInto, rates);
            measured.Add((node, Math.Max(0, rate.DownstreamBps), Math.Max(0, rate.UpstreamBps), capDown, capUp));
        }

        var end = at ?? DateTime.UtcNow;
        var dpi = await DpiTotalsAsync(end - DpiRecentWindow, end, ct);

        var loadsDown = new List<WanShareReconciler.Load>(measured.Count);
        var loadsUp = new List<WanShareReconciler.Load>(measured.Count);
        foreach (var m in measured)
        {
            (double Down, double Up) bytes = default;
            if (m.Node.Kind == LanNodeKind.VirtualHub)
            {
                // The port's WAN share is weighted by everything the console saw behind it.
                if (membersByHub.TryGetValue(m.Node.Id, out var members))
                    foreach (var member in members)
                        if (dpi.TryGetValue(member.Mac!, out var b)) bytes = (bytes.Down + b.Down, bytes.Up + b.Up);
            }
            else
            {
                dpi.TryGetValue(m.Node.Mac!, out bytes);
            }
            loadsDown.Add(new WanShareReconciler.Load(m.Down, bytes.Down, m.CapDown));
            loadsUp.Add(new WanShareReconciler.Load(m.Up, bytes.Up, m.CapUp));
        }
        var splitDown = wanDownBps is { } wd ? WanShareReconciler.Allocate(wd, loadsDown) : new WanShareReconciler.Split(new double[measured.Count], false);
        var splitUp = wanUpBps is { } wu ? WanShareReconciler.Allocate(wu, loadsUp) : new WanShareReconciler.Split(new double[measured.Count], false);

        var rows = new List<HogRow>(measured.Count);
        for (var i = 0; i < measured.Count; i++)
        {
            var m = measured[i];
            var (viaDevice, viaPort) = Via(m.Node, nodeById);
            var isHub = m.Node.Kind == LanNodeKind.VirtualHub;
            rows.Add(new HogRow
            {
                ClientMac = isHub ? m.Node.Id : m.Node.Mac!,
                Name = isHub ? m.Node.Name : ResolveName(m.Node.Name, snapshot, m.Node.Mac!),
                Ip = m.Node.Ip,
                IsWired = m.Node.Kind != LanNodeKind.WifiClient,
                Band = m.Node.Band,
                ViaDevice = viaDevice,
                ViaPort = isHub ? null : viaPort,
                PortClientCount = isHub && membersByHub.TryGetValue(m.Node.Id, out var members) ? members.Count : 0,
                DownBps = m.Down,
                UpBps = m.Up,
                WanDownBps = splitDown.WanBps[i],
                WanUpBps = splitUp.WanBps[i],
            });
        }

        var (capDownTotal, capUpTotal) = await CapacityAsync(wanKeys, ct);
        return new HogsResult
        {
            Rows = rows,
            WanDownBps = wanDownBps,
            WanUpBps = wanUpBps,
            WanCapacityDownBps = capDownTotal,
            WanCapacityUpBps = capUpTotal,
            WanEstimated = splitDown.Estimated || splitUp.Estimated,
            At = at,
        };
    }

    /// <summary>
    /// Every client's bytes over the window: WAN from the DPI report, and with
    /// <paramref name="includeLan"/> LAN + WAN from our counters as well. The counters are the
    /// expensive half (site-wide scans, or the rollup), so a WAN-only view never pays for them.
    /// A wired client's LAN figure is its switch port's; a port that hosted more than one client
    /// goes to the one present longest and is flagged.
    /// </summary>
    public async Task<HogsResult> GetDataUsageAsync(DateTime from, DateTime to, bool includeLan, CancellationToken ct = default)
    {
        LanFlowMapSnapshot? snapshot = null;
        try { snapshot = await _map.BuildSnapshotAsync(ct); }
        catch (Exception ex) { _logger.LogDebug(ex, "Bandwidth Hogs: no topology snapshot; naming clients from telemetry"); }
        var nodeByMac = new Dictionary<string, LanNode>(StringComparer.OrdinalIgnoreCase);
        var nodeById = new Dictionary<string, LanNode>(StringComparer.OrdinalIgnoreCase);
        if (snapshot != null)
        {
            foreach (var n in snapshot.Nodes)
            {
                nodeById.TryAdd(n.Id, n);
                if (n.Kind is LanNodeKind.WifiClient or LanNodeKind.WiredClient && !string.IsNullOrEmpty(n.Mac))
                    nodeByMac.TryAdd(n.Mac, n);
            }
        }

        var rows = new Dictionary<string, HogRow>(StringComparer.OrdinalIgnoreCase);
        HogRow RowFor(string mac) => rows.TryGetValue(mac, out var r) ? r : rows[mac] = SeedRow(mac, nodeByMac, nodeById, snapshot);

        // WAN, per UniFi Network.
        try
        {
            var traffic = await _dashboard.GetSiteTrafficAsync(from, to, ct);
            foreach (var c in traffic?.ClientUsageByApp ?? new())
            {
                var mac = NormalizeMac(c.Client?.Mac);
                if (mac.Length == 0) continue;
                long down = 0, up = 0;
                foreach (var u in c.UsageByApp) { down += u.BytesReceived; up += u.BytesTransmitted; }
                if (down == 0 && up == 0) continue;
                var row = RowFor(mac);
                rows[mac] = row with
                {
                    Name = row.Name ?? FirstNonEmpty(c.Client?.Name, c.Client?.Hostname),
                    IsWired = nodeByMac.ContainsKey(mac) ? row.IsWired : c.Client?.IsWired ?? row.IsWired,
                    WanDownBytes = down,
                    WanUpBytes = up,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: WAN usage unavailable");
        }

        if (!includeLan)
            return new HogsResult { Rows = rows.Values.ToList(), From = from, To = to };

        // LAN + WAN, wireless: the access point's per-client counters.
        try
        {
            foreach (var t in await WifiTotalsAsync(from, to, ct))
            {
                var row = RowFor(t.ClientMac);
                rows[t.ClientMac] = row with { DownBytes = t.ToClientBytes, UpBytes = t.FromClientBytes, IsWired = false };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: wireless usage unavailable");
        }

        // LAN + WAN, wired: the switch port's counters, given to whoever sat on the port.
        try
        {
            var ports = await PortTotalsAsync(from, to, ct);
            if (ports.Count > 0)
            {
                var occupants = await _influx.QueryWiredPortOccupantsAsync(from, to, ct);
                var ifNamesByPort = await IfNamesByPortAsync(ct);
                foreach (var group in occupants.GroupBy(o => (o.DeviceMac, o.Port)))
                {
                    if (!ifNamesByPort.TryGetValue(group.Key, out var ifNames)) continue;
                    long down = 0, up = 0;
                    var any = false;
                    foreach (var ifName in ifNames)
                    {
                        if (!ports.TryGetValue((group.Key.DeviceMac, ifName), out var total)) continue;
                        down += total.ToClientBytes;
                        up += total.FromClientBytes;
                        any = true;
                    }
                    if (!any || (down == 0 && up == 0)) continue;
                    var switchName = nodeById.TryGetValue("dev-" + group.Key.DeviceMac, out var sw) ? sw.Name : null;
                    var macs = group.Select(o => o.ClientMac).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    if (macs.Count == 1)
                    {
                        var occupant = group.First();
                        var row = RowFor(occupant.ClientMac);
                        rows[occupant.ClientMac] = row with
                        {
                            Name = row.Name ?? occupant.ClientName,
                            Ip = row.Ip ?? occupant.ClientIp,
                            IsWired = true,
                            DownBytes = down,
                            UpBytes = up,
                            ViaDevice = row.ViaDevice ?? switchName,
                            ViaPort = row.ViaPort ?? $"Port {group.Key.Port}",
                        };
                        continue;
                    }
                    // A shared port: one row for the port, named as the map names its hub, with
                    // the WAN bytes of everything behind it. The interfaces keep their own WAN rows.
                    long wanDown = 0, wanUp = 0;
                    string? portName = null;
                    foreach (var mac in macs)
                    {
                        if (rows.TryGetValue(mac, out var member)) { wanDown += member.WanDownBytes; wanUp += member.WanUpBytes; }
                        if (portName == null && nodeByMac.TryGetValue(mac, out var node) && !string.IsNullOrEmpty(node.SwitchPortName))
                            portName = node.SwitchPortName;
                    }
                    var key = $"hub-{group.Key.DeviceMac}-{group.Key.Port}";
                    rows[key] = new HogRow
                    {
                        ClientMac = key,
                        Name = $"{portName ?? $"Port {group.Key.Port}"} ({macs.Count})",
                        IsWired = true,
                        DownBytes = down,
                        UpBytes = up,
                        WanDownBytes = wanDown,
                        WanUpBytes = wanUp,
                        ViaDevice = switchName,
                        PortClientCount = macs.Count,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: wired usage unavailable");
        }

        return new HogsResult { Rows = rows.Values.ToList(), From = from, To = to, IncludesLan = true };
    }

    private static HogRow SeedRow(string mac, Dictionary<string, LanNode> nodeByMac, Dictionary<string, LanNode> nodeById, LanFlowMapSnapshot? snapshot)
    {
        if (!nodeByMac.TryGetValue(mac, out var node))
        {
            string? name = null;
            snapshot?.RecentClientNames.TryGetValue(mac, out name);
            return new HogRow { ClientMac = mac, Name = name };
        }
        var (viaDevice, viaPort) = Via(node, nodeById);
        return new HogRow
        {
            ClientMac = mac,
            Name = ResolveName(node.Name, snapshot, mac),
            Ip = node.Ip,
            IsWired = node.Kind == LanNodeKind.WiredClient,
            Band = node.Band,
            ViaDevice = viaDevice,
            ViaPort = viaPort,
        };
    }

    private static string? ResolveName(string? nodeName, LanFlowMapSnapshot? snapshot, string mac)
    {
        if (!string.IsNullOrWhiteSpace(nodeName)) return nodeName;
        string? recent = null;
        snapshot?.RecentClientNames.TryGetValue(mac, out recent);
        return recent;
    }

    private static (string? Device, string? Port) Via(LanNode node, Dictionary<string, LanNode> nodeById)
    {
        var parentId = node.ParentId;
        // A virtual hub is a port with several MACs on it; the switch behind it is what to name.
        if (parentId != null && nodeById.TryGetValue(parentId, out var parent) && parent.Kind == LanNodeKind.VirtualHub)
            parentId = parent.ParentId;
        var device = parentId != null && nodeById.TryGetValue(parentId, out var dev) ? dev.Name : null;
        return (device, node.Kind == LanNodeKind.WiredClient ? node.SwitchPortName : null);
    }

    /// <summary>
    /// The least any hop between the client's device and the gateway carried, with the reconciler's
    /// threshold as headroom so counters read a few seconds apart do not clip a client below its
    /// own rate. A hop with no rate says nothing and is skipped.
    /// </summary>
    private static (double? Down, double? Up) ChainCap(
        string? parentId, Dictionary<string, LanNode> nodeById,
        Dictionary<string, List<LanLink>> linksInto, IReadOnlyDictionary<string, LinkLiveRates> rates)
    {
        double? down = null, up = null;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = parentId;
        while (current != null && visited.Add(current) && nodeById.TryGetValue(current, out var node))
        {
            if (node.Kind == LanNodeKind.Gateway) break;
            if (linksInto.TryGetValue(node.Id, out var into))
            {
                var uplink = into.FirstOrDefault(l => l.Kind is LanLinkKind.Uplink or LanLinkKind.MeshBackhaul);
                if (uplink != null && rates.TryGetValue(uplink.Id, out var r))
                {
                    if (r.DownstreamBps > 0) down = Math.Min(down ?? double.MaxValue, r.DownstreamBps);
                    if (r.UpstreamBps > 0) up = Math.Min(up ?? double.MaxValue, r.UpstreamBps);
                }
            }
            current = node.ParentId;
        }
        var headroom = 1 + WanShareReconciler.Threshold;
        return (down * headroom, up * headroom);
    }

    private async Task<Dictionary<string, (double Down, double Up)>> DpiTotalsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var totals = new Dictionary<string, (double Down, double Up)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var traffic = await _dashboard.GetSiteTrafficAsync(from, to, ct);
            foreach (var c in traffic?.ClientUsageByApp ?? new())
            {
                var mac = NormalizeMac(c.Client?.Mac);
                if (mac.Length == 0) continue;
                double down = 0, up = 0;
                foreach (var u in c.UsageByApp) { down += u.BytesReceived; up += u.BytesTransmitted; }
                totals[mac] = (down, up);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: DPI report unavailable; WAN split weighted by rate");
        }
        return totals;
    }

    private async Task<(double? Down, double? Up)> CapacityAsync(IReadOnlyCollection<string> wanKeys, CancellationToken ct)
    {
        if (wanKeys.Count == 0) return (null, null);
        try
        {
            var groups = wanKeys.Select(k => GatewayWanHelper.WanNetworkGroupFromKey(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await using var db = _siteDb.CreateForSite(_site.Slug, _site.IsDefault);
            var profiles = await db.WanProfiles.AsNoTracking().ToListAsync(ct);
            double? down = null, up = null;
            foreach (var p in profiles.Where(p => groups.Contains(p.WanNetworkgroup)))
            {
                if (p.DownloadMbps is > 0) down = (down ?? 0) + p.DownloadMbps.Value * 1_000_000;
                if (p.UploadMbps is > 0) up = (up ?? 0) + p.UploadMbps.Value * 1_000_000;
            }
            return (down, up);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: WAN capacity unavailable");
            return (null, null);
        }
    }

    /// <summary>The Data tab's rule: counters for a short window, the hourly rollup topped up from
    /// counters for the hour in progress otherwise, counters again when there is no rollup yet.</summary>
    private async Task<IReadOnlyList<MonitoringInfluxClient.ClientByteTotal>> WifiTotalsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var span = to - from;
        if (span <= CounterWindow)
            return await _influx.QueryAllWifiClientByteUsageAsync(from, to, ct);

        var hourStart = new DateTime(to.Year, to.Month, to.Day, to.Hour, 0, 0, DateTimeKind.Utc);
        var rolled = await _influx.QueryAllWifiClientUsageRollupAsync(from, hourStart, ct);
        if (rolled.Totals.Count == 0)
            return span <= CounterFallbackMax
                ? await _influx.QueryAllWifiClientByteUsageAsync(from, to, ct)
                : Array.Empty<MonitoringInfluxClient.ClientByteTotal>();

        var topUpFrom = rolled.LastHour is { } last ? last.AddHours(1) : hourStart;
        if (topUpFrom < hourStart - RollupTopUpMax) topUpFrom = hourStart - RollupTopUpMax;
        var tail = await _influx.QueryAllWifiClientByteUsageAsync(topUpFrom, to, ct);
        return Merge(rolled.Totals, tail, t => t.ClientMac,
            (a, b) => new MonitoringInfluxClient.ClientByteTotal(a.ClientMac, a.ToClientBytes + b.ToClientBytes, a.FromClientBytes + b.FromClientBytes));
    }

    private async Task<Dictionary<(string DeviceMac, string IfName), MonitoringInfluxClient.PortByteTotal>> PortTotalsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var span = to - from;
        IReadOnlyList<MonitoringInfluxClient.PortByteTotal> totals;
        if (span <= CounterWindow)
        {
            totals = await _influx.QueryAllPortByteUsageAsync(from, to, ct);
        }
        else
        {
            var hourStart = new DateTime(to.Year, to.Month, to.Day, to.Hour, 0, 0, DateTimeKind.Utc);
            var rolled = await _influx.QueryAllPortUsageRollupAsync(from, hourStart, ct);
            if (rolled.Totals.Count == 0)
            {
                totals = span <= CounterFallbackMax
                    ? await _influx.QueryAllPortByteUsageAsync(from, to, ct)
                    : Array.Empty<MonitoringInfluxClient.PortByteTotal>();
            }
            else
            {
                var topUpFrom = rolled.LastHour is { } last ? last.AddHours(1) : hourStart;
                if (topUpFrom < hourStart - RollupTopUpMax) topUpFrom = hourStart - RollupTopUpMax;
                var tail = await _influx.QueryAllPortByteUsageAsync(topUpFrom, to, ct);
                totals = Merge(rolled.Totals, tail, t => (t.DeviceMac, t.IfName),
                    (a, b) => new MonitoringInfluxClient.PortByteTotal(a.DeviceMac, a.IfName, a.ToClientBytes + b.ToClientBytes, a.FromClientBytes + b.FromClientBytes));
            }
        }
        var result = new Dictionary<(string, string), MonitoringInfluxClient.PortByteTotal>();
        foreach (var t in totals)
        {
            // Sub-interfaces double count the port they ride on.
            if (t.IfName.Contains('.')) continue;
            result[(t.DeviceMac, t.IfName)] = t;
        }
        return result;
    }

    private static List<T> Merge<T, TKey>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, TKey> key, Func<T, T, T> add) where TKey : notnull
    {
        var merged = new Dictionary<TKey, T>();
        foreach (var t in a) merged[key(t)] = t;
        foreach (var t in b) merged[key(t)] = merged.TryGetValue(key(t), out var existing) ? add(existing, t) : t;
        return merged.Values.ToList();
    }

    private async Task<Dictionary<(string DeviceMac, int Port), List<string>>> IfNamesByPortAsync(CancellationToken ct)
    {
        var result = new Dictionary<(string, int), List<string>>();
        await using var db = _siteDb.CreateForSite(_site.Slug, _site.IsDefault);
        var maps = await db.InterfaceNameMaps.AsNoTracking()
            .Where(m => m.PortNumber != null)
            .Select(m => new { m.DeviceMac, m.PortNumber, m.IfName })
            .ToListAsync(ct);
        foreach (var m in maps)
        {
            var key = (NormalizeMac(m.DeviceMac), m.PortNumber!.Value);
            if (!result.TryGetValue(key, out var list)) result[key] = list = new List<string>();
            list.Add(m.IfName);
        }
        return result;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string NormalizeMac(string? mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');
}
