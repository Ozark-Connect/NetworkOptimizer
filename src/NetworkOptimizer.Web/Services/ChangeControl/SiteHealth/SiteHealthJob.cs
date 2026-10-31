using System.Diagnostics;
using System.Text.Json;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl.SiteHealth;

/// <summary>
/// Keeps one site's Site Health hours current: each run scores the newest finished hour, or backfills
/// one day (newest first, 90 days deep), then checks changes whose next three hours are stored for a
/// health drop. Every page reads the stored hours, so Influx is touched only here, in bounded runs.
/// </summary>
public sealed class SiteHealthJob
{
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan RunEvery = TimeSpan.FromMinutes(2);
    // An hour is scored once its data has landed (agents and pollers flush within a few minutes).
    private static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);
    // Reads slower than this mean Influx is busy: back off rather than add to it.
    private static readonly TimeSpan SlowRun = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SlowPause = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ChangeWindow = TimeSpan.FromHours(3);
    // WAN rate resolution for load. The classifier needs two loaded samples in a row, so a transfer of
    // a minute or more registers; 5-minute means made a 5-minute download one sample, thrown out as a glitch.
    internal static readonly TimeSpan RateStep = TimeSpan.FromSeconds(30);

    private static readonly string[] AlertTypes =
        ["device.offline", "device.rebooted", "monitoring.wan_outage", "monitoring.wan_outage_partial", "device.recovered", "monitoring.wan_recovered"];

    private readonly string _slug;
    private readonly IChangeControlRepositoryAccessor _repos;
    private readonly Func<MonitoringInfluxClient> _influx;
    private readonly Func<IspHealthService> _isp;
    private readonly Func<UniFiConnectionService> _connection;
    private readonly IAlertEventBus? _bus;
    private readonly ILogger _logger;
    private DateTime _next = DateTime.MinValue;
    // The device list (names and the uplink tree) comes from the console, which connects after start-up.
    private static readonly TimeSpan ConsoleWait = TimeSpan.FromMinutes(15);
    private readonly DateTime _created = DateTime.UtcNow;

    public SiteHealthJob(string slug, IChangeControlRepositoryAccessor repos, Func<MonitoringInfluxClient> influx,
        Func<IspHealthService> isp, Func<UniFiConnectionService> connection, IAlertEventBus? bus, ILogger logger)
    {
        _slug = slug;
        _repos = repos;
        _influx = influx;
        _isp = isp;
        _connection = connection;
        _bus = bus;
        _logger = logger;
    }

    /// <summary>
    /// The current hour (and, for five minutes after the hour, the one before it), scored on partial data
    /// each run so health lines reach now. Never stored.
    /// </summary>
    public IReadOnlyList<SiteHealthHour> Provisional { get; private set; } = [];

    /// <summary>Raised after a run rescores the provisional hours, so open health lines can redraw.</summary>
    public event Action? Scored;

    /// <summary>Stored hours plus any provisional hour not stored yet, ordered by hour.</summary>
    public static List<SiteHealthHour> WithProvisional(IEnumerable<SiteHealthHour> stored, IReadOnlyList<SiteHealthHour> provisional)
    {
        var hours = stored.ToList();
        var have = hours.Select(h => h.HourStart).ToHashSet();
        hours.AddRange(provisional.Where(p => !have.Contains(p.HourStart)));
        return hours.OrderBy(h => h.HourStart).ToList();
    }

    /// <summary>Runs from the site engine's tick; returns at once unless a run is due.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now < _next) return;
        // A site with no UniFi Console has no network of its own to score.
        if (_connection().HasNoConsole) return;
        // Scored before the console connects, every device is nameless and weighs the same.
        if (!_connection().IsConnected && now - _created < ConsoleWait) return;
        _next = now + RunEvery;
        try
        {
            var latest = FloorHour(now - Settle).AddHours(-1);
            var oldest = FloorHour(now - Retention);
            var stored = await _repos.UseAsync((r, c) => r.GetHealthHoursAsync(oldest.AddDays(-14), latest.AddHours(1), c), ct);
            var current = stored.Where(h => h.Version == SiteHealthScorer.Version).Select(h => h.HourStart).ToHashSet();

            DateTime? missing = null;
            for (var h = latest; h >= oldest; h = h.AddHours(-1))
                if (!current.Contains(h)) { missing = h; break; }

            if (missing is { } end)
            {
                // The newest hour alone when it is the one missing; otherwise a day ending at the gap.
                var start = end == latest ? end : Max(oldest, end.AddHours(-23));
                var sw = Stopwatch.StartNew();
                var hours = await ComputeAsync(start, end.AddHours(1), stored, ct);
                await _repos.UseAsync((r, c) => r.SaveHealthHoursAsync(hours, oldest, c), ct);
                if (sw.Elapsed > SlowRun)
                {
                    _next = DateTime.UtcNow + SlowPause;
                    _logger.LogInformation("Site Health for {Site} took {Seconds:0.0} s for {Hours} hour(s); pausing {Minutes} minutes",
                        _slug, sw.Elapsed.TotalSeconds, hours.Count, SlowPause.TotalMinutes);
                }
                stored = stored.Where(s => !hours.Any(h => h.HourStart == s.HourStart)).Concat(hours).ToList();
            }

            await CheckChangesAsync(stored, latest.AddHours(1), ct);

            // The hours after the newest settled one, scored on what has landed so far. Kept in memory and
            // never saved, so change checks and the stored history only ever see finished hours.
            var sw2 = Stopwatch.StartNew();
            Provisional = await ComputeAsync(latest.AddHours(1), FloorHour(now).AddHours(1), stored, ct);
            if (sw2.Elapsed > SlowRun) _next = DateTime.UtcNow + SlowPause;
            Scored?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _next = DateTime.UtcNow + SlowPause;
            _logger.LogWarning(ex, "Site Health run failed for site {Site}", _slug);
        }
    }

    /// <summary>Scores every hour in [from, to).</summary>
    internal async Task<List<SiteHealthHour>> ComputeAsync(DateTime from, DateTime to, IReadOnlyList<SiteHealthHour> stored, CancellationToken ct)
    {
        var influx = _influx();
        var isp = _isp();
        var profile = await isp.GetAccessProfileAsync(ct);
        var primaryKey = NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(await isp.GetScoredWanKeyAsync(ct));
        var poolIds = await isp.GetLossPoolTargetIdsAsync(ct);
        var scope = await isp.GetLatencyWanScopeAsync(ct);
        var (gatewayMac, counterIfs) = await isp.GetWanCounterInterfacesAsync(ct);
        var (expectedDown, expectedUp) = await isp.GetExpectedWanSpeedsAsync(ct);
        var fine = TimeSpan.FromMinutes(SiteHealthScorer.WindowMinutes);

        var lossTask = influx.QueryTargetLatencyMeansAsync(from, to, poolIds, null, fine, scope, ct);
        var satisfactionTask = influx.QuerySatisfactionCountsAsync(from, to, ct);
        var wirelessTask = influx.QueryDistinctClientsByDeviceAsync("wifi_client", from, to, ct);
        var wiredTask = influx.QueryDistinctClientsByDeviceAsync("wired_client", from, to, ct);
        var fabricTask = influx.QueryTargetLatencyMeansAsync(from, to, null, "fabric", fine, null, ct);
        var portsTask = influx.QueryPortErrorRisesAsync(from, to, ct);
        var ratesTask = gatewayMac != null && counterIfs.Count > 0
            ? influx.QueryGatewayWanRatesAsync(gatewayMac, [counterIfs[0]], from, to, RateStep, ct: ct)
            : Task.FromResult<IReadOnlyList<MonitoringInfluxClient.WanRatePoint>>([]);
        var cpuTask = gatewayMac != null
            ? influx.QueryDeviceCpuAsync(gatewayMac, from, to, TimeSpan.FromHours(1), ct)
            : Task.FromResult<IReadOnlyList<(DateTime Time, double Value)>>([]);
        await Task.WhenAll(lossTask, satisfactionTask, wirelessTask, wiredTask, fabricTask, portsTask, ratesTask, cpuTask);

        // The first clean hop: the access target with the lowest median RTT, as ISP Health picks it.
        var lossPool = lossTask.Result;
        var firstHop = lossPool.Where(p => p.TargetType == "accessisp" && p.RttAvgMs != null)
            .GroupBy(p => p.TargetId).OrderBy(g => Median(g.Select(p => p.RttAvgMs!.Value))).Select(g => g.Key).FirstOrDefault();
        var firstHopFine = firstHop == null ? []
            : await influx.QueryTargetLatencyMeansAsync(from, to, [firstHop], null, fine, scope, ct);
        // The rate query stamps each sample at its window's end and the latency reads at the start;
        // restamped at the start so a sample lands in the 5-minute window it measured.
        var rates = ratesTask.Result.Select(r => new ThroughputSample(r.Time - RateStep, r.DownloadBps, r.UploadBps)).ToList();
        var load = expectedDown != null || expectedUp != null
            ? LoadClassifier.Classify(rates, expectedDown, expectedUp, isp.Options)
            : new Dictionary<DateTime, LoadWindow>();
        var windowLoad = WindowLoad(rates, load, TimeSpan.FromSeconds(isp.Options.LoadWindowSeconds), fine,
            expectedDown, expectedUp, isp.Options.LossSaturationLoadFraction);

        // A day past the batch, so a recovery just after it still ends an outage that began inside it.
        var alerts = await _repos.UseAsync((r, c) => r.GetAlertsActiveAsync(from, to.AddDays(1), AlertTypes, c), ct);
        var rollout = await _repos.UseAsync((r, c) => r.GetRolloutDowntimeAsync(from, to, c), ct);
        var fabricDevices = await _repos.UseAsync((r, c) => r.GetFabricTargetDevicesAsync(c), ct);
        List<DiscoveredDevice> devices;
        try { devices = await _connection().GetDiscoveredDevicesAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Site Health: device list unavailable for {Site}; availability weighs devices equally", _slug);
            devices = [];
        }
        var topology = new Topology(devices);
        var gatewayMacs = devices.Where(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway)
            .Select(d => Mac(d.Mac)).Append(gatewayMac is { } g ? Mac(g) : "").Where(m => m.Length > 0).ToHashSet();

        var history = stored.Where(h => h.HourStart < from).ToList();
        var result = new List<SiteHealthHour>();
        for (var hour = from; hour < to; hour = hour.AddHours(1))
        {
            var end = hour.AddHours(1);
            var inputs = BuildInputs(hour, end, lossPool, firstHopFine, load, windowLoad, satisfactionTask.Result, wirelessTask.Result,
                wiredTask.Result, fabricTask.Result, portsTask.Result, cpuTask.Result, alerts, rollout, fabricDevices, topology, gatewayMacs, primaryKey,
                Baseline(history.Concat(result), hour));
            result.Add(SiteHealthScorer.Score(inputs, profile, DateTime.UtcNow));
        }
        return result;
    }

    private HealthHourInputs BuildInputs(
        DateTime hour, DateTime end,
        IReadOnlyList<MonitoringInfluxClient.TargetLatencyMean> lossPool,
        IReadOnlyList<MonitoringInfluxClient.TargetLatencyMean> firstHopFine,
        Dictionary<DateTime, LoadWindow> load, Dictionary<DateTime, WindowLoadSummary> windowLoad,
        IReadOnlyList<MonitoringInfluxClient.SatisfactionCounts> satisfaction,
        IReadOnlyList<(DateTime Hour, string DeviceMac, int Clients)> wireless,
        IReadOnlyList<(DateTime Hour, string DeviceMac, int Clients)> wired,
        IReadOnlyList<MonitoringInfluxClient.TargetLatencyMean> fabric,
        IReadOnlyList<(DateTime Hour, string DeviceMac, string IfName, double Errors, double Packets)> ports,
        IReadOnlyList<(DateTime Time, double Value)> cpu,
        List<AlertHistoryEntry> alerts, List<FirmwareRolloutStep> rollout, Dictionary<string, string> fabricDevices,
        Topology topology, HashSet<string> gatewayMacs, string primaryKey, double? baseline)
    {
        bool In(DateTime t) => t >= hour && t < end;

        // Internet. A 5-minute RTT counts as loaded when most of its window was, idle when all of it was.
        var idle = new List<double>();
        var loaded = new List<double>();
        foreach (var p in firstHopFine.Where(p => In(p.Time) && p.RttAvgMs != null))
        {
            var w = windowLoad.GetValueOrDefault(p.Time);
            if (w.Total > 0 && w.Loaded * 2 >= w.Total) loaded.Add(p.RttAvgMs!.Value);
            else if (load.Count == 0 || (w.Total > 0 && w.Idle == w.Total)) idle.Add(p.RttAvgMs!.Value);
        }
        // The provisional hour has not ended: outage and offline time count against the part that has elapsed.
        var elapsed = Min(end, DateTime.UtcNow) - hour;
        var scale = elapsed > TimeSpan.Zero && elapsed < end - hour ? (end - hour) / elapsed : 1;

        var windows = load.Keys.Count(In);
        var loadedShare = windows > 0 ? (double)load.Count(kv => In(kv.Key) && (kv.Value.IsLoadedDown || kv.Value.IsLoadedUp)) / windows : 0;

        double outageMinutes = 0;
        var anyTotal = false;
        double partialMinutes = 0;
        string? partialShare = null;
        foreach (var a in alerts.Where(a => a.EventType is "monitoring.wan_outage" or "monitoring.wan_outage_partial"))
        {
            if (!string.Equals(NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(Context(a, "wan") ?? a.DeviceId ?? ""), primaryKey,
                    StringComparison.OrdinalIgnoreCase)) continue;
            var lasted = Overlap(a, alerts, hour, end).TotalMinutes;
            if (lasted <= 0) continue;
            var minutes = lasted * scale;
            if (a.EventType == "monitoring.wan_outage_partial")
            {
                var hasFailing = double.TryParse(Context(a, "targets_failing"), out var f);
                var hasTotal = double.TryParse(Context(a, "targets_total"), out var t);
                var known = hasFailing && hasTotal && t > 0;
                // The label states how long it lasted; the provisional hour's scale is for the score only.
                partialMinutes += lasted;
                partialShare ??= known ? $"{f:0} of {t:0} targets" : null;
                minutes *= known ? f / t : 0.5;
            }
            else anyTotal = true;
            outageMinutes += minutes;
        }
        // The weighted minutes read as a whole-WAN outage; a partial-only hour says what it was.
        var outageText = !anyTotal && partialMinutes > 0
            ? $"partial WAN outage{(partialShare is { } share ? $", {share}" : "")}, {partialMinutes:0} min"
            : null;

        string? localCause = null;
        if (alerts.Any(a => a.EventType == "device.rebooted" && gatewayMacs.Contains(Mac(a.DeviceId)) && In(a.TriggeredAt)))
            localCause = "gateway rebooted";
        else if (alerts.Any(a => a.EventType == "device.offline" && gatewayMacs.Contains(Mac(a.DeviceId)) && Overlap(a, alerts, hour, end) > TimeSpan.Zero))
            localCause = "gateway offline";
        else if (cpu.Where(c => In(c.Time)).Select(c => c.Value).DefaultIfEmpty(0).Average() >= 90)
            localCause = "gateway CPU above 90%";

        // Wi-Fi
        var radios = satisfaction.Where(s => In(s.Hour)).ToList();
        var worst = radios.Where(r => r.Total > 0).OrderByDescending(r => (r.Below80 - r.Below60) * 0.5 + r.Below60).FirstOrDefault();
        string? worstRadio = worst is { Below80: > 0 } ? $"{topology.Name(worst.DeviceMac)} {BandLabel(worst.Band)}" : null;

        // Clients and availability
        var clientsByDevice = new Dictionary<string, int>();
        foreach (var c in wireless.Concat(wired).Where(c => In(c.Hour)))
            clientsByDevice[Mac(c.DeviceMac)] = clientsByDevice.GetValueOrDefault(Mac(c.DeviceMac)) + c.Clients;
        var wirelessClients = wireless.Where(c => In(c.Hour)).Sum(c => c.Clients);
        var wiredClients = wired.Where(c => In(c.Hour)).Sum(c => c.Clients);

        var offline = new Dictionary<string, double>();
        var offlineNames = new Dictionary<string, string>();
        foreach (var a in alerts.Where(a => a.EventType == "device.offline"))
        {
            var mac = Mac(a.DeviceId);
            if (mac.Length == 0) continue;
            var fraction = Overlap(a, alerts, hour, end).TotalHours * scale;
            if (fraction <= 0) continue;
            offline[mac] = Math.Min(1, offline.GetValueOrDefault(mac) + fraction);
            offlineNames[mac] = a.DeviceName ?? topology.Name(mac);
        }
        // Firmware Rollout suppresses the offline alerts of the devices it upgrades; the downtime still counts.
        foreach (var s in rollout)
        {
            var mac = Mac(s.DeviceMac);
            if (mac.Length == 0 || s.WentDownAt is not { } wentDown) continue;
            var down = Max(wentDown, hour);
            var back = Min(s.BackAt ?? Min(end, DateTime.UtcNow), end);
            if (back <= down) continue;
            offline[mac] = Math.Min(1, offline.GetValueOrDefault(mac) + (back - down).TotalHours * scale);
            offlineNames.TryAdd(mac, s.DeviceName is { Length: > 0 } name ? name : topology.Name(mac));
        }
        var (availability, availabilityText) = topology.Availability(offline, offlineNames, clientsByDevice);

        // LAN. A device that is down is Availability's, weighted by the clients behind it, so the pings it
        // (or anything behind it) missed are not LAN loss as well.
        var fabricWindows = fabric
            .Where(p => In(p.Time) && p.LossPercent != null
                && !(fabricDevices.TryGetValue(p.TargetId, out var dev) && topology.IsOut(Mac(dev), offline)))
            .GroupBy(p => p.Time)
            .ToDictionary(g => g.Key, g => Median(g.Select(p => p.LossPercent!.Value)));

        // Internet loss per window: the pool's median, how loaded the WAN was, and whether the LAN lost
        // packets alongside it (the probes' own path, not the ISP).
        var lossWindows = lossPool.Where(p => In(p.Time) && p.LossPercent != null)
            .GroupBy(p => p.Time).OrderBy(g => g.Key)
            .Select(g =>
            {
                var loss = Median(g.Select(p => p.LossPercent!.Value));
                var lan = fabricWindows.TryGetValue(g.Key, out var f) && f >= SiteHealthScorer.NoticeableLossPct && f >= loss / 2;
                return new LossWindow(loss, windowLoad.GetValueOrDefault(g.Key).Saturation, lan);
            })
            .ToList();
        var hourPorts = ports.Where(p => In(p.Hour) && !gatewayMacs.Contains(Mac(p.DeviceMac)) && topology.IsWiredPort(p.DeviceMac, p.IfName)).ToList();
        var (link, linkText) = topology.LinkErrors(hourPorts, clientsByDevice);

        return new HealthHourInputs
        {
            Hour = hour,
            LossPct = lossWindows.Count > 0 ? lossWindows.Average(w => w.LossPct) : null,
            LossWindows = lossWindows,
            IdleRttMs = idle.Count > 0 ? Median(idle) : null,
            LoadedRttMs = loaded.Count > 0 ? Median(loaded) : null,
            LoadedShare = loadedShare,
            OutageMinutes = outageMinutes,
            OutageText = outageText,
            LocalCause = localCause,
            BaselineRttMs = baseline,
            WifiSamples = radios.Sum(r => r.Total),
            WifiBelow80 = radios.Sum(r => r.Below80),
            WifiBelow60 = radios.Sum(r => r.Below60),
            WorstRadio = worstRadio,
            WorstRadioShare = worst is { Total: > 0 } ? (double)worst.Below80 / worst.Total : 0,
            WirelessClients = wireless.Count > 0 ? wirelessClients : null,
            WiredClients = wired.Count > 0 ? wiredClients : null,
            Availability = availability,
            AvailabilityText = availabilityText,
            FabricLossPct = fabricWindows.Count > 0 ? fabricWindows.Values.Average() : null,
            FabricWindows = fabricWindows.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList(),
            HasLinkData = hourPorts.Count > 0,
            LinkImpairment = link,
            LinkText = linkText,
        };
    }

    /// <summary>
    /// The latency baseline: the median stored first-hop RTT at the same UTC hour of week over the prior
    /// 14 days (3 values at least), else the median of all prior 14 days, else none.
    /// </summary>
    internal static double? Baseline(IEnumerable<SiteHealthHour> history, DateTime hour)
    {
        var prior = history.Where(h => h.HourStart < hour && h.HourStart >= hour.AddDays(-14) && h.AccessRttMs != null).ToList();
        var same = prior.Where(h => h.HourStart.DayOfWeek == hour.DayOfWeek && h.HourStart.Hour == hour.Hour).Select(h => h.AccessRttMs!.Value).ToList();
        if (same.Count >= 3) return Median(same);
        return prior.Count >= 3 ? Median(prior.Select(h => h.AccessRttMs!.Value)) : null;
    }

    // ---------- Health after a change ----------

    /// <summary>
    /// For each change whose 3 hours after are stored: records local health (ISP part removed) 3 hours
    /// each side, and alerts when the after falls below both the before and the same hours over the
    /// prior 14 days by more than max(5, 2 x their spread).
    /// </summary>
    private async Task CheckChangesAsync(IReadOnlyList<SiteHealthHour> stored, DateTime storedThrough, CancellationToken ct)
    {
        var changes = await _repos.UseAsync((r, c) => r.GetChangesAwaitingHealthAsync(
            storedThrough - Retention, storedThrough - ChangeWindow - TimeSpan.FromHours(1), c), ct);
        foreach (var change in changes)
        {
            var (before, after, baseline, spread) = HealthAround(stored, change.OccurredAt);
            change.HealthBefore = before;
            // -1: no health data after the change, so it is never checked again.
            change.HealthAfter = after ?? -1;
            await _repos.UseAsync((r, c) => r.UpdateChangeAsync(change, c), ct);

            if (after is not { } a || before is not { } b || baseline is not { } typical) continue;
            if (change.ActorKind == ConfigChangeActorKind.NetworkOptimizer && change.PlanId == null) continue;
            var margin = Math.Max(5, 2 * spread);
            if (b - a <= margin || typical - a <= margin) continue;
            if (_bus == null) continue;
            await _bus.PublishAsync(new AlertEvent
            {
                EventType = ChangeAlerts.HealthRegression,
                Source = ChangeAlerts.Source,
                Severity = b - a >= 15 ? AlertSeverity.Error : AlertSeverity.Warning,
                Title = $"Site Health fell from {b:0} to {a:0} after: {change.Summary}",
                Message = $"Site Health (ISP problems left out) averaged {b:0} in the 3 hours before this change and {a:0} in the 3 hours after; " +
                          $"these hours are usually {typical:0}. {ChangeControlService.Who(change)}.",
                SourceUrl = ChangeAlerts.ChangeUrl(change.Id),
                SiteSlug = _slug,
                Context = new Dictionary<string, string>
                {
                    ["change_id"] = change.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["health_before"] = b.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    ["health_after"] = a.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                    ["health_typical"] = typical.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                },
            }, ct);
        }
    }

    /// <summary>Local health 3 hours before and after a time, and the typical value and spread of those after-hours over the prior 14 days.</summary>
    internal static (double? Before, double? After, double? Typical, double Spread) HealthAround(IReadOnlyList<SiteHealthHour> hours, DateTime at)
    {
        var first = FloorHour(at).AddHours(1);
        double? Mean(IEnumerable<SiteHealthHour> hs)
        {
            var values = hs.Select(SiteHealthScorer.LocalHealth).OfType<double>().ToList();
            return values.Count > 0 ? values.Average() : null;
        }
        var before = Mean(hours.Where(h => h.HourStart >= FloorHour(at) - ChangeWindow && h.HourStart < FloorHour(at)));
        var after = Mean(hours.Where(h => h.HourStart >= first && h.HourStart < first + ChangeWindow));
        var hoursOfDay = Enumerable.Range(0, (int)ChangeWindow.TotalHours).Select(i => first.AddHours(i).Hour).ToHashSet();
        var typical = hours.Where(h => h.HourStart < FloorHour(at) && h.HourStart >= at.AddDays(-14) && hoursOfDay.Contains(h.HourStart.Hour))
            .Select(SiteHealthScorer.LocalHealth).OfType<double>().ToList();
        if (typical.Count < 6) return (before, after, null, 0);
        var median = Median(typical);
        return (before, after, median, Median(typical.Select(v => Math.Abs(v - median))));
    }

    // ---------- Helpers ----------

    /// <summary>
    /// One scoring window's WAN load: rate samples classified loaded and idle out of the total, and its
    /// average utilization (the busier direction against plan, as ISP Health's average load) as a share
    /// of <c>LossSaturationLoadFraction</c>, capped at 1.
    /// </summary>
    internal readonly record struct WindowLoadSummary(int Total, int Loaded, int Idle, double Saturation);

    /// <summary>Groups start-stamped rate samples and their load classes into scoring windows.</summary>
    internal static Dictionary<DateTime, WindowLoadSummary> WindowLoad(
        IReadOnlyList<ThroughputSample> rates, Dictionary<DateTime, LoadWindow> load, TimeSpan loadWindow, TimeSpan window,
        double? expectedDownMbps, double? expectedUpMbps, double saturationFraction)
    {
        var result = new Dictionary<DateTime, WindowLoadSummary>();
        foreach (var g in rates.GroupBy(r => CongestionDetector.FloorTime(r.Time, window)))
        {
            int loaded = 0, idle = 0;
            var utilization = new List<double>();
            foreach (var r in g)
            {
                if (load.TryGetValue(CongestionDetector.FloorTime(r.Time, loadWindow), out var w))
                {
                    if (w.IsLoadedDown || w.IsLoadedUp) loaded++;
                    else if (w.IsIdle) idle++;
                }
                var d = expectedDownMbps > 0 ? (r.DownloadBps ?? 0) / (expectedDownMbps.Value * 1_000_000) : 0;
                var u = expectedUpMbps > 0 ? (r.UploadBps ?? 0) / (expectedUpMbps.Value * 1_000_000) : 0;
                utilization.Add(Math.Clamp(Math.Max(d, u), 0, 1));
            }
            var saturation = saturationFraction > 0 ? Math.Min(1, utilization.Average() / saturationFraction) : 0;
            result[g.Key] = new WindowLoadSummary(g.Count(), loaded, idle, saturation);
        }
        return result;
    }

    internal static DateTime FloorHour(DateTime t) => new(t.Year, t.Month, t.Day, t.Hour, 0, 0, DateTimeKind.Utc);

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>The part of [from, to) the alert's condition lasted: until its recovery, not until someone resolved it.</summary>
    private static TimeSpan Overlap(AlertHistoryEntry a, IReadOnlyList<AlertHistoryEntry> all, DateTime from, DateTime to)
    {
        var end = ChangeIncidents.ConditionEnd(a, all);
        var start = a.TriggeredAt > from ? a.TriggeredAt : from;
        var stop = end is { } e && e < to ? e : to;
        // An alert still going on runs to the end of the hour, never past now.
        if (end == null && stop > DateTime.UtcNow) stop = DateTime.UtcNow;
        return stop > start ? stop - start : TimeSpan.Zero;
    }

    private static string? Context(AlertHistoryEntry a, string key)
    {
        if (string.IsNullOrEmpty(a.ContextJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(a.ContextJson);
            return doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // Influx, the console, and alerts spell MACs differently (colons, none, upper case); match on one form.
    internal static string Mac(string? mac) => string.IsNullOrWhiteSpace(mac) ? "" : FirmwareHistoryImport.Mac(mac);

    private static string BandLabel(string band) => band switch
    {
        "2.4ghz" => "2.4 GHz",
        "5ghz" => "5 GHz",
        "6ghz" => "6 GHz",
        _ => band,
    };

    /// <summary>The console's uplink tree, for weighting a device by the clients behind it.</summary>
    internal sealed class Topology
    {
        private readonly Dictionary<string, string> _parent = [];
        private readonly Dictionary<string, List<string>> _children = [];
        private readonly Dictionary<string, string> _names = [];
        private readonly Dictionary<string, DeviceType> _types = [];
        private readonly List<string> _all;

        public Topology(IEnumerable<DiscoveredDevice> devices)
        {
            foreach (var d in devices)
            {
                var mac = Mac(d.Mac);
                if (mac.Length == 0) continue;
                _names[mac] = d.Name;
                _types[mac] = d.Type;
                if (Mac(d.UplinkMac) is { Length: > 0 } up && up != mac)
                {
                    _parent[mac] = up;
                    if (!_children.TryGetValue(up, out var list)) _children[up] = list = [];
                    list.Add(mac);
                }
            }
            _all = _names.Keys.ToList();
        }

        public string Name(string mac) => _names.TryGetValue(Mac(mac), out var n) && n.Length > 0 ? n : mac;

        /// <summary>
        /// A cable, not a radio: every port of a switch, only the Ethernet interfaces of anything else (an
        /// AP's wireless interfaces count radio errors, which Wi-Fi satisfaction already covers). VLAN
        /// subinterfaces repeat their parent's counters and are skipped.
        /// </summary>
        public bool IsWiredPort(string mac, string ifName) =>
            !ifName.Contains('.')
            && (_types.GetValueOrDefault(Mac(mac)) == DeviceType.Switch || ifName.StartsWith("eth", StringComparison.OrdinalIgnoreCase));

        private IEnumerable<string> SelfAndAncestors(string mac)
        {
            var seen = new HashSet<string>();
            for (var m = mac; m != null && seen.Add(m); m = _parent.GetValueOrDefault(m))
                yield return m;
        }

        private IEnumerable<string> SelfAndDescendants(string mac)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>([mac]);
            while (stack.Count > 0)
            {
                var m = stack.Pop();
                if (!seen.Add(m)) continue;
                yield return m;
                foreach (var c in _children.GetValueOrDefault(m) ?? []) stack.Push(c);
            }
        }

        /// <summary>
        /// Share of clients that lost their path: each client-holding device is out for its own offline
        /// time or any ancestor's. With no client counts, devices weigh equally.
        /// </summary>
        public (double Impairment, string? Text) Availability(Dictionary<string, double> offline, Dictionary<string, string> names, Dictionary<string, int> clients)
        {
            if (offline.Count == 0) return (0, null);
            var holders = clients.Where(kv => kv.Value > 0).ToList();
            var total = holders.Sum(kv => kv.Value);
            double impaired;
            if (total > 0)
                impaired = holders.Sum(kv => kv.Value * SelfAndAncestors(kv.Key).Select(m => offline.GetValueOrDefault(m)).DefaultIfEmpty(0).Max()) / total;
            else
            {
                var devices = _all.Count > 0 ? _all : offline.Keys.ToList();
                impaired = devices.Sum(m => SelfAndAncestors(m).Select(x => offline.GetValueOrDefault(x)).DefaultIfEmpty(0).Max()) / devices.Count;
            }
            var worst = offline.OrderByDescending(kv => kv.Value * (total > 0 ? SubtreeClients(kv.Key, clients) : 1)).First();
            return (Math.Clamp(impaired, 0, 1), $"{names.GetValueOrDefault(worst.Key, Name(worst.Key))} offline {worst.Value * 60:0} min");
        }

        /// <summary>True when the device or anything on its uplink path was offline during the hour.</summary>
        public bool IsOut(string mac, Dictionary<string, double> offline) =>
            mac.Length > 0 && SelfAndAncestors(mac).Any(m => offline.GetValueOrDefault(m) > 0);

        private int SubtreeClients(string mac, Dictionary<string, int> clients) =>
            SelfAndDescendants(mac).Sum(m => clients.GetValueOrDefault(m));

        /// <summary>
        /// Port errors per device: input errors over unicast packets received across its ports, ramped
        /// from 0.01% to 1% and weighted by the clients behind the device (equally when unknown).
        /// </summary>
        public (double Impairment, string? Text) LinkErrors(List<(DateTime Hour, string DeviceMac, string IfName, double Errors, double Packets)> ports, Dictionary<string, int> clients)
        {
            var total = clients.Values.Sum();
            var deviceCount = Math.Max(1, _all.Count);
            double sum = 0;
            (string Mac, double Ratio, double Part)? worst = null;
            foreach (var p in ports.GroupBy(p => Mac(p.DeviceMac)))
            {
                var packets = p.Sum(x => x.Packets);
                if (packets < 10_000) continue;
                var ratio = p.Sum(x => x.Errors) / packets;
                var share = total > 0 ? (double)SubtreeClients(p.Key, clients) / total : 1.0 / deviceCount;
                var part = SiteHealthScorer.Ramp(ratio, 0.0001, 0.01) * share;
                if (part <= 0) continue;
                sum += part;
                if (worst == null || part > worst.Value.Part) worst = (p.Key, ratio, part);
            }
            return (Math.Clamp(sum, 0, 1), worst is { } w ? $"{Name(w.Mac)} port errors {w.Ratio * 100:0.###}%" : null);
        }
    }
}
