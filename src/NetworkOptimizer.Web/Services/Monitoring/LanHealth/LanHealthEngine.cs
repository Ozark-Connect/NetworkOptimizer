using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Services.ApAgent;

namespace NetworkOptimizer.Web.Services.Monitoring.LanHealth;

/// <summary>
/// One site's LAN Health: rolls each finished hour up into link rows every 15 minutes (backfilling
/// two weeks a day at a time), and builds the report and link pages from those rows plus the
/// current hour computed live while someone is watching. Influx is read here only, in one-hour
/// windows.
/// </summary>
public sealed class LanHealthEngine
{
    /// <summary>Bump when the roll-up changes; older rows are recomputed on the next backfill pass.</summary>
    public const int Version = 15;

    internal static readonly TimeSpan RunEvery = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan Backfill = TimeSpan.FromDays(14);
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan LiveCache = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TopologyCache = TimeSpan.FromMinutes(2);
    private const int OfflineNameLookbackHours = 2160;
    private static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(10);
    private const int HoursPerTick = 24;

    private readonly string _slug;
    private readonly Func<CancellationToken, Task<NetworkOptimizerDbContext>> _db;
    private readonly Func<MonitoringInfluxClient> _influx;
    private readonly Func<UniFiConnectionService> _connection;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _next = DateTime.MinValue;
    private readonly DateTime _created = DateTime.UtcNow;

    private Topology? _topology;
    private DateTime _topologyAt = DateTime.MinValue;
    private readonly Dictionary<DateTime, (DateTime At, List<LanHealthLinkHour> Rows)> _liveHours = [];

    /// <summary>Wired clients changing switch port between topology reads, for MAC Flapping.</summary>
    internal MacMoveTracker MacMoves { get; } = new();
    private readonly Dictionary<string, (DateTime At, LanHealthReport Report)> _reports = new();

    /// <summary>Raised after a roll-up or a live refresh, so open pages and the alert evaluator can act.</summary>
    public event Action<LanHealthReport>? Refreshed;


    public LanHealthEngine(string slug, Func<CancellationToken, Task<NetworkOptimizerDbContext>> db, Func<MonitoringInfluxClient> influx,
        Func<UniFiConnectionService> connection, ILogger logger)
    {
        _slug = slug;
        _db = db;
        _influx = influx;
        _connection = connection;
        _logger = logger;
    }

    public string Slug => _slug;

    /// <summary>The notification clock and format, for text with no viewer (alerts).</summary>
    public NetworkOptimizer.Alerts.Interfaces.INotificationTimeZoneResolver? NotificationClock { get; set; }

    /// <summary>Firmware Rollout's console locator, so a step on the console's side of the path explains inform failures.</summary>
    public Firmware.IRolloutObserverLocator? ConsoleLocator { get; set; }

    /// <summary>The site's devices, clients and links as of the last console read.</summary>
    public sealed class Topology
    {
        public List<UniFiDeviceResponse> Devices { get; init; } = [];
        public List<UniFiClientResponse> Clients { get; init; } = [];
        public List<LinkRecord> Links { get; init; } = [];
        public Dictionary<(string Mac, string IfName), int> NameMap { get; init; } = [];
        public Dictionary<string, string> TargetDevices { get; init; } = [];
        public List<LanHealthExclusion> Exclusions { get; init; } = [];
        public bool ScoreFabricOnly { get; init; }
        public List<UniFiNetworkConfig> Networks { get; init; } = [];
        public List<UniFiPortProfile> PortProfiles { get; init; } = [];
        /// <summary>The console settings (Global Switch Settings), as Config Optimizer reads them.</summary>
        public System.Text.Json.JsonDocument? Settings { get; init; }
        public Dictionary<string, LinkRecord> DownstreamToLink { get; init; } = [];
        public Dictionary<string, HashSet<string>> SubtreeAps { get; init; } = [];
    }

    // ---------- Background roll-up ----------

    /// <summary>Runs from the site engine's tick; returns at once unless a run is due.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _next) return;
        var connection = _connection();
        if (connection.HasNoConsole) return;
        if (!connection.IsConnected && now - _created < TimeSpan.FromMinutes(15)) return;
        _next = now + RunEvery;
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var topology = await GetTopologyAsync(ct, force: true);
            if (topology == null || topology.Links.Count == 0) return;
            await PollPortInsightsAsync(ct);
            var latest = FloorHour(now - Settle).AddHours(-1);
            var oldest = FloorHour(now - Backfill);
            await using var db = await _db(ct);
            var have = (await db.LanHealthSiteHours.AsNoTracking()
                    .Where(h => h.HourStart >= oldest && h.HourStart <= latest && h.Version == Version)
                    .Select(h => h.HourStart).ToListAsync(ct))
                .Select(h => DateTime.SpecifyKind(h, DateTimeKind.Utc)).ToHashSet();
            var missing = new List<DateTime>();
            for (var h = latest; h >= oldest && missing.Count < HoursPerTick; h = h.AddHours(-1))
                if (!have.Contains(h)) missing.Add(h);
            var sw = Stopwatch.StartNew();
            // Oldest first, so each hour's trailing score can read the hours before it.
            foreach (var hour in missing.OrderBy(h => h))
            {
                ct.ThrowIfCancellationRequested();
                var rows = await RollupHourAsync(hour, topology, ct);
                await SaveHourAsync(hour, rows, topology, ct);
            }
            if (sw.Elapsed > SlowRun)
                _logger.LogInformation("LAN Health for {Site} rolled up {Hours} hour(s) in {Seconds:0.0} s", _slug, missing.Count, sw.Elapsed.TotalSeconds);
            await PruneAsync(ct);
            await using (var cutDb = await _db(ct))
                CutoverAt = await cutDb.LanHealthSiteHours.AsNoTracking().OrderBy(h => h.HourStart).Select(h => (DateTime?)h.HourStart).FirstOrDefaultAsync(ct) is DateTime c
                    ? DateTime.SpecifyKind(c, DateTimeKind.Utc) : null;
            lock (_reports) _reports.Clear();
            // Alert text has no viewer: it reads on the notification clock and format, as every alert does.
            var zone = NotificationClock != null ? await NotificationClock.ResolveAsync(_slug, ct) : TimeZoneInfo.Utc;
            var format = NotificationClock != null ? await NotificationClock.ResolveFormatAsync(_slug, ct) : DisplayFormat.Fallback;
            var report = await GetReportAsync(48, zone, format, ct);
            Refreshed?.Invoke(report);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "LAN Health run failed for site {Site}", _slug);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> ConfigChangedSinceTopologyAsync(CancellationToken ct)
    {
        if (_topology == null) return false;
        try
        {
            await using var db = await _db(ct);
            var latest = await db.ConfigChanges.AsNoTracking().OrderByDescending(c => c.OccurredAt).Select(c => (DateTime?)c.OccurredAt).FirstOrDefaultAsync(ct);
            return latest is DateTime l && DateTime.SpecifyKind(l, DateTimeKind.Utc) > _topologyAt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: change check failed for {Site}", _slug);
            return false;
        }
    }

    /// <summary>Reads the console and the site database for the current topology, cached briefly.</summary>
    public async Task<Topology?> GetTopologyAsync(CancellationToken ct, bool force = false)
    {
        if (!force && _topology != null && DateTime.UtcNow - _topologyAt < TopologyCache) return _topology;
        var connection = _connection();
        if (connection.Client is not { } client) return _topology;
        List<UniFiDeviceResponse> devices;
        List<UniFiClientResponse> clients;
        try
        {
            devices = await client.GetDevicesAsync(ct) ?? [];
            clients = await client.GetClientsAsync(ct) ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: console read failed for {Site}", _slug);
            return _topology;
        }
        // A disconnected console answers with an empty device list. Never cache that: keep the last real
        // topology (or none) and read again on the next call, so data returns as soon as the console does.
        if (devices.Count == 0) return _topology;
        List<UniFiNetworkConfig> networks = [];
        List<UniFiPortProfile> profiles = [];
        System.Text.Json.JsonDocument? settings = null;
        try
        {
            networks = await client.GetNetworkConfigsAsync(ct) ?? [];
            profiles = await client.GetPortProfilesAsync(ct) ?? [];
            settings = await client.GetSettingsRawAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: network config read failed for {Site}", _slug);
        }
        var roles = new Dictionary<string, DeviceType>();
        foreach (var d in connection.CachedDevices ?? [])
            if (!string.IsNullOrEmpty(d.Mac)) roles[LanLinkBuilder.Mac(d.Mac)] = d.Type;

        // Names through the app's one naming path (ClientNames), with UniFi's display names and, for a
        // client that is offline now (a sleeping PC), its name from the console's client history.
        IReadOnlyDictionary<string, string> displayNames = new Dictionary<string, string>();
        List<UniFiClientDetailResponse> history = [];
        try
        {
            displayNames = await NetworkOptimizer.UniFi.ClientDisplayNameCache.GetAsync(client);
            // Offline clients are named from the console's client history through ClientNames, as Live
            // Traffic names them; 90 days, the LAN map's lookback.
            history = await client.GetClientHistoryAsync(OfflineNameLookbackHours, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: client names read failed for {Site}", _slug);
        }
        var offlineNames = history.Where(h => !string.IsNullOrEmpty(h.Mac))
            .GroupBy(h => LanLinkBuilder.Mac(h.Mac))
            .Select(g => (g.Key, Name: NetworkOptimizer.UniFi.ClientNames.For(g.First())))
            .Where(x => x.Name != null).ToDictionary(x => x.Key, x => x.Name!);
        var portClients = clients.Select(c => new LanLinkBuilder.PortClient(LanLinkBuilder.Mac(c.Mac),
            NetworkOptimizer.UniFi.ClientNames.Choose(displayNames.GetValueOrDefault(c.Mac.ToLowerInvariant()), c.Name, c.UnifiDeviceInfoFromUcore?.Name,
                c.Hostname, c.IsUniFiDevice) ?? offlineNames.GetValueOrDefault(LanLinkBuilder.Mac(c.Mac)) ?? LanLinkBuilder.Mac(c.Mac),
            c.SwMac, c.SwPort, c.ApMac, c.IsWired)).ToList();
        var links = LanLinkBuilder.Build(devices, portClients, roles, offlineNames);

        // A server or NAS (a hypervisor's VMs) puts many MACs on one port by design: the audit's own
        // device classification marks those ports, so the unmanaged-switch check leaves them alone.
        var detection = new NetworkOptimizer.Audit.Services.DeviceTypeDetectionService();
        var historyByMac = history.Where(h => !string.IsNullOrEmpty(h.Mac)).GroupBy(h => LanLinkBuilder.Mac(h.Mac)).ToDictionary(g => g.Key, g => g.First());
        bool IsServer(NetworkOptimizer.Core.Enums.ClientDeviceCategory c) => c is NetworkOptimizer.Core.Enums.ClientDeviceCategory.Server or NetworkOptimizer.Core.Enums.ClientDeviceCategory.NAS;
        foreach (var l in links.Where(l => l.Kind == LinkKind.Access))
        {
            var onPort = clients.Where(c => c.IsWired && c.SwPort == l.Upstream.PortIdx && LanLinkBuilder.Mac(c.SwMac) == l.Upstream.DeviceMac);
            l.ServerOnPort = onPort.Any(c => IsServer(detection.DetectDeviceType(c).Category))
                || (l.ClientMac is { } cm && historyByMac.TryGetValue(cm, out var h) && IsServer(detection.DetectDeviceType(h).Category));
        }

        await using var db = await _db(ct);
        var nameMap = (await db.InterfaceNameMaps.AsNoTracking().Where(m => m.PortNumber != null)
                .Select(m => new { m.DeviceMac, m.IfName, m.PortNumber }).ToListAsync(ct))
            .GroupBy(m => (LanLinkBuilder.Mac(m.DeviceMac), m.IfName))
            .ToDictionary(g => g.Key, g => g.First().PortNumber!.Value);
        var targets = (await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.DeviceMac != null)
                .Select(t => new { t.TargetId, t.DeviceMac }).ToListAsync(ct))
            .GroupBy(t => t.TargetId).ToDictionary(g => g.Key, g => LanLinkBuilder.Mac(g.First().DeviceMac));
        var exclusions = await db.LanHealthExclusions.AsNoTracking().ToListAsync(ct);

        // Probed APs below each device: probes to switches never score, so a switch's link is judged by the APs under it.
        var byMac = devices.Where(d => !string.IsNullOrEmpty(d.Mac)).GroupBy(d => LanLinkBuilder.Mac(d.Mac)).ToDictionary(g => g.Key, g => g.First());
        var downstreamToLink = links.Where(l => l.IsFabric).GroupBy(l => l.Downstream.DeviceMac).ToDictionary(g => g.Key, g => g.First());
        var children = links.Where(l => l.IsFabric).GroupBy(l => l.Upstream.DeviceMac).ToDictionary(g => g.Key, g => g.Select(l => l.Downstream.DeviceMac).ToList());
        var probedMacs = targets.Values.ToHashSet();
        var subtreeAps = new Dictionary<string, HashSet<string>>();
        foreach (var mac in downstreamToLink.Keys)
        {
            var set = new HashSet<string>();
            var stack = new Stack<string>([mac]);
            var seen = new HashSet<string>();
            while (stack.Count > 0)
            {
                var m = stack.Pop();
                if (!seen.Add(m)) continue;
                var role = roles.TryGetValue(m, out var r) ? r : byMac.TryGetValue(m, out var d) ? d.DeviceType : DeviceType.Unknown;
                if (role == DeviceType.AccessPoint && probedMacs.Contains(m)) set.Add(m);
                foreach (var c in children.GetValueOrDefault(m) ?? []) stack.Push(c);
            }
            subtreeAps[mac] = set;
        }

        _topology = new Topology
        {
            Devices = devices,
            Clients = clients,
            Links = links,
            NameMap = nameMap,
            TargetDevices = targets,
            Exclusions = exclusions,
            ScoreFabricOnly = exclusions.Any(e => e.Scope == "access-all" && e.Level == (int)ExclusionLevel.ExcludeFromScore),
            Networks = networks,
            PortProfiles = profiles,
            Settings = settings,
            DownstreamToLink = downstreamToLink,
            SubtreeAps = subtreeAps,
        };
        _topologyAt = DateTime.UtcNow;
        // Wired clients by the port they sit on, named as the links name it ("Switch A Port 3").
        var portName = devices.Where(d => !string.IsNullOrEmpty(d.Mac)).GroupBy(d => LanLinkBuilder.Mac(d.Mac)).ToDictionary(g => g.Key, g => g.First().Name ?? g.Key);
        MacMoves.Observe(_topologyAt, clients.Where(c => c.IsWired && c.SwPort is > 0 && !string.IsNullOrEmpty(c.SwMac))
            .Select(c => (c.Mac, NetworkOptimizer.UniFi.ClientNames.For(c) ?? c.Mac, $"{portName.GetValueOrDefault(LanLinkBuilder.Mac(c.SwMac)) ?? c.SwMac} Port {c.SwPort}")));
        return _topology;
    }

    /// <summary>Reads one hour from Influx and builds every link's row.</summary>
    internal async Task<List<LanHealthLinkHour>> RollupHourAsync(DateTime hour, Topology topology, CancellationToken ct)
    {
        var influx = _influx();
        var to = hour.AddHours(1);
        var portsTask = influx.QueryLanPortStatsAsync(hour, to, TimeSpan.FromHours(1), LanHealthEvidenceBuilder.PortFields, ct);
        var eventsTask = influx.QueryLanEventsAsync(hour.AddMinutes(-15), to, LanHealthEvidenceBuilder.EventTypes, ct);
        var lossTask = influx.QueryFabricLossSamplesAsync(hour, to, ct);
        var probesTask = influx.QueryFabricProbeSummaryAsync(hour, to, ct);
        var devicesTask = influx.QueryLanDeviceFieldsAsync(hour, to, TimeSpan.FromHours(1), LanHealthEvidenceBuilder.DeviceFields, ct);
        var rebootsTask = influx.QueryDeviceRebootsInRangeAsync(hour.AddMinutes(-10), to, ct);
        await Task.WhenAll(portsTask, eventsTask, lossTask, probesTask, devicesTask, rebootsTask);

        var explain = await ExplainWindowsAsync(hour, to, topology, eventsTask.Result, rebootsTask.Result, ct);
        // One boot can write several reboot points; one per device per ten minutes.
        var reboots = rebootsTask.Result
            .GroupBy(r => LanLinkBuilder.Mac(r.DeviceMac))
            .ToDictionary(g => g.Key, g => g.Select(r => r.BootedAt).OrderBy(t => t)
                .Aggregate(new List<DateTime>(), (acc, t) => { if (acc.Count == 0 || (t - acc[^1]).TotalMinutes > 10) acc.Add(t); return acc; })
                .Where(t => t >= hour && t < to).ToList());
        var inputs = new LanHealthEvidenceBuilder.HourInputs
        {
            Hour = hour,
            Ports = portsTask.Result,
            Events = eventsTask.Result.Where(e => e.Time >= hour && e.Time < to).ToList(),
            Loss = lossTask.Result,
            Probes = probesTask.Result,
            Devices = devicesTask.Result,
            TargetDevices = topology.TargetDevices,
            NameMap = topology.NameMap,
            Explain = explain,
            Reboots = reboots,
            PortInsightsAvailable = PortInsightsAvailable,
        };
        return topology.Links
            .Select(l => LanHealthEvidenceBuilder.BuildHour(l, inputs, topology.DownstreamToLink, topology.SubtreeAps, Version).Row)
            .ToList();
    }

    /// <summary>
    /// What explains a relink in the hour: a reboot, a device state window padded by a poll on each
    /// side, a Change Control change to the device or its port profile, and a Firmware Rollout wave
    /// on the device or anything above it.
    /// </summary>
    private async Task<List<ExplainWindow>> ExplainWindowsAsync(DateTime from, DateTime to, Topology topology,
        IReadOnlyList<MonitoringInfluxClient.LanEventPoint> events, IReadOnlyList<MonitoringInfluxClient.DeviceRebootPoint> reboots, CancellationToken ct)
    {
        var windows = new List<ExplainWindow>();
        foreach (var r in reboots)
            // The detail carries the reboot's cause (RebootCategory), so PoE Power Cycling counts only power losses.
            windows.Add(new ExplainWindow(r.BootedAt.AddMinutes(-3), r.BootedAt.AddMinutes(3), LanLinkBuilder.Mac(r.DeviceMac), ExplainReason.DeviceRebooted,
                string.IsNullOrEmpty(r.Category) ? null : r.Category));

        windows.AddRange(StateWindows(events, to));
        // The AP's own provision re-up explains the relink beside it.
        foreach (var e in events.Where(e => e.EventType == ApAgentLinkEventSink.EventTypes.UplinkReconfigure))
            windows.Add(new ExplainWindow(e.Time.AddSeconds(-30), e.Time.AddSeconds(30), LanLinkBuilder.Mac(e.DeviceMac), ExplainReason.ManualLinkReset, "provision"));

        try
        {
            await using var chDb = await _db(ct);
            var moves = await chDb.ApChannelChanges.AsNoTracking()
                .Where(c => c.ChangedAtUtc >= from.AddMinutes(-5) && c.ChangedAtUtc < to.AddMinutes(5)).ToListAsync(ct);
            windows.AddRange(BackhaulChannelWindows(moves, topology.Links));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: channel changes unavailable for {Site}", _slug);
        }

        try
        {
            await using var db = await _db(ct);
            var changes = await db.ConfigChanges.AsNoTracking().Where(c => c.OccurredAt >= from.AddMinutes(-5) && c.OccurredAt < to.AddMinutes(30)).ToListAsync(ct);
            if (changes.Count > 0)
            {
                // A Network app upgrade restarts the app: every AP's inform fails for a few minutes. Change
                // Control records it when it notices, so the window reaches back before that.
                foreach (var up in changes.Where(c => c.Kind == ConfigChangeKind.NetworkAppUpgrade))
                    windows.Add(new ExplainWindow(up.OccurredAt.AddMinutes(-30), up.OccurredAt.AddMinutes(10), "", ExplainReason.NetworkAppUpgrade));
                var ids = changes.Select(c => c.Id).ToList();
                var objects = await db.ConfigChangeObjects.AsNoTracking().Where(o => ids.Contains(o.ChangeId)).ToListAsync(ct);
                var deviceById = topology.Devices.Where(d => !string.IsNullOrEmpty(d.Id)).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => LanLinkBuilder.Mac(g.First().Mac));
                foreach (var o in objects)
                {
                    var change = changes.First(c => c.Id == o.ChangeId);
                    var detail = ChangeDetail.Format(change.ActorName, change.Summary);
                    if (o.Collection == "device" && deviceById.TryGetValue(o.ObjectId, out var mac))
                        windows.Add(new ExplainWindow(change.OccurredAt, change.OccurredAt, mac, ExplainReason.PortProfileChanged, detail));
                    else if (o.Collection == "portconf")
                        foreach (var l in topology.Links.Where(l => l.UpstreamPort.PortProfileId == o.ObjectId))
                            windows.Add(new ExplainWindow(change.OccurredAt, change.OccurredAt, l.Upstream.DeviceMac, ExplainReason.PortProfileChanged, detail));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: change lookup failed for {Site}", _slug);
        }

        // Firmware Rollout: the upgraded device and every device below it drop while it reboots.
        try
        {
            await using var db = await _db(ct);
            var steps = await db.FirmwareRolloutSteps.AsNoTracking()
                .Where(s => s.WentDownAt != null && s.WentDownAt < to && (s.BackAt == null || s.BackAt >= from.AddMinutes(-10)))
                .ToListAsync(ct);
            // Firmware Rollout's own dark set: a step on the path between a device and the console (a
            // self-hosted console behind its own switch) cuts the device off without dropping its links.
            List<Firmware.RolloutDeviceObservation>? observed = null;
            Firmware.RolloutObserverPositions? positions = null;
            if (steps.Count > 0 && ConsoleLocator != null)
            {
                observed = topology.Devices.Where(d => !string.IsNullOrEmpty(d.Mac)).Select(d => new Firmware.RolloutDeviceObservation
                {
                    Mac = LanLinkBuilder.Mac(d.Mac),
                    UplinkMac = d.Uplink?.UplinkMac is { Length: > 0 } u ? LanLinkBuilder.Mac(u) : null,
                    LastUplinkMac = d.LastUplink?.UplinkMac is { Length: > 0 } lu ? LanLinkBuilder.Mac(lu) : null,
                    WirelessUplink = string.Equals(d.Uplink?.Type, "wireless", StringComparison.OrdinalIgnoreCase),
                    IsGateway = d.DeviceType == DeviceType.Gateway,
                }).ToList();
                try { positions = await ConsoleLocator.LocateAsync(observed, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "LAN Health: console locate failed for {Site}", _slug); }
            }
            foreach (var s in steps)
            {
                var start = s.WentDownAt!.Value.AddMinutes(-2);
                var end = (s.BackAt ?? to).AddMinutes(5);
                var stepMac = LanLinkBuilder.Mac(s.DeviceMac);
                var below = Below(stepMac, topology).ToHashSet();
                foreach (var mac in below)
                    windows.Add(new ExplainWindow(start, end, mac, ExplainReason.FirmwareRolloutWave, s.ToVersion));
                if (observed != null && positions != null)
                {
                    var isAp = topology.Devices.Any(d => LanLinkBuilder.Mac(d.Mac) == stepMac && d.DeviceType == DeviceType.AccessPoint);
                    foreach (var mac in Firmware.RolloutDarkSet.DevicesDarkenedBy(stepMac, !isAp, observed, positions).Where(m => !below.Contains(m)))
                        windows.Add(new ExplainWindow(start, end, mac, ExplainReason.ConsoleCutOff, s.ToVersion));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: rollout lookup failed for {Site}", _slug);
        }
        return windows;

    }

    /// <summary>Device states that explain a relink: the console is changing the device. Offline and error states never do.</summary>
    internal static readonly HashSet<string> ExplainingStates = ["Pending", "Upgrading", "Provisioning", "Adopting", "Deleting"];

    /// <summary>
    /// A window per stretch a device spent in an <see cref="ExplainingStates"/> state, from the recorder's state
    /// events. Any other state (Online, Offline, an error) closes the stretch: a device knocked offline by a
    /// flapping uplink must not have those drops explained away.
    /// </summary>
    internal static IEnumerable<ExplainWindow> StateWindows(IEnumerable<MonitoringInfluxClient.LanEventPoint> events, DateTime to)
    {
        var pad = TimeSpan.FromMinutes(1);
        foreach (var g in events.Where(e => e.EventType == LanPortSnapshotRecorder.StateEventType).GroupBy(e => LanLinkBuilder.Mac(e.DeviceMac)))
        {
            DateTime? start = null;
            string? state = null;
            foreach (var e in g.OrderBy(e => e.Time))
            {
                if (e.NewValue is { } v && ExplainingStates.Contains(v)) { start ??= e.Time; state = v; continue; }
                if (start is DateTime s) yield return new ExplainWindow(s - pad, e.Time + pad, g.Key, Reason(state), state);
                start = null;
            }
            if (start is DateTime open) yield return new ExplainWindow(open - pad, to + pad, g.Key, Reason(state), state);
        }

        static ExplainReason Reason(string? state) => state == "Upgrading" ? ExplainReason.DeviceUpgrading : ExplainReason.DeviceProvisioning;
    }

    private readonly HashSet<(string, int, string)> _anomaliesSeen = [];

    /// <summary>The anomalies Port Anomalies scored at the last poll.</summary>
    internal IReadOnlyList<LanHealthPortInsights.Anomaly> PortAnomalies { get; private set; } = [];

    /// <summary>Whether Port Anomalies answered at the last poll (it then counts as an observer, even with nothing to say).</summary>
    public bool PortInsightsAvailable { get; private set; }

    /// <summary>Reads the port anomaly scores once a run and stores anomalies not yet stored.</summary>
    private async Task PollPortInsightsAsync(CancellationToken ct)
    {
        if (_connection().Client is not { } client) return;
        try
        {
            using var doc = await client.GetPortAnomaliesRawAsync(ct);
            PortInsightsAvailable = doc != null;
            if (doc == null) return;
            var influx = _influx();
            var now = DateTime.UtcNow;
            // Scores are current state: an anomaly is written when it appears, and again if it clears and returns.
            var current = LanHealthPortInsights.Parse(doc.RootElement).ToList();
            PortAnomalies = current;
            var keys = current.Select(a => (a.DeviceMac, a.PortIdx, a.Kind)).ToHashSet();
            foreach (var a in current.Where(a => !_anomaliesSeen.Contains((a.DeviceMac, a.PortIdx, a.Kind))))
                _ = influx.WriteEventAsync(a.DeviceMac, LanHealthPortInsights.EventType, "info", a.Kind,
                    a.PortIdx.ToString(System.Globalization.CultureInfo.InvariantCulture), null,
                    a.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture), now);
            _anomaliesSeen.Clear();
            _anomaliesSeen.UnionWith(keys);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: Port Anomalies read failed for {Site}", _slug);
        }
    }

    /// <summary>
    /// A parent AP's channel move explains its mesh children's drops around it: the child rescans and rejoins. The
    /// window covers the children only, so the parent's own wired link is not explained by it.
    /// </summary>
    internal static IEnumerable<ExplainWindow> BackhaulChannelWindows(IEnumerable<NetworkOptimizer.Storage.Models.ApChannelChange> moves, IReadOnlyList<LinkRecord> links)
    {
        foreach (var m in moves)
        {
            var parent = LanLinkBuilder.Mac(m.ApMac);
            var detail = $"{m.PreviousChannel?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}>{m.NewChannel.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            foreach (var l in links.Where(l => l.IsMesh && l.Upstream.DeviceMac == parent))
                yield return new ExplainWindow(m.ChangedAtUtc.AddMinutes(-1), m.ChangedAtUtc.AddMinutes(2), l.Downstream.DeviceMac, ExplainReason.BackhaulChannelChange, detail);
        }
    }

    /// <summary>A device and every UniFi device below it in the uplink tree.</summary>
    internal static IEnumerable<string> Below(string mac, Topology topology)
    {
        var seen = new HashSet<string>();
        var stack = new Stack<string>([mac]);
        while (stack.Count > 0)
        {
            var m = stack.Pop();
            if (!seen.Add(m)) continue;
            yield return m;
            foreach (var l in topology.Links.Where(l => l.IsFabric && l.Upstream.DeviceMac == m)) stack.Push(l.Downstream.DeviceMac);
        }
    }

    private async Task SaveHourAsync(DateTime hour, List<LanHealthLinkHour> rows, Topology topology, CancellationToken ct)
    {
        await using var db = await _db(ct);
        var old = await db.LanHealthLinkHours.Where(r => r.HourStart == hour).ToListAsync(ct);
        db.LanHealthLinkHours.RemoveRange(old);
        db.LanHealthLinkHours.AddRange(rows);
        await db.SaveChangesAsync(ct);

        // The trailing 48 hours' score at this hour, from the rows just stored and the ones before them.
        var window = await LoadRowsAsync(db, hour.AddHours(-47), hour.AddHours(1), ct);
        var report = LanHealthReportBuilder.Build(topology.Links, window, new Dictionary<string, List<LanHealthLinkHour>>(),
            topology.Exclusions, topology.ScoreFabricOnly, null, FindingText.Utc, hour.AddHours(-47), hour.AddHours(1));
        var (impairment, worst) = LanHealthReportBuilder.HourImpairment(rows, topology.Links, topology.Exclusions, topology.ScoreFabricOnly, report.Findings);
        var site = await db.LanHealthSiteHours.FirstOrDefaultAsync(h => h.HourStart == hour, ct);
        if (site == null) { site = new LanHealthSiteHour { HourStart = hour }; db.LanHealthSiteHours.Add(site); }
        site.Score = report.Score;
        site.HourImpairment = Math.Round(impairment, 4);
        site.Incidents = (int)rows.Sum(r => r.Drops + r.Relinks + r.LossBursts + (r.ErrorBursts > 0 ? 1 : 0));
        site.Explained = (int)rows.Sum(r => r.DropsExplained);
        site.FabricLinks = topology.Links.Count(l => l.IsFabric);
        site.AccessPorts = topology.Links.Count(l => !l.IsFabric);
        site.Worst = worst is { Length: > 200 } w ? w[..200] : worst;
        site.Version = Version;
        site.ComputedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - Retention;
        await using var db = await _db(ct);
        await db.LanHealthLinkHours.Where(r => r.HourStart < cutoff).ExecuteDeleteAsync(ct);
        await db.LanHealthSiteHours.Where(r => r.HourStart < cutoff).ExecuteDeleteAsync(ct);
    }

    private static async Task<Dictionary<string, List<LanHealthLinkHour>>> LoadRowsAsync(NetworkOptimizerDbContext db, DateTime from, DateTime to, CancellationToken ct) =>
        (await db.LanHealthLinkHours.AsNoTracking().Where(r => r.HourStart >= from && r.HourStart < to).ToListAsync(ct))
            .Select(r => { r.HourStart = DateTime.SpecifyKind(r.HourStart, DateTimeKind.Utc); return r; })
            .GroupBy(r => r.LinkId).ToDictionary(g => g.Key, g => g.OrderBy(r => r.HourStart).ToList());

    // ---------- Report ----------

    /// <summary>
    /// The report over the last <paramref name="hours"/>, with the current hour computed live (cached
    /// a minute across viewers). Findings render times on the viewer's clock.
    /// </summary>
    public async Task<LanHealthReport> GetReportAsync(int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct)
    {
        var key = $"{hours}|{zone.Id}|{format}";
        // A Change Control change since the topology was read drops both caches, so a fixed setting
        // clears its finding on the next reload instead of after the 2-minute console cache.
        var changed = await ConfigChangedSinceTopologyAsync(ct);
        if (changed) lock (_reports) _reports.Clear();
        lock (_reports)
            if (_reports.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.At < LiveCache) return cached.Report;

        var topology = await GetTopologyAsync(ct, force: changed);
        var now = DateTime.UtcNow;
        var from = FloorHour(now).AddHours(-hours + 1);
        var to = FloorHour(now).AddHours(1);
        if (topology == null) return new LanHealthReport { From = from, To = to };

        await using var db = await _db(ct);
        var rows = await LoadRowsAsync(db, from, to, ct);
        // The last closed hour is computed live too until the tick stores it, up to RunEvery + Settle after it ends.
        var previous = FloorHour(now).AddHours(-1);
        var previousStored = await db.LanHealthSiteHours.AsNoTracking().AnyAsync(h => h.HourStart == previous && h.Version == Version, ct);
        var live = await LiveHoursAsync(topology, LiveHoursFor(now, previousStored), ct);
        foreach (var r in live)
        {
            if (!rows.TryGetValue(r.LinkId, out var list)) rows[r.LinkId] = list = [];
            if (!list.Any(x => x.HourStart == r.HourStart)) list.Add(r);
        }
        var baseline = await LoadRowsAsync(db, from.AddDays(-LanHealthReportBuilder.BaselineDays), from, ct);
        var text = new FindingText(t => TimeFormatHelper.FormatLocalDateTime(t, zone, format), RangeLabel(hours));
        var tests = await SpeedTestsAsync(db, ct);
        var attributed = LanHealthSpeedTests.Attribute(tests, topology.Links, from, to);
        var forwarding = LanHealthSpeedTests.GatewayForwarding(tests, from, to);
        var informFailures = rows.Values.SelectMany(r => r).Where(r => r.InformFailures > 0)
            .GroupBy(r => topology.Links.FirstOrDefault(l => l.Id == r.LinkId)?.Downstream.DeviceMac ?? "")
            .Where(g => g.Key.Length > 0).ToDictionary(g => g.Key, g => g.Sum(r => r.InformFailures));
        var stpBaseline = baseline.Values.SelectMany(r => r).Where(r => r.StpChanges != null)
            .GroupBy(r => topology.Links.FirstOrDefault(l => l.Id == r.LinkId)?.Upstream.DeviceMac ?? "")
            .ToDictionary(g => g.Key, g => g.Sum(r => r.StpChanges ?? 0) / Math.Max(1, LanHealthReportBuilder.BaselineDays * 24));
        var report = LanHealthReportBuilder.Build(topology.Links, rows, baseline, topology.Exclusions, topology.ScoreFabricOnly,
            (links, evidence) => SiteEvidenceFor(topology, links, evidence, text, hours, forwarding, informFailures, stpBaseline, MacMoves.Since(from)), text, from, to, attributed,
            // Optics reads the modules now: the live report only, since a stored past hour has no reading of its own.
            await SfpAsync(topology, ct), await SpeedHistoryAsync(db, topology, ct));
        AddTestSuggestions(report, topology, tests);
        try
        {
            var since = DateTime.UtcNow - LanHealthReportBuilder.UpgradeSuspectWindow - TimeSpan.FromDays(2);
            var steps = await db.FirmwareRolloutSteps.AsNoTracking().Where(s => s.BackAt != null && s.BackAt >= since).ToListAsync(ct);
            if (steps.Count > 0)
                foreach (var l in report.Links) l.UpgradeSuspect = LanHealthReportBuilder.UpgradeSuspectFor(l, steps);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "LAN Health: rollout steps unavailable for {Site}", _slug);
        }
        foreach (var note in LanHealthPortInsights.Notes(report.Links, PortAnomalies, topology.Devices))
        {
            report.Links.First(l => l.Link.Id == note.LinkId).Findings.Add(note);
            report.Findings.Add(note);
        }
        foreach (var l in report.Links)
            l.ProbeTargetId = topology.TargetDevices.FirstOrDefault(kv => string.Equals(kv.Value, l.Link.Downstream.DeviceMac, StringComparison.OrdinalIgnoreCase)).Key;
        var history = await db.LanHealthSiteHours.AsNoTracking().Where(h => h.HourStart >= from && h.HourStart < to && h.Score != null)
            .OrderBy(h => h.HourStart).Select(h => new { h.HourStart, h.Score }).ToListAsync(ct);
        report.History.AddRange(history.Select(h => (DateTime.SpecifyKind(h.HourStart, DateTimeKind.Utc), h.Score!.Value)));
        if (report.Score is double s) report.History.Add((FloorHour(now), s));
        report.CutoverAt = await db.LanHealthSiteHours.AsNoTracking().OrderBy(h => h.HourStart).Select(h => (DateTime?)h.HourStart).FirstOrDefaultAsync(ct);
        report.NotScored.AddRange(NotScored(topology));
        report.Ready = history.Count > 0 || live.Count > 0;
        if (topology.Links.Count > 0) lock (_reports) _reports[key] = (DateTime.UtcNow, report);
        return report;
    }

    /// <summary>The hours a report computes live: the current one, and the last closed one until the tick stores it.</summary>
    internal static List<DateTime> LiveHoursFor(DateTime now, bool previousStored) =>
        previousStored ? [FloorHour(now)] : [FloorHour(now).AddHours(-1), FloorHour(now)];

    private (DateTime At, Dictionary<string, NetworkOptimizer.Diagnostics.Models.WanLinkSpeedHistory> ByLink)? _speedHistory;

    /// <summary>Each wired fabric link's 30-day speed history from its stored hours, refreshed hourly.</summary>
    private async Task<Dictionary<string, NetworkOptimizer.Diagnostics.Models.WanLinkSpeedHistory>> SpeedHistoryAsync(
        NetworkOptimizerDbContext db, Topology topology, CancellationToken ct)
    {
        if (_speedHistory is { } c && DateTime.UtcNow - c.At < TimeSpan.FromHours(1)) return c.ByLink;
        var since = DateTime.UtcNow - LanHealthReportBuilder.SpeedHistoryLookback;
        var rows = await db.LanHealthLinkHours.AsNoTracking()
            .Where(r => r.HourStart >= since && r.Kind == (int)LinkKind.FabricWired && r.SpeedMinMbps > 0)
            .Select(r => new { r.LinkId, r.HourStart, r.SpeedMinMbps }).ToListAsync(ct);
        var byLink = LanHealthReportBuilder.SpeedHistories(topology.Links,
            rows.Select(r => (r.LinkId, DateTime.SpecifyKind(r.HourStart, DateTimeKind.Utc), r.SpeedMinMbps!.Value)));
        _speedHistory = (DateTime.UtcNow, byLink);
        return byLink;
    }

    private (DateTime At, IReadOnlyDictionary<(string DeviceMac, string Port), double> Best)? _sfpBest;

    /// <summary>Each link's SFP reading now, with the module's best this week (that half refreshed hourly).</summary>
    private async Task<Dictionary<string, LanHealthReportBuilder.SfpReading>> SfpAsync(Topology topology, CancellationToken ct)
    {
        if (_sfpBest is not { } cached || DateTime.UtcNow - cached.At > TimeSpan.FromHours(1))
        {
            try { _sfpBest = (DateTime.UtcNow, await _influx().QuerySfpRxBestAsync(DateTime.UtcNow.AddDays(-7), ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "LAN Health: SFP history unavailable for {Site}", _slug);
                _sfpBest ??= (DateTime.UtcNow, new Dictionary<(string, string), double>());
            }
        }
        return LanHealthReportBuilder.SfpReadings(topology.Links, topology.Devices, _sfpBest!.Value.Best);
    }

    /// <summary>Rows for hours not yet stored, each computed live and cached for LiveCache.</summary>
    private async Task<List<LanHealthLinkHour>> LiveHoursAsync(Topology topology, IReadOnlyList<DateTime> hours, CancellationToken ct)
    {
        var all = new List<LanHealthLinkHour>();
        lock (_liveHours)
            foreach (var stale in _liveHours.Keys.Where(k => !hours.Contains(k)).ToList()) _liveHours.Remove(stale);
        foreach (var hour in hours)
        {
            (DateTime At, List<LanHealthLinkHour> Rows) cached;
            bool hit;
            lock (_liveHours) hit = _liveHours.TryGetValue(hour, out cached);
            if (hit && DateTime.UtcNow - cached.At < LiveCache) { all.AddRange(cached.Rows); continue; }
            try
            {
                var rows = await RollupHourAsync(hour, topology, ct);
                lock (_liveHours) _liveHours[hour] = (DateTime.UtcNow, rows);
                all.AddRange(rows);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "LAN Health: live hour failed for {Site}", _slug);
                if (hit) all.AddRange(cached.Rows);
            }
        }
        return all;
    }

    private List<LanHealthSpeedTests.Test>? _tests;
    private DateTime _testsAt = DateTime.MinValue;

    /// <summary>LAN speed tests over 90 days (the norms need history), cached a quarter hour.</summary>
    private async Task<List<LanHealthSpeedTests.Test>> SpeedTestsAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        if (_tests != null && DateTime.UtcNow - _testsAt < RunEvery) return _tests;
        var since = DateTime.UtcNow.AddDays(-90);
        var lan = new[] { SpeedTestDirection.ServerToDevice, SpeedTestDirection.ClientToServer, SpeedTestDirection.BrowserToServer };
        var rows = await db.Iperf3Results.AsNoTracking()
            .Where(r => r.Success && r.TestTime >= since && r.PathAnalysisJson != null && lan.Contains(r.Direction))
            .OrderByDescending(r => r.TestTime).Take(5000).ToListAsync(ct);
        _tests = LanHealthSpeedTests.FromResults(rows);
        _testsAt = DateTime.UtcNow;
        return _tests;
    }

    /// <summary>Untested-link and cross-VLAN suggestions: findings at 0 pts that never run a test.</summary>
    private static void AddTestSuggestions(LanHealthReport report, Topology t, IReadOnlyList<LanHealthSpeedTests.Test> tests)
    {
        var models = t.Devices.Where(d => !string.IsNullOrEmpty(d.Mac)).GroupBy(d => LanLinkBuilder.Mac(d.Mac)).ToDictionary(g => g.Key, g => g.First());
        foreach (var f in LanHealthSpeedTests.Suggestions(tests, t.Links, DateTime.UtcNow, mac => models.TryGetValue(mac, out var d) && d.CanRunIperf3))
        {
            var owner = report.Links.FirstOrDefault(l => l.Link.Id == f.LinkId);
            owner?.Findings.Add(f);
            report.Findings.Add(f);
        }
        var serverNetwork = t.Networks.FirstOrDefault(n => n.Purpose == "corporate" && n.Vlan is null or 1)?.Name;
        var wired = t.Links.Where(l => l.Kind == LinkKind.Access && l.ClientMac != null)
            .Select(l => (Mac: l.ClientMac!, Name: l.Downstream.DeviceName,
                Network: t.Clients.FirstOrDefault(c => LanLinkBuilder.Mac(c.Mac) == l.ClientMac)?.Network, l.SpeedMbps, SwitchMac: (string?)l.Upstream.DeviceMac))
            .ToList();
        if (LanHealthSpeedTests.CrossVlanCandidate(tests, t.Links, wired, serverNetwork, DateTime.UtcNow) is { } cross)
            report.Findings.Add(cross);
    }

    private static SiteEvidence SiteEvidenceFor(Topology t, IReadOnlyList<LinkHealth> links, IReadOnlyDictionary<string, LinkEvidence> evidence,
        FindingText text, int hours, (int Tests, double RatioToNorm)? forwarding,
        IReadOnlyDictionary<string, int> informFailures, IReadOnlyDictionary<string, double> stpBaseline,
        IReadOnlyList<(string Mac, string Name, int Moves, IReadOnlyList<string> Ports)>? macMoves = null) => new()
        {
            MacMoves = macMoves ?? [],
            GatewayForwarding = forwarding,
            InformFailures = informFailures,
            DeviceHealth = t.Devices.Where(d => !string.IsNullOrEmpty(d.Mac)).GroupBy(d => LanLinkBuilder.Mac(d.Mac))
            .ToDictionary(g => g.Key, g => (Percent(g.First().SystemStats?.Cpu), Percent(g.First().SystemStats?.Mem), g.First().GeneralTemperature)),
            Devices = t.Devices,
            Networks = t.Networks,
            PortProfiles = t.PortProfiles,
            Settings = t.Settings,
            Clients = t.Clients,
            Links = links,
            Evidence = evidence,
            StpChanges = links.Where(l => l.Link.IsFabric).GroupBy(l => l.Link.Upstream.DeviceMac)
            .ToDictionary(g => g.Key, g =>
            {
                var window = g.Sum(l => evidence.TryGetValue(l.Link.Id, out var e) ? e.Upstream.StpChanges ?? 0 : 0);
                return (window, stpBaseline.GetValueOrDefault(g.Key));
            }),
            DuplicateIps = t.Clients.Where(c => !string.IsNullOrEmpty(c.Ip)).GroupBy(c => c.Ip)
            .Where(g => g.Select(c => c.Mac.ToLowerInvariant()).Distinct().Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<string>)g.Select(c => c.Mac.ToLowerInvariant()).Distinct().ToList())).ToList(),
            Text = text,
            WindowHours = hours,
        };

    private static IEnumerable<string> NotScored(Topology t)
    {
        var classes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var d in t.Devices)
            foreach (var p in d.PortTable ?? [])
                if (NetworkOptimizer.UniFi.LanHealthPorts.IsWanPort(p, d.DeviceType == DeviceType.Gateway)) classes.Add("WAN ports");
        classes.Add("bridges");
        classes.Add("VPN tunnels");
        classes.Add("VLAN sub-interfaces");
        classes.Add("Wi-Fi radios");
        return classes;
    }

    private static double? Percent(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    internal static string RangeLabel(int hours) => hours <= 48 ? $"{hours}h" : $"{hours / 24}d";

    /// <summary>Drops cached reports and re-reads the topology, after an exclusion changes.</summary>
    public async Task InvalidateAsync(CancellationToken ct)
    {
        lock (_reports) _reports.Clear();
        await GetTopologyAsync(ct, force: true);
    }

    // ---------- Link page ----------

    /// <summary>One link's page: its evaluation and the incidents behind it.</summary>
    public async Task<LinkHealth?> GetLinkAsync(string linkId, int hours, TimeZoneInfo zone, DisplayFormat format, CancellationToken ct)
    {
        var report = await GetReportAsync(hours, zone, format, ct);
        return report.Links.FirstOrDefault(l => l.Link.Id == linkId);
    }

    /// <summary>The first hour LAN Health rolled up (the Site Health cutover), once known; read without waiting.</summary>
    public DateTime? CutoverAt { get; private set; }

    /// <summary>The stored site hours in [from, to): each hour's own impairment and worst link, for Site Health.</summary>
    /// <summary>Each stored hour's LAN impairment, its worst link's name, and that link's id (from the same hour's link rows).</summary>
    public async Task<IReadOnlyDictionary<DateTime, (double Impairment, string? Worst, string? WorstLinkId)>> GetSiteHoursAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await using var db = await _db(ct);
        var hours = await db.LanHealthSiteHours.AsNoTracking()
            .Where(h => h.HourStart >= from && h.HourStart < to && h.HourImpairment != null)
            .Select(h => new { h.HourStart, h.HourImpairment, h.Worst }).ToListAsync(ct);
        var names = hours.Where(h => h.Worst != null).Select(h => h.Worst!).Distinct().ToList();
        var ids = names.Count == 0 ? [] : await db.LanHealthLinkHours.AsNoTracking()
            .Where(r => r.HourStart >= from && r.HourStart < to && r.Name != null && names.Contains(r.Name))
            .Select(r => new { r.HourStart, r.Name, r.LinkId }).ToListAsync(ct);
        var idOf = ids.GroupBy(r => (r.HourStart, r.Name)).ToDictionary(g => g.Key, g => g.First().LinkId);
        var result = hours.ToDictionary(h => DateTime.SpecifyKind(h.HourStart, DateTimeKind.Utc),
            h => (h.HourImpairment!.Value, h.Worst, h.Worst != null ? idOf.GetValueOrDefault((h.HourStart, (string?)h.Worst)) : null));

        // Hours not stored yet (the one in progress, and the last closed one until the tick stores it) are
        // computed live, as the LAN Health page shows them: Site Health's provisional hour would otherwise
        // read no LAN impairment at all.
        var now = DateTime.UtcNow;
        var live = LiveHoursFor(now, result.ContainsKey(FloorHour(now).AddHours(-1))).Where(h => h >= from && h < to && !result.ContainsKey(h)).ToList();
        if (live.Count > 0 && await GetTopologyAsync(ct) is { Links.Count: > 0 } topology)
        {
            try
            {
                var report = await GetReportAsync(48, TimeZoneInfo.Utc, DisplayFormat.Fallback, ct);
                var rows = await LiveHoursAsync(topology, live, ct);
                foreach (var h in live)
                {
                    var (impairment, worst) = LanHealthReportBuilder.HourImpairment(rows.Where(r => r.HourStart == h).ToList(), topology.Links,
                        topology.Exclusions, topology.ScoreFabricOnly, report.Findings);
                    result[h] = (Math.Round(impairment, 4), worst, worst == null ? null : topology.Links.FirstOrDefault(l => l.Name == worst)?.Id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "LAN Health: live hours for Site Health failed for {Site}", _slug);
            }
        }
        return result;
    }

    /// <summary>When each stored LAN Health hour in [from, to) was last computed, so Site Health can redo hours scored before it.</summary>
    public async Task<IReadOnlyDictionary<DateTime, DateTime>> GetSiteHourTimesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        await using var db = await _db(ct);
        return (await db.LanHealthSiteHours.AsNoTracking()
                .Where(h => h.HourStart >= from && h.HourStart < to && h.HourImpairment != null)
                .Select(h => new { h.HourStart, h.ComputedAt }).ToListAsync(ct))
            .ToDictionary(h => DateTime.SpecifyKind(h.HourStart, DateTimeKind.Utc), h => DateTime.SpecifyKind(h.ComputedAt, DateTimeKind.Utc));
    }

    /// <summary>
    /// Fabric links that are unhealthy now and out of character: well below the score the same link
    /// had over the same 48 hours a week ago. A link that always looks like this never holds a rollout.
    /// </summary>
    public async Task<List<LinkHealth>> GetOutOfCharacterFabricLinksAsync(CancellationToken ct)
    {
        var topology = await GetTopologyAsync(ct);
        if (topology == null) return [];
        var now = await GetReportAsync(48, TimeZoneInfo.Utc, DisplayFormat.Fallback, ct);
        await using var db = await _db(ct);
        var end = FloorHour(DateTime.UtcNow).AddDays(-7);
        var rows = await LoadRowsAsync(db, end.AddHours(-48), end, ct);
        var before = LanHealthReportBuilder.Build(topology.Links, rows, new Dictionary<string, List<LanHealthLinkHour>>(),
            topology.Exclusions, topology.ScoreFabricOnly, null, FindingText.Utc, end.AddHours(-48), end);
        return now.Links.Where(l => l.Link.IsFabric && l.Score < 70
                && (before.Links.FirstOrDefault(b => b.Link.Id == l.Link.Id)?.Score ?? 100) - l.Score >= 20)
            .ToList();
    }

    internal static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);
}

/// <summary>Site Health's read of LAN Health's hours.</summary>
public static class LanHealthSiteHours
{
    public static Task<IReadOnlyDictionary<DateTime, (double Impairment, string? Worst, string? WorstLinkId)>> ReadAsync(LanHealthEngine engine, DateTime from, DateTime to, CancellationToken ct) =>
        engine.GetSiteHoursAsync(from, to, ct);

    public static Task<IReadOnlyDictionary<DateTime, DateTime>> ReadTimesAsync(LanHealthEngine engine, DateTime from, DateTime to, CancellationToken ct) =>
        engine.GetSiteHourTimesAsync(from, to, ct);
}
