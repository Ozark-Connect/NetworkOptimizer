using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Point-in-time per-port statistics for the Live View port playback table.
/// Reads <c>interface_counters</c> at the current map scrubber position (or the
/// latest sample when no instant is supplied) and enriches each port with the
/// UniFi-correlated friendly name, port number, media type and negotiated link
/// speed from <see cref="InterfaceNameMap"/>.
/// </summary>
public static class PortStatsEndpoints
{
    // Matches a VLAN sub-interface such as "eth0.100" or "switch0.99": a base
    // interface name followed by a dotted numeric VLAN id.
    private static readonly Regex SubInterface = new(@"^(?<base>.+)\.(?<vlan>\d+)$", RegexOptions.Compiled);

    // Switch SNMP ifTables expose logical interfaces - link aggregations, VLAN SVIs, and
    // stack / tunnel / VPN / user-defined ports - alongside the real physical ports. They are
    // noise in the port stats table. Real ports ("Port 1", "SFP+ 1") and renamed ports never
    // match these patterns, and VLAN sub-interfaces ("eth0.100") keep their dotted form. This
    // is a reader-side display filter only; nothing is removed from InfluxDB or the live cache.
    private static readonly Regex JunkSwitchInterface = new(
        @"^(Port-Channel\d+|Logical-int \d+|User Defined Port \d+|stack-port|tunnel\d+|OpenVPN|\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // How far back a historic scrub looks for a port's last client: wired port presence's lookback, so a WoL NIC
    // listed only when its host last woke still names its port. last() is pushed down, so the span costs little.
    private static readonly TimeSpan HistoricLastClientLookback = NetworkOptimizer.Web.Services.WiredPortPresence.WiredPortPresenceRule.Lookback;

    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/port-stats", async (
            MonitoringInfluxClient influx,
            MonitoringLiveStats liveStats,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
            UniFiConnectionService connectionService,
            NetworkOptimizer.Web.Services.Monitoring.LanHealth.ILanHealthService lanHealth,
            NetworkOptimizer.Web.Services.LanFlowMap.LanFlowMapCache mapCache,
            NetworkOptimizer.Web.Services.WiredPortPresence.IWiredPortPresenceService portPresence,
            string? macs,
            DateTime? at,
            CancellationToken ct) =>
        {
            var requested = (macs ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var filterMacs = requested.Count > 0 ? requested : null;

            // Live mode is served from the in-memory cache (sub-ms, no InfluxDB load);
            // only historic scrubbing (an explicit timestamp) hits InfluxDB.
            IReadOnlyList<MonitoringInfluxClient.PortStatsPoint> points = at.HasValue
                ? await influx.QueryPortStatsAsync(filterMacs, at.Value.ToUniversalTime(), ct)
                : liveStats.GetPortStatsSnapshot(filterMacs);

            // Historic scrub resolves the Client column from port-tagged wired_client
            // points at the same instant; live keeps the in-memory map. Pre-2.0 history
            // has no port tag and simply yields no client labels.
            Dictionary<(string Mac, int Port), MonitoringInfluxClient.WiredPortClientPoint>? histClients = null;
            IReadOnlyDictionary<(string DeviceMac, int Port), int> histShared = new Dictionary<(string, int), int>();
            if (at.HasValue)
            {
                var window = await influx.QueryWiredPortClientsWindowAsync(filterMacs, at.Value.ToUniversalTime(), ct);
                histClients = window.Clients.ToDictionary(p => (Norm(p.DeviceMac), p.Port));
                histShared = window.SharedPorts;
            }

            // Live: a client the console dropped while its port stayed up, as the LAN map draws it on that port.
            var presentByPort = new Dictionary<(string Mac, int Port), NetworkOptimizer.Web.Services.WiredPortPresence.WiredPortPresence>();
            if (!at.HasValue)
            {
                try
                {
                    foreach (var p in await portPresence.ListAsync())
                        presentByPort.TryAdd((Norm(p.SwitchMac), p.Port), p);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* The column shows listed clients only. */ }
            }
            // The LAN map's client history names, for a client LAN Health has no link for (a past occupant on a
            // historic scrub). Read off the cached snapshot: never a rebuild.
            var recentNames = mapCache.Current?.RecentClientNames;

            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);

            // Device display name + type ("ap" / "switch" / "gateway") from the fabric
            // monitoring targets, matching how the device health chart resolves names.
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.RetiredAt == null && t.DeviceMac != null)
                .Select(t => new { t.DeviceMac, t.Name, t.AutoLabel })
                .ToListAsync(ct);
            var targetByMac = targets
                .GroupBy(t => Norm(t.DeviceMac!))
                .ToDictionary(g => g.Key, g => g.First());

            // UniFi-correlated interface metadata keyed by (mac, ifName).
            var nameMaps = await db.InterfaceNameMaps.AsNoTracking().ToListAsync(ct);
            var mapByKey = new Dictionary<(string mac, string ifName), InterfaceNameMap>();
            var friendlyByMacIf = new Dictionary<(string mac, string ifName), string>();
            foreach (var m in nameMaps)
            {
                var k = (Norm(m.DeviceMac), m.IfName);
                mapByKey[k] = m;
                if (!string.IsNullOrWhiteSpace(m.FriendlyName))
                    friendlyByMacIf[k] = m.FriendlyName!;
            }

            var poeByPort = at.HasValue
                ? await HistoricPoeAsync(influx, at.Value, mapByKey, ct)
                : await LivePoeAsync(connectionService, ct);

            // Each port's LAN Health link (today's), so a row opens its link page. Empty when LAN Health has none yet.
            var lanByPort = new Dictionary<(string Mac, int Port), NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkHealth>();
            try
            {
                var report = await lanHealth.GetReportAsync(48, TimeZoneInfo.Utc, NetworkOptimizer.Core.Helpers.DisplayFormat.Fallback, ct);
                lanByPort = NetworkOptimizer.Web.Services.Monitoring.LanHealth.LanHealthLinkMatch.ByPort(report.Links);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { /* The table renders without the links. */ }

            // Historic: a port with no client at the instant (asleep, unlisted) shows the last one recorded on it
            // before then, as the live table shows the port's last client. A port shared at the instant stays empty.
            if (histClients != null && at.HasValue)
            {
                var empty = points
                    .Select(p => (Mac: Norm(p.DeviceMac), Port: mapByKey.GetValueOrDefault((Norm(p.DeviceMac), p.IfName))?.PortNumber))
                    .Where(p => p.Port is int n && !histClients.ContainsKey((p.Mac, n)) && !histShared.ContainsKey((p.Mac, n))
                        && !IsFabricPort(lanByPort, p.Mac, n))
                    .Select(p => (p.Mac, p.Port!.Value)).Distinct().ToList();
                try
                {
                    foreach (var last in await influx.QueryLastWiredPortClientsAsync(empty, at.Value.ToUniversalTime(), HistoricLastClientLookback, ct))
                        histClients.TryAdd((Norm(last.DeviceMac), last.Port), last);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { /* The column shows the instant's clients only. */ }
            }

            NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkRecord? LanClient(string deviceMac, int port) =>
                LanHealthPortClient(lanByPort, deviceMac, port);
            (string? Mac, string? Ip, string? Name) Fill(string deviceMac, int port, string? clientMac, string? ip, string? name) =>
                FillClient(LanClient(deviceMac, port), recentNames, clientMac, ip, name);

            string? ResolveFriendly(string mac, string ifName)
            {
                if (friendlyByMacIf.TryGetValue((mac, ifName), out var direct))
                    return direct;
                // VLAN sub-interface: inherit the parent port's friendly name and tag
                // it with the VLAN id, e.g. eth0.100 -> "Fiber ISP (100)".
                var sub = SubInterface.Match(ifName);
                if (sub.Success && friendlyByMacIf.TryGetValue((mac, sub.Groups["base"].Value), out var parent))
                    return $"{parent} ({sub.Groups["vlan"].Value})";
                return null;
            }

            // One row per interface SERIES, not per port: a port written under two names shows
            // twice for the minutes both have samples (a relabel, or a walk that once failed).
            // Accepted - folding on IfName would be the wrong key, and the window is short.
            var devices = points
                .GroupBy(p => p.DeviceMac, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var mac = Norm(g.Key);
                    targetByMac.TryGetValue(mac, out var target);
                    var type = NormalizeType(target?.AutoLabel);
                    return new
                    {
                        mac = g.Key,
                        name = !string.IsNullOrWhiteSpace(target?.Name) ? target!.Name : g.Key,
                        type,
                        // Switches only: drop logical-interface noise before the friendly-name
                        // resolution below. Physical and renamed ports pass through untouched.
                        ports = g
                            .Where(p => type != "switch" || !IsJunkSwitchInterface(p.IfName))
                            .Select(p =>
                            {
                                mapByKey.TryGetValue((mac, p.IfName), out var nm);
                                // VLAN sub-interfaces sit on a physical port, so they inherit
                                // the parent port's number and media when their own row has
                                // none (eth0.100 takes eth0's port number and SFP flag).
                                InterfaceNameMap? parent = null;
                                var sub = SubInterface.Match(p.IfName);
                                if (sub.Success)
                                    mapByKey.TryGetValue((mac, sub.Groups["base"].Value), out parent);
                                var portNumber = nm?.PortNumber ?? parent?.PortNumber;
                                // Single wired client on this physical port (links to the
                                // Client Dashboard). Keyed on the port's OWN number so a
                                // VLAN sub-interface doesn't borrow the parent's client.
                                string? clientMac = null, clientIp = null, clientName = null;
                                // A port several clients share names none of them: the table shows how many.
                                int? clientCount = null;
                                // A switch-to-switch (fabric) port carries no client, whatever was once recorded on it.
                                if (nm?.PortNumber is int pnum && !IsFabricPort(lanByPort, mac, pnum))
                                {
                                    if (histClients != null)
                                    {
                                        if (histClients.TryGetValue((mac, pnum), out var hc))
                                            (clientMac, clientIp, clientName) = Fill(mac, pnum, hc.ClientMac, hc.ClientIp, hc.ClientName);
                                        else if (histShared.TryGetValue((mac, pnum), out var hn))
                                            clientCount = hn;
                                    }
                                    else if (liveStats.GetPortClient(mac, pnum) is { } lc)
                                    {
                                        (clientMac, clientIp, clientName) = Fill(mac, pnum, lc.Mac, lc.Ip, lc.Name);
                                    }
                                    else if (liveStats.GetSharedPortCount(mac, pnum) is int ln)
                                    {
                                        clientCount = ln;
                                    }
                                    else if (presentByPort.TryGetValue((mac, pnum), out var present))
                                    {
                                        (clientMac, clientIp, clientName) = Fill(mac, pnum, present.ClientMac, present.Ip, present.Name);
                                    }
                                    else if (LanClient(mac, pnum) is { } lan)
                                    {
                                        // Asleep or unplugged: the port's last client, as LAN Health's link names it.
                                        (clientMac, clientIp, clientName) = Fill(mac, pnum, lan.ClientMac, null, null);
                                    }
                                }
                                return new
                                {
                                    ifName = p.IfName,
                                    portId = p.PortId,
                                    portNumber,
                                    connectedMac = clientMac,
                                    connectedIp = clientIp,
                                    connectedName = clientName,
                                    connectedCount = clientCount,
                                    // The port's own number, as the client: a VLAN sub-interface draws no power.
                                    // The port's own number: a VLAN sub-interface is not a link.
                                    lanHealthHref = nm?.PortNumber is int lp && lanByPort.TryGetValue((mac, lp), out var lh)
                                        ? NetworkOptimizer.Web.Services.Monitoring.LanHealth.LanHealthLinks.Link(lh.Link.Id) : null,
                                    poeWatts = nm?.PortNumber is int pp && poeByPort.TryGetValue((mac, pp), out var watts) ? watts : (double?)null,
                                    // Agent-resolved WAN/carrier label wins (e.g. gre1 ->
                                    // "WAN3 - AT&T Wireless"); otherwise the port_table /
                                    // sub-interface friendly name.
                                    friendlyName = liveStats.GetInterfaceLabel(mac, p.IfName)
                                        ?? ResolveFriendly(mac, p.IfName),
                                    isSfp = nm?.IsSfp ?? parent?.IsSfp,
                                    linkSpeedMbps = LinkSpeedAt(p, type, historic: at.HasValue) ?? nm?.SpeedMbps,
                                    operStatus = p.OperStatus,
                                    rateInBps = p.RateInBps,
                                    rateOutBps = p.RateOutBps,
                                    bytesIn = p.BytesIn,
                                    bytesOut = p.BytesOut,
                                    ucastPktsIn = p.UcastPktsIn,
                                    ucastPktsOut = p.UcastPktsOut,
                                    mcastPktsIn = p.McastPktsIn,
                                    mcastPktsOut = p.McastPktsOut,
                                    bcastPktsIn = p.BcastPktsIn,
                                    bcastPktsOut = p.BcastPktsOut,
                                    errorsIn = p.ErrorsIn,
                                    errorsOut = p.ErrorsOut,
                                    discardsIn = p.DiscardsIn,
                                    discardsOut = p.DiscardsOut,
                                };
                            })
                            // Physical ports first, ordered by port number; virtual /
                            // sub-interfaces (no port number) fall to the bottom by name.
                            .OrderBy(p => p.portNumber ?? int.MaxValue)
                            .ThenBy(p => p.ifName, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                })
                .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new { at = at?.ToUniversalTime().ToString("o"), devices });
        });
    }

    /// <summary>
    /// Each PoE port's draw now, keyed (device MAC, port number), from the console's port table: the number
    /// UniFi Network shows, for every switch and gateway whether SNMP reads it or not. Empty when the console
    /// is not answering. A port with PoE turned off (<c>poe_mode</c> "off") is left out, so it reads as no PoE.
    /// </summary>
    private static async Task<Dictionary<(string Mac, int Port), double>> LivePoeAsync(UniFiConnectionService connection, CancellationToken ct)
    {
        var poe = new Dictionary<(string, int), double>();
        if (connection.Client is not { } client) return poe;
        try
        {
            foreach (var device in await client.GetDevicesAsync(ct))
                foreach (var port in device.PortTable ?? [])
                    if (port.PortPoe && port.PortIdx > 0
                        && !string.Equals(port.PoeMode, "off", StringComparison.OrdinalIgnoreCase)
                        && double.TryParse(port.PoePower, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w))
                        poe[(Norm(device.Mac ?? ""), port.PortIdx)] = w;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The column is a detail: the table renders without it.
        }
        return poe;
    }

    /// <summary>
    /// Each PoE port's recorded draw nearest the scrub instant, keyed as <see cref="LivePoeAsync"/>. A switch's console
    /// series is <c>pt:N</c>; a gateway's is its interface name, numbered through the name map.
    /// </summary>
    private static async Task<Dictionary<(string Mac, int Port), double>> HistoricPoeAsync(
        MonitoringInfluxClient influx, DateTime at, Dictionary<(string mac, string ifName), InterfaceNameMap> mapByKey, CancellationToken ct)
    {
        var poe = new Dictionary<(string, int), double>();
        foreach (var p in await influx.QueryPortPoeAtAsync(at, ct))
        {
            var mac = Norm(p.DeviceMac);
            int? port = p.PortId.StartsWith("pt:", StringComparison.Ordinal) && int.TryParse(p.PortId.AsSpan(3), out var n) ? n
                : mapByKey.TryGetValue((mac, p.PortId), out var nm) ? nm.PortNumber : null;
            if (port is int idx) poe[(mac, idx)] = p.Watts;
        }
        return poe;
    }

    /// <summary>
    /// The port's client as LAN Health resolves it (named through ClientNames, then client history; its ip online
    /// or its last one), so Port Statistics and LAN Health agree. Null for a fabric link, a port with no client, or
    /// a shared port (a hypervisor's VMs), which names no one client.
    /// </summary>
    internal static NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkRecord? LanHealthPortClient(
        IReadOnlyDictionary<(string Mac, int Port), NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkHealth> lanByPort,
        string deviceMac, int port) =>
        lanByPort.TryGetValue((deviceMac, port), out var lh)
            && lh.Link.Kind == NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkKind.Access && lh.Link.Upstream.DeviceMac == deviceMac
            && !string.IsNullOrEmpty(lh.Link.ClientMac) && lh.Link.OccupantMacs.Count <= 1 ? lh.Link : null;

    /// <summary>True when LAN Health has a fabric link (device to device: uplink, mesh) on the port.</summary>
    internal static bool IsFabricPort(
        IReadOnlyDictionary<(string Mac, int Port), NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkHealth> lanByPort,
        string deviceMac, int port) =>
        lanByPort.TryGetValue((deviceMac, port), out var lh) && lh.Link.IsFabric;

    /// <summary>
    /// A client's name and ip for the Client column: its own, then LAN Health's for the same client on the port,
    /// then the client history name. LAN Health's answer applies only when it names this client, never a past one.
    /// </summary>
    internal static (string? Mac, string? Ip, string? Name) FillClient(
        NetworkOptimizer.Web.Services.Monitoring.LanHealth.LinkRecord? lan,
        IReadOnlyDictionary<string, string>? historyNames,
        string? clientMac, string? ip, string? name)
    {
        var key = Norm(clientMac ?? "");
        if (lan?.ClientMac is { } lanMac && Norm(lanMac) == key)
        {
            // LAN Health names an unnamed client by its MAC: that is no name at all here.
            if (string.IsNullOrWhiteSpace(name) && lan.Downstream.DeviceName != key) name = lan.Downstream.DeviceName;
            if (string.IsNullOrWhiteSpace(ip)) ip = lan.ClientIp;
        }
        if (string.IsNullOrWhiteSpace(name) && historyNames?.GetValueOrDefault(key) is { } known) name = known;
        return (clientMac, string.IsNullOrWhiteSpace(ip) ? null : ip, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    private static string Norm(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    // True when a switch interface is logical noise that should not appear in the port stats
    // table (see JunkSwitchInterface). Callers gate this on device type == "switch".
    private static bool IsJunkSwitchInterface(string? ifName) =>
        !string.IsNullOrEmpty(ifName) && JunkSwitchInterface.IsMatch(ifName);

    /// <summary>
    /// A historic scrub's link speed at the instant, or null to fall back to the name map (today's
    /// speed). Gateways use only the console-recorded speed: their built-in switch ports report the
    /// 10G switch conduit over SNMP. Switches and APs report correctly, so their SNMP sample counts
    /// while the port is up.
    /// </summary>
    internal static int? LinkSpeedAt(MonitoringInfluxClient.PortStatsPoint p, string deviceType, bool historic)
    {
        if (!historic) return null;
        if (p.LinkSpeedMbps is > 0) return p.LinkSpeedMbps;
        if (deviceType is "switch" or "ap" && p.OperStatus == 1 && p.SpeedBps is > 0)
            return (int)(p.SpeedBps.Value / 1_000_000L);
        return null;
    }

    private static string NormalizeType(string? autoLabel) => (autoLabel ?? "").ToLowerInvariant() switch
    {
        "ap" => "ap",
        "switch" => "switch",
        "gateway" => "gateway",
        _ => "unknown"
    };
}
