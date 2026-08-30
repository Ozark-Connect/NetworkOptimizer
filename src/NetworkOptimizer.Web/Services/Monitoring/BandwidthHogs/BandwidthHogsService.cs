using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
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
    private readonly UniFiConnectionService _connection;
    private readonly MonitoringLiveStatsRegistry _liveStats;
    private readonly ILogger<BandwidthHogsService> _logger;

    /// <summary>How far back the DPI report is read to weight a live WAN split.</summary>
    private static readonly TimeSpan DpiRecentWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Less history than this and no baseline is claimed. Only as long as the console needs to
    /// corroborate a WAN flow (its rates catch one within a minute): a baseline computed over a
    /// shorter span could call a WAN flow local before the ceiling knew about it, and that error
    /// self-heals within the console's lag either way. The histories accumulate in the site's
    /// live cache as the sources write (MonitoringLiveStats.RowRateHistory) - no page needed.
    /// </summary>
    private static readonly TimeSpan BaselineMinSpan = TimeSpan.FromSeconds(90);

    /// <summary>How long a persisted baseline may stand in for a live one after a restart.</summary>
    private static readonly TimeSpan RowBaselineSeedLife = TimeSpan.FromHours(24);

    /// <summary>A live console rate older than this says nothing about now.</summary>
    private static readonly TimeSpan ConsoleNowFreshness = TimeSpan.FromSeconds(90);

    /// <summary>
    /// A client the console has known for this long, that moved under <see cref="ExclusionFloorBytes"/>
    /// through the WAN in it and nothing in the recent window, is not a WAN user: a camera streaming
    /// to a local NVR, a hypervisor's management interface. It is left out of the WAN split - and
    /// out of the "does it add up" sum, where its local traffic otherwise forced an estimate.
    /// A device younger than this is never excluded; it has not had time to show what it does.
    /// </summary>
    private static readonly TimeSpan ExclusionLookback = TimeSpan.FromHours(24);
    private const long ExclusionFloorBytes = 1_000_000;

    private static readonly TimeSpan FirstSeenCacheFor = TimeSpan.FromMinutes(5);
    private (DateTime At, Dictionary<string, DateTime> Map)? _firstSeen;

    /// <summary>Temporary: last time the split diagnostics were logged (see GetThroughputAsync).</summary>
    private DateTime _lastSplitLog;

    /// <summary>
    /// Raw counters answer up to here, the rollup past it. Kept at the top-up's own reach: a
    /// counter query reads every point in the window (client identity is a field, not a tag), so
    /// six hours of raw scan was seconds of Flux for a totals view the hourly rollup answers.
    /// </summary>
    private static readonly TimeSpan CounterWindow = TimeSpan.FromHours(2);

    /// <summary>
    /// Past this, no rollup means best-effort partials rather than a counter scan: the site-wide
    /// port counter query reads every interface's samples (48 s over 24 h at a 2-second fast
    /// tier), so while a rollup rebuild runs, windows up to a day stay exact through the counters
    /// and longer ones show what is rolled so far.
    /// </summary>
    private static readonly TimeSpan CounterFallbackMax = TimeSpan.FromHours(24);

    private static readonly TimeSpan RollupTopUpMax = TimeSpan.FromHours(2);

    /// <summary>Assembled Data results, shared across pages (the service is scoped per page).</summary>
    private static readonly ConcurrentDictionary<(string Site, long SpanMinutes, long SlotMinutes, bool Lan), (DateTime At, HogsResult Result)> DataResultCache = new();
    private static readonly TimeSpan DataResultCacheFor = TimeSpan.FromMinutes(1);

    private static HogsResult CacheDataResult((string, long, long, bool) key, HogsResult result)
    {
        DataResultCache[key] = (DateTime.UtcNow, result);
        if (DataResultCache.Count > 64)
            foreach (var stale in DataResultCache.Where(kv => DateTime.UtcNow - kv.Value.At > TimeSpan.FromMinutes(5)).Select(kv => kv.Key).ToList())
                DataResultCache.TryRemove(stale, out _);
        return result;
    }

    public BandwidthHogsService(
        LanFlowMapService map,
        ClientDashboardService dashboard,
        MonitoringInfluxClient influx,
        SiteDbContextFactory siteDb,
        SiteContextService site,
        UniFiConnectionService connection,
        MonitoringLiveStatsRegistry liveStats,
        ILogger<BandwidthHogsService> logger)
    {
        _map = map;
        _dashboard = dashboard;
        _influx = influx;
        _siteDb = siteDb;
        _site = site;
        _connection = connection;
        _liveStats = liveStats;
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

        var measured = new List<(LanNode Node, double Down, double Up, double? CapDown, double? CapUp, string? HistoryKey)>();
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
            // Where this row's rate history lives in the live cache: the client's own Wi-Fi
            // throughput, its port's rate (also the hub case), or the wired fallback.
            var historyKey = link.Kind == LanLinkKind.WifiClient && node.Mac != null
                ? MonitoringLiveStats.WifiRowKey(node.Mac)
                : link.PortKey is { Length: > 0 } pk && pk.IndexOf('|') is var sep && sep > 0
                    ? MonitoringLiveStats.PortRowKey(pk[..sep], pk[(sep + 1)..])
                    : node.Mac != null ? MonitoringLiveStats.WiredRowKey(node.Mac) : null;
            measured.Add((node, Math.Max(0, rate.DownstreamBps), Math.Max(0, rate.UpstreamBps), capDown, capUp, historyKey));
        }

        // Live weights by the last quarter hour. At the playhead the window snaps to quarter-hour
        // boundaries, so every position inside one shares a single cached DPI report.
        var end = at is { } a ? new DateTime(a.Ticks - a.Ticks % DpiRecentWindow.Ticks, DateTimeKind.Utc) : DateTime.UtcNow;
        var dpi = await DpiTotalsAsync(end - DpiRecentWindow, end, ct);
        var history = await DpiTotalsAsync(end - ExclusionLookback, end, ct);
        var firstSeen = await FirstSeenAsync(ct);

        bool NotAWanUser(string mac) => IsNotAWanUser(
            firstSeen.TryGetValue(mac, out var seen) ? seen : null,
            history.TryGetValue(mac, out var h) ? h.Down + h.Up : 0,
            dpi.TryGetValue(mac, out var r) ? r.Down + r.Up : 0,
            end, ExclusionLookback, ExclusionFloorBytes);

        var included = new List<int>(measured.Count);
        var loadsDown = new List<WanShareReconciler.Load>(measured.Count);
        var loadsUp = new List<WanShareReconciler.Load>(measured.Count);
        // Live only; at the playhead the split runs on DPI weights alone. Per-row source
        // hierarchy, best evidence first:
        //   1. (Future) the gateway agent's conntrack-measured WAN: exact from its first report,
        //      a covered row bypasses everything below - see gateway-conntrack-spec.md.
        //   2. A baseline armed from the live histories, or the persisted one a restart reloaded:
        //      the baseline comes off the rate and the rest is the WAN candidate.
        //   3. Nothing learned: attribute only what the console's own signals corroborate, which
        //      survive OUR restarts because they are console-side.
        var liveStats = at == null ? _liveStats.GetFor(_site.Slug) : null;
        var now = DateTime.UtcNow;
        (double Down, double Up, bool Known, double? Floor, double? Ceiling) BaselineLocal(string? historyKey, IReadOnlyList<string> macs)
        {
            if (liveStats == null) return (0, 0, true, null, null);
            if (historyKey == null) return (0, 0, true, null, null);
            var samples = liveStats.RowRateHistory(historyKey);
            var floor = samples.Any(s => now - s.At >= BaselineMinSpan) ? Percentile90(samples.Select(s => s.Down)) : (double?)null;
            var histories = macs.Select(liveStats.ConsoleRateHistory).ToList();
            var ceiling = ConsoleWanCeiling(histories, now, BaselineMinSpan);
            if (floor != null && ceiling is { } c)
            {
                var down = BaselineLocalBps(samples.Select(s => (s.At, s.Down)).ToList(), c.Down, now, BaselineMinSpan);
                var up = BaselineLocalBps(samples.Select(s => (s.At, s.Up)).ToList(), c.Up, now, BaselineMinSpan);
                liveStats.RecordRowBaseline(historyKey, down, up, now);
                return (down, up, true, floor, c.Down);
            }
            if (liveStats.GetRowBaseline(historyKey, RowBaselineSeedLife) is { } seed)
                return (seed.DownBps, seed.UpBps, true, floor, ceiling?.Down);
            return (0, 0, false, floor, ceiling?.Down);
        }
        (double Down, double Up)? ConsoleNow(IReadOnlyList<string> macs)
        {
            if (liveStats == null) return null;
            (double Down, double Up)? sum = null;
            foreach (var mac in macs)
                if (liveStats.GetConsoleWanRate(mac, ConsoleNowFreshness) is { } r)
                    sum = ((sum?.Down ?? 0) + r.DownBps, (sum?.Up ?? 0) + r.UpBps);
            return sum;
        }

        // TEMPORARY diagnostics for the NVR mis-attribution investigation: one block every 10 s
        // per direction pair, showing each row's inputs and what it was attributed. Remove once
        // the split's behavior is settled.
        var logThis = at == null && _logger.IsEnabled(LogLevel.Debug) && now - _lastSplitLog >= TimeSpan.FromSeconds(10);
        if (logThis) _lastSplitLog = now;
        var diag = logThis ? new List<string>() : null;

        for (var i = 0; i < measured.Count; i++)
        {
            var m = measured[i];
            (double Down, double Up) bytes = default;
            var macs = new List<string>();
            bool excluded;
            if (m.Node.Kind == LanNodeKind.VirtualHub)
            {
                // The port's WAN share is weighted by everything the console saw behind it, and
                // the port is a WAN user if any interface on it is. Its console history is the
                // sum of its interfaces'.
                membersByHub.TryGetValue(m.Node.Id, out var members);
                members ??= new List<LanNode>();
                foreach (var member in members)
                {
                    if (dpi.TryGetValue(member.Mac!, out var b)) bytes = (bytes.Down + b.Down, bytes.Up + b.Up);
                    macs.Add(member.Mac!);
                }
                excluded = members.Count > 0 && members.All(member => NotAWanUser(member.Mac!));
            }
            else
            {
                dpi.TryGetValue(m.Node.Mac!, out bytes);
                macs.Add(m.Node.Mac!);
                excluded = NotAWanUser(m.Node.Mac!);
            }
            if (excluded) continue;
            var baseline = BaselineLocal(m.HistoryKey, macs);
            double effDown, effUp;
            if (baseline.Known)
            {
                effDown = Math.Max(0, m.Down - baseline.Down);
                effUp = Math.Max(0, m.Up - baseline.Up);
            }
            else
            {
                var console = ConsoleNow(macs);
                effDown = Math.Min(m.Down, UnarmedWanCapBps(bytes.Down, DpiRecentWindow, console?.Down));
                effUp = Math.Min(m.Up, UnarmedWanCapBps(bytes.Up, DpiRecentWindow, console?.Up));
            }
            included.Add(i);
            loadsDown.Add(new WanShareReconciler.Load(effDown, bytes.Down, m.CapDown));
            loadsUp.Add(new WanShareReconciler.Load(effUp, bytes.Up, m.CapUp));
            diag?.Add($"{m.Node.Name ?? m.Node.Mac} rate={m.Down / 1e6:F1}/{m.Up / 1e6:F1}Mbps floorDn={(baseline.Floor is { } f ? (f / 1e6).ToString("F1") : "none")} consCeilDn={(baseline.Ceiling is { } c ? (c / 1e6).ToString("F2") : "none")} baseDn={baseline.Down / 1e6:F1}{(baseline.Known ? "" : " unarmed")} effDn={effDown / 1e6:F1} dpiDn={bytes.Down / 1e6:F0}MB");
        }
        var splitDown = wanDownBps is { } wd ? WanShareReconciler.Allocate(wd, loadsDown) : new WanShareReconciler.Split(new double[included.Count], false);
        var splitUp = wanUpBps is { } wu ? WanShareReconciler.Allocate(wu, loadsUp) : new WanShareReconciler.Split(new double[included.Count], false);
        if (diag != null)
        {
            for (var j = 0; j < included.Count; j++)
                diag[j] += $" -> wanDn={splitDown.WanBps[j] / 1e6:F1} wanUp={splitUp.WanBps[j] / 1e6:F1}";
            _logger.LogDebug("Hogs split wanDn={WanDown:F1}Mbps wanUp={WanUp:F1}Mbps estDn={EstDown} estUp={EstUp}\n  {Rows}",
                (wanDownBps ?? 0) / 1e6, (wanUpBps ?? 0) / 1e6, splitDown.Estimated, splitUp.Estimated, string.Join("\n  ", diag));
        }
        var wanDown = new double[measured.Count];
        var wanUp = new double[measured.Count];
        for (var j = 0; j < included.Count; j++)
        {
            wanDown[included[j]] = splitDown.WanBps[j];
            wanUp[included[j]] = splitUp.WanBps[j];
        }

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
                WanDownBps = wanDown[i],
                WanUpBps = wanUp[i],
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
            WarmupSecondsRemaining = liveStats == null
                ? 0
                : (int)Math.Max(0, Math.Ceiling((liveStats.StartedAt + BaselineMinSpan - now).TotalSeconds)),
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
        // The card refreshes every one to five minutes and each assembly pays the port top-up
        // and the DPI report; one minute of reuse makes refreshes free while a window switch
        // still computes at once. Static: the card's service is scoped per page.
        var cacheKey = (_site.Slug, (long)(to - from).TotalMinutes, (long)(to - DateTime.UnixEpoch).TotalMinutes, includeLan);
        if (DataResultCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At <= DataResultCacheFor)
            return hit.Result;
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

        // A MAC nothing can identify is not a client of ours - UniFi's traffic report can list
        // the gateway's WAN-side L2 neighbor (the ISP's edge). Unnamed, off-map, un-listed: dropped.
        var known = await FirstSeenAsync(ct);
        List<HogRow> Resolved() => rows.Values
            .Where(r => r.Name != null || nodeByMac.ContainsKey(r.ClientMac) || known.ContainsKey(r.ClientMac))
            .ToList();

        if (!includeLan)
            return CacheDataResult(cacheKey, new HogsResult { Rows = Resolved(), From = from, To = to });

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
                    // Over a long window a port hosts clients in succession, not concurrency: a
                    // device that moved away months of samples ago must not turn the port into a
                    // phantom "(5)" hub carrying mixed traffic and linking to a past tenant. The
                    // sample counts say who actually lived there; only a port with no dominant
                    // occupant is genuinely shared.
                    if (DominantOccupant(group.ToList()) is { } occupant)
                    {
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
                    // The row links to the interface with the most WAN traffic, as the map's hub does.
                    string? representativeIp = null;
                    long representativeBytes = -1;
                    foreach (var mac in macs)
                    {
                        long memberBytes = 0;
                        string? memberIp = null;
                        if (rows.TryGetValue(mac, out var member))
                        {
                            wanDown += member.WanDownBytes;
                            wanUp += member.WanUpBytes;
                            memberBytes = member.WanDownBytes + member.WanUpBytes;
                            memberIp = member.Ip;
                        }
                        memberIp ??= group.FirstOrDefault(o => string.Equals(o.ClientMac, mac, StringComparison.OrdinalIgnoreCase))?.ClientIp;
                        // Most WAN bytes wins; with nothing to go on, the lowest IP, as the map's hub picks.
                        if (!string.IsNullOrEmpty(memberIp)
                            && (memberBytes > representativeBytes
                                || (memberBytes == representativeBytes && representativeIp != null
                                    && NetworkUtilities.IpSortKey(memberIp).CompareTo(NetworkUtilities.IpSortKey(representativeIp)) < 0)))
                        {
                            representativeBytes = memberBytes;
                            representativeIp = memberIp;
                        }
                        if (portName == null && nodeByMac.TryGetValue(mac, out var node) && !string.IsNullOrEmpty(node.SwitchPortName))
                            portName = node.SwitchPortName;
                    }
                    var key = $"hub-{group.Key.DeviceMac}-{group.Key.Port}";
                    rows[key] = new HogRow
                    {
                        ClientMac = key,
                        Name = $"{portName ?? $"Port {group.Key.Port}"} ({macs.Count})",
                        Ip = representativeIp,
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

        return CacheDataResult(cacheKey, new HogsResult { Rows = Resolved(), From = from, To = to, IncludesLan = true });
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
    /// The most WAN the console's history could explain of a row: per direction, the sum over its
    /// client(s) of each history's maximum. Null unless every client's history spans
    /// <paramref name="minSpan"/> - a client the console has not covered leaves the whole row's
    /// baseline unclaimed rather than understated.
    /// </summary>
    public static (double Down, double Up)? ConsoleWanCeiling(
        IReadOnlyList<IReadOnlyList<(DateTime At, double Down, double Up)>> histories,
        DateTime now, TimeSpan minSpan)
    {
        if (histories.Count == 0) return null;
        double down = 0, up = 0;
        foreach (var h in histories)
        {
            if (!h.Any(s => now - s.At >= minSpan)) return null;
            down += h.Max(s => s.Down);
            up += h.Max(s => s.Up);
        }
        return (down, up);
    }

    /// <summary>
    /// A row's baseline local rate in one direction: the level its measured rate held across the
    /// history, less the most the console's WAN figure explained of it. The level is the 90th
    /// percentile, not the minimum: a camera feed wobbles across a wide band (13-37 Mbps
    /// observed), and a min-based baseline left the wobble above it as 10-22 Mbps of standing
    /// phantom candidacy. p90 sits at the top of the band while one burst sample among many
    /// cannot drag it up the way a max would. A bursty client's p90 is still ~its idle level, and
    /// anything above the baseline is a WAN candidate as usual. Zero until the measured history
    /// spans <paramref name="minSpan"/>: a flow we have not watched is a WAN candidate, never
    /// quietly ruled local.
    /// </summary>
    public static double BaselineLocalBps(
        IReadOnlyList<(DateTime At, double Bps)> measured,
        double consoleCeilingBps,
        DateTime now, TimeSpan minSpan)
    {
        if (measured.Count == 0 || !measured.Any(s => now - s.At >= minSpan)) return 0;
        return Math.Max(0, Percentile90(measured.Select(s => s.Bps)) - consoleCeilingBps);
    }

    /// <summary>Lower-interpolation p90: sorted[floor(0.9 * (n-1))], so small sample sets pick a
    /// held value rather than a lone spike.</summary>
    public static double Percentile90(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? 0 : sorted[(int)Math.Floor(0.9 * (sorted.Length - 1))];
    }

    /// <summary>
    /// With nothing learned about a row (a truly cold start), its WAN candidacy is capped at what
    /// the console's own signals corroborate: twice the larger of its recent DPI rate and its
    /// live console rate. Both are console-side, so they survive our restarts - a local-heavy
    /// device reads ~0 from the very first split, at the cost of a brand-new burst
    /// under-attributing for the half-minute the console needs to see it.
    /// </summary>
    public static double UnarmedWanCapBps(double dpiRecentBytes, TimeSpan dpiWindow, double? consoleBps) =>
        2 * Math.Max(Math.Max(0, dpiRecentBytes) * 8 / dpiWindow.TotalSeconds, Math.Max(0, consoleBps ?? 0));

    /// <summary>
    /// The client a port's usage belongs to: the sole occupant, or one holding at least nine of
    /// every ten occupancy samples over the window (the rest are passers-by or past tenants).
    /// Null when the port is genuinely shared - several clients each with a real presence.
    /// </summary>
    public static MonitoringInfluxClient.WiredPortOccupant? DominantOccupant(
        IReadOnlyList<MonitoringInfluxClient.WiredPortOccupant> occupants)
    {
        if (occupants.Count == 0) return null;
        if (occupants.Count == 1) return occupants[0];
        var total = occupants.Sum(o => (long)o.Samples);
        var top = occupants.OrderByDescending(o => o.Samples).First();
        return total > 0 && top.Samples * 10L >= total * 9 ? top : null;
    }

    /// <summary>
    /// The never-touches-the-WAN exclusion: known to the console since before the lookback, under
    /// the byte floor across it, and nothing at all in the recent window. Null first-seen (an
    /// unknown client) never excludes.
    /// </summary>
    public static bool IsNotAWanUser(DateTime? firstSeen, double lookbackBytes, double recentBytes, DateTime end, TimeSpan lookback, long floorBytes) =>
        firstSeen is { } seen && seen <= end - lookback && lookbackBytes < floorBytes && recentBytes <= 0;

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

    /// <summary>When the console first saw each connected client, read once per
    /// <see cref="FirstSeenCacheFor"/>. Empty when the console cannot answer, which excludes nobody.</summary>
    private async Task<Dictionary<string, DateTime>> FirstSeenAsync(CancellationToken ct)
    {
        if (_firstSeen is { } cached && DateTime.UtcNow - cached.At < FirstSeenCacheFor) return cached.Map;
        var map = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (_connection.IsConnected && _connection.Client != null)
            {
                foreach (var c in await _connection.Client.GetClientsAsync(ct))
                {
                    var mac = NormalizeMac(c.Mac);
                    if (mac.Length > 0 && c.FirstSeen > 0)
                        map[mac] = DateTimeOffset.FromUnixTimeSeconds(c.FirstSeen).UtcDateTime;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bandwidth Hogs: client list unavailable; nothing excluded from WAN");
        }
        _firstSeen = (DateTime.UtcNow, map);
        return map;
    }

    private static readonly TimeSpan CapacityCacheFor = TimeSpan.FromMinutes(5);
    private (string Keys, DateTime At, (double? Down, double? Up) Value)? _capacity;

    /// <summary>The selected WANs' expected speeds, read once per <see cref="CapacityCacheFor"/>:
    /// they change when someone edits the WAN, not every three seconds.</summary>
    private async Task<(double? Down, double? Up)> CapacityAsync(IReadOnlyCollection<string> wanKeys, CancellationToken ct)
    {
        if (wanKeys.Count == 0) return (null, null);
        var signature = string.Join(",", wanKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
        if (_capacity is { } cached && cached.Keys == signature && DateTime.UtcNow - cached.At < CapacityCacheFor)
            return cached.Value;
        var value = await ReadCapacityAsync(wanKeys, ct);
        _capacity = (signature, DateTime.UtcNow, value);
        return value;
    }

    private async Task<(double? Down, double? Up)> ReadCapacityAsync(IReadOnlyCollection<string> wanKeys, CancellationToken ct)
    {
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
        // Partial coverage is worse than none: a rebuild rolls newest first, and totals over its
        // uncovered early hours read silently low, not empty. Not reaching the window's start is
        // treated exactly like having no rollup.
        if (rolled.Totals.Count == 0 || rolled.FirstHour is not { } firstWifi || firstWifi > from.AddHours(1))
            return span <= CounterFallbackMax
                ? await _influx.QueryAllWifiClientByteUsageAsync(from, to, ct)
                : rolled.Totals;

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
            // Same partial-coverage rule as the wireless side: a rollup that does not reach the
            // window's start is no rollup.
            if (rolled.Totals.Count == 0 || rolled.FirstHour is not { } firstPort || firstPort > from.AddHours(1))
            {
                totals = span <= CounterFallbackMax
                    ? await _influx.QueryAllPortByteUsageAsync(from, to, ct)
                    : rolled.Totals;
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
