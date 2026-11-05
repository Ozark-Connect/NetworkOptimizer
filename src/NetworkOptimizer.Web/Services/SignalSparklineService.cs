using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class SignalSparklineService : ISignalSparklineService
{
    /// <summary>How far back an access-network sparkline reaches (ONT, cable modem, cellular).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// How far back a Live View tile reaches. Its readings move in minutes, and the tile opens the
    /// full series on a click.
    /// </summary>
    public static readonly TimeSpan TileWindow = TimeSpan.FromHours(1);

    private static readonly TimeSpan TileBucket = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan TileCacheFor = TimeSpan.FromSeconds(30);

    // 96 points across the card. The panel appends the live reading as the last point, so a
    // cache this long never leaves the line behind the value beside it.
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<SignalSparklineService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<SparkPoint>? Value)> _cache = new();
    private readonly Dictionary<string, (DateTime At, Task<LiveViewSparklines> Task)> _liveView = new();

    public SignalSparklineService(
        MonitoringInfluxRegistry influxRegistry,
        SiteContextService siteContext,
        ILogger<SignalSparklineService> logger)
    {
        _influxRegistry = influxRegistry;
        _siteContext = siteContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SparkPoint>?> GetSfpRxAsync(string deviceMac, string portName) =>
        CachedAsync($"sfp:{deviceMac}:{portName}", "sfp",
            new[] { ("device_mac", deviceMac), ("port_name", portName) }, "rx_power_dbm");

    /// <inheritdoc />
    public Task<IReadOnlyList<SparkPoint>?> GetOntRxAsync(int ontId) =>
        CachedAsync($"ont:{ontId}", "ont", new[] { ("ont_id", ontId.ToString()) }, "rx_power_dbm");

    /// <inheritdoc />
    public Task<IReadOnlyList<SparkPoint>?> GetCmSnrAsync(int cmId) =>
        CachedAsync($"cm:{cmId}", "cable_modem", new[] { ("cm_id", cmId.ToString()) }, "ds_snr_avg_db");

    /// <inheritdoc />
    public Task<IReadOnlyList<SparkPoint>?> GetCellularQualityAsync(int modemId) =>
        CachedAsync($"cell:{modemId}", "cellular", new[] { ("modem_id", modemId.ToString()) }, "signal_quality");

    /// <inheritdoc />
    /// <remarks>The LTE leg is written with network_mode "LTE"; the NR leg under the mode's own label ("5G NSA", "5G SA").</remarks>
    public Task<IReadOnlyList<SparkPoint>?> GetCellularRsrpAsync(int modemId, bool nr) =>
        nr
            ? CachedAsync($"cell-rsrp:{modemId}:nr", "cellular", new[] { ("modem_id", modemId.ToString()) }, "rsrp",
                excludeTags: new[] { ("network_mode", "LTE") })
            : CachedAsync($"cell-rsrp:{modemId}:lte", "cellular", new[] { ("modem_id", modemId.ToString()), ("network_mode", "LTE") }, "rsrp");

    /// <inheritdoc />
    public Task<LiveViewSparklines> GetLiveViewAsync(LiveViewSparkRequest request)
    {
        var key = $"{_siteContext.Slug}|liveview|{request.Key}";
        var now = DateTime.UtcNow;
        lock (_liveView)
        {
            // The task itself is cached, so tiles rendering together await one fetch, not nine.
            if (_liveView.TryGetValue(key, out var hit) && now - hit.At < TileCacheFor)
                return hit.Task;
            var task = LoadLiveViewAsync(request, now);
            _liveView[key] = (now, task);
            return task;
        }
    }

    private async Task<LiveViewSparklines> LoadLiveViewAsync(LiveViewSparkRequest request, DateTime now)
    {
        try
        {
            var from = now - TileWindow;
            var targets = request.IspTargetIds.Concat(request.TransitTargetIds)
                .Concat(string.IsNullOrEmpty(request.GatewayTargetId) ? [] : [request.GatewayTargetId])
                .Distinct(StringComparer.Ordinal).ToList();
            var latencyTask = Influx.QueryTargetFieldSeriesAsync(targets, [RttField, LossField], from, now, TileBucket);
            var healthTask = string.IsNullOrEmpty(request.GatewayMac)
                ? Task.FromResult(new Dictionary<string, List<(DateTime, double)>>())
                : Influx.QueryDeviceHealthFieldSeriesAsync(request.GatewayMac, [CpuField, MemoryField, TemperatureField], from, now, TileBucket);
            await Task.WhenAll(latencyTask, healthTask);
            var latency = latencyTask.Result;
            var health = healthTask.Result;

            var series = new Dictionary<LiveViewSpark, IReadOnlyList<SparkPoint>>();
            void Put(LiveViewSpark tile, IEnumerable<(DateTime Time, double Value)>? rows)
            {
                var points = rows?.Select(r => new SparkPoint(r.Time, r.Value)).ToList();
                // Two points is the least that draws a line.
                if (points is { Count: >= 2 }) series[tile] = points;
            }
            IEnumerable<(DateTime, double)>? Target(string? id, string field) =>
                id != null && latency.TryGetValue((id, field), out var rows) ? rows : null;

            Put(LiveViewSpark.GatewayRtt, Target(request.GatewayTargetId, RttField));
            Put(LiveViewSpark.GatewayLoss, Target(request.GatewayTargetId, LossField));
            var floor = IspAccessFloor(latency, request.IspTargetIds);
            Put(LiveViewSpark.IspRtt, floor.Where(s => s.RttAvgMs.HasValue).Select(s => (s.Time, s.RttAvgMs!.Value)));
            Put(LiveViewSpark.IspLoss, floor.Where(s => s.LossPercent.HasValue).Select(s => (s.Time, s.LossPercent!.Value)));
            Put(LiveViewSpark.TransitRtt, MeanAcrossTargets(latency, request.TransitTargetIds, RttField));
            Put(LiveViewSpark.TransitLoss, MeanAcrossTargets(latency, request.TransitTargetIds, LossField));
            Put(LiveViewSpark.GatewayCpu, health.GetValueOrDefault(CpuField));
            Put(LiveViewSpark.GatewayMemory, health.GetValueOrDefault(MemoryField));
            Put(LiveViewSpark.GatewayTemperature, health.GetValueOrDefault(TemperatureField));
            return new LiveViewSparklines(series);
        }
        catch (Exception ex)
        {
            // The tiles show their live readings on their own.
            _logger.LogDebug(ex, "Live View sparklines unavailable");
            return LiveViewSparklines.Empty;
        }
    }

    private const string RttField = "rtt_avg_ms";
    private const string LossField = "loss_percent";
    private const string CpuField = "cpu_percent";
    private const string MemoryField = "memory_used_percent";
    private const string TemperatureField = "temperature_c";

    /// <summary>
    /// The access layer's latency exactly as ISP Health reads it (<see cref="AccessFloor"/>): per
    /// window, the lowest RTT across the access hops, with loss from the lowest-median hop. Built the
    /// same way as SiteHealthJob's access floor.
    /// </summary>
    internal static List<LatencySample> IspAccessFloor(
        IReadOnlyDictionary<(string TargetId, string Field), List<(DateTime Time, double Value)>> perTarget,
        IEnumerable<string> targetIds)
    {
        var hops = targetIds.Distinct(StringComparer.Ordinal)
            .Select(id =>
            {
                var rtt = perTarget.TryGetValue((id, RttField), out var r) ? r : [];
                var loss = perTarget.TryGetValue((id, LossField), out var l) ? l.ToDictionary(p => p.Time, p => p.Value) : [];
                var rttByTime = rtt.ToDictionary(p => p.Time, p => p.Value);
                return rttByTime.Keys.Union(loss.Keys).Order()
                    .Select(t => new LatencySample(t,
                        rttByTime.TryGetValue(t, out var v) ? v : null, null, null,
                        loss.TryGetValue(t, out var lp) ? lp : null))
                    .ToList();
            })
            .Where(s => s.Any(p => p.RttAvgMs != null))
            .ToList();
        if (hops.Count == 0) return [];
        var lowest = hops.MinBy(s => SeriesStats.Median(s.Where(p => p.RttAvgMs != null).Select(p => p.RttAvgMs!.Value).ToList()) ?? double.MaxValue)!;
        return AccessFloor.Build(hops, lowest);
    }

    /// <summary>The mean across targets per window, as the Transit tiles read their hops. Windows no target reported in are left out.</summary>
    internal static List<(DateTime Time, double Value)> MeanAcrossTargets(
        IReadOnlyDictionary<(string TargetId, string Field), List<(DateTime Time, double Value)>> perTarget,
        IEnumerable<string> targetIds, string field) =>
        targetIds.Distinct(StringComparer.Ordinal)
            .SelectMany(id => perTarget.TryGetValue((id, field), out var rows) ? rows : [])
            .GroupBy(r => r.Time)
            .Select(g => (g.Key, g.Average(r => r.Value)))
            .OrderBy(r => r.Key)
            .ToList();
    // Resolved per call, like the sibling site-scoped services: the circuit's site can change.
    private MonitoringInfluxClient Influx => _influxRegistry.GetFor(_siteContext.Slug);

    private async Task<IReadOnlyList<SparkPoint>?> CachedAsync(
        string key, string measurement, (string, string)[] tags, string field,
        (string, string)[]? excludeTags = null)
    {
        key = $"{_siteContext.Slug}|{key}";
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
            return hit.Value;

        IReadOnlyList<SparkPoint>? value;
        try
        {
            var rows = await Influx.QueryFieldSeriesAsync(measurement, tags, field, now - Window, now, Bucket, excludeTags);
            // Two points is the least that draws a line.
            value = rows.Count >= 2 ? rows.Select(r => new SparkPoint(r.Time, r.Value)).ToList() : null;
        }
        catch (Exception ex)
        {
            // The hero shows the live reading on its own.
            _logger.LogDebug(ex, "Signal sparkline unavailable for {Key}", key);
            value = null;
        }
        _cache[key] = (now, value);
        return value;
    }
}
