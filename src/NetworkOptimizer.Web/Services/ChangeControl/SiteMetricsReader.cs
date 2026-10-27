using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One lane on the timeline: up to two series sharing an axis.</summary>
public sealed class TimelineLane
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string SeriesAName { get; init; } = "";
    public string SeriesBName { get; init; } = "";
    public List<(DateTime Time, double? Value)> SeriesA { get; init; } = [];
    public List<(DateTime Time, double? Value)> SeriesB { get; init; } = [];
    public string? LatestA { get; init; }
    public string? LatestB { get; init; }
    /// <summary>Stacked areas drawn instead of series A's line; series A holds their total.</summary>
    public List<StackLayer> Stack { get; init; } = [];
    /// <summary>Extra named lines on the lane's axis, one per band for the PHY lane.</summary>
    public List<LaneLine> Lines { get; init; } = [];
}

/// <summary>One named line on a lane, with its latest value formatted.</summary>
public sealed record LaneLine(string Name, string Color, List<(DateTime Time, double? Value)> Series, string? Latest);

/// <summary>One stacked area: its lower and upper edge at each time, and its latest value.</summary>
public sealed record StackLayer(string Name, string Color, List<(DateTime Time, double Lower, double Upper)> Points, int Latest);

/// <summary>
/// Reads the timeline's stat lanes and a change's before/after numbers from the site's existing
/// Influx measurements (no new measurement). Latency and loss are split into idle and loaded with
/// ISP Health's own classifier and inputs. Every query is aggregated to about 300 points and the
/// range is capped (spec "Query budget": never a raw long-range query).
/// </summary>
public sealed class SiteMetricsReader
{
    /// <summary>Longest range the lanes read.</summary>
    public static readonly TimeSpan MaxRange = TimeSpan.FromDays(90);
    private const int TargetPoints = 300;

    private readonly MonitoringInfluxClient _influx;
    private readonly IspHealthService _isp;
    private readonly ILogger _logger;

    public SiteMetricsReader(MonitoringInfluxClient influx, IspHealthService isp, ILogger logger)
    {
        _influx = influx;
        _isp = isp;
        _logger = logger;
    }

    private static TimeSpan Window(TimeSpan range, int floorSeconds) =>
        TimeSpan.FromSeconds(Math.Max(floorSeconds, Math.Ceiling(range.TotalSeconds / TargetPoints)));

    private TimeSpan LaneWindow(DateTime from, DateTime to) => Window(to - from, Math.Max(10, _isp.Options.LoadWindowSeconds));

    /// <summary>
    /// Mean RTT and loss across the loss-pool targets: each target's window mean, averaged within its
    /// target type, then across types (the WAN live chart's weighting). The combine runs here, not in
    /// Flux, so the query stays a pushdown shape.
    /// </summary>
    private async Task<List<(DateTime Time, double? Rtt, double? Loss)>> LatencyAsync(DateTime from, DateTime to, TimeSpan window, CancellationToken ct)
    {
        // ISP Health's targets and wan-tag scope: the primary WAN only, so another WAN's degradation stays out.
        var rows = await _influx.QueryIspTransitLatencyByTargetAsync(from, to, await _isp.GetLossPoolTargetIdsAsync(ct), window, ct,
            await _isp.GetLatencyWanScopeAsync(ct));
        static double? Combine(IEnumerable<IGrouping<string, double>> byType)
        {
            var means = byType.Select(t => t.Average()).ToList();
            return means.Count == 0 ? null : means.Average();
        }
        return rows.GroupBy(r => r.Time).OrderBy(g => g.Key).Select(g => (
            g.Key,
            Combine(g.Where(r => r.RttAvgMs != null).GroupBy(r => r.TargetType, r => r.RttAvgMs!.Value)),
            Combine(g.Where(r => r.LossPercent != null).GroupBy(r => r.TargetType, r => r.LossPercent!.Value)))).ToList();
    }

    /// <summary>The site row's sparkline on the fleet timeline: WAN latency only, one query.</summary>
    public async Task<List<(DateTime Time, double? Value)>> LatencySparklineAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        if (to - from > MaxRange) from = to - MaxRange;
        try
        {
            return (await LatencyAsync(from, to, LaneWindow(from, to), ct)).Select(p => (p.Time, p.Rtt)).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Timeline sparkline could not be read");
            return [];
        }
    }

    /// <summary>WAN download and upload in Mbps on the primary WAN's counter interface, or empty.</summary>
    public async Task<List<(DateTime Time, double? Down, double? Up)>> WanThroughputAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var (gatewayMac, counterIfs) = await _isp.GetWanCounterInterfacesAsync(ct);
        if (gatewayMac == null || counterIfs.Count == 0) return [];
        var rates = await _influx.QueryGatewayWanRatesAsync(gatewayMac, [counterIfs[0]], from, to, LaneWindow(from, to), ct: ct);
        return rates.Select(r => (r.Time, r.DownloadBps / 1e6, r.UploadBps / 1e6)).ToList();
    }

    /// <summary>The lanes for a range: WAN latency and loss (idle vs loaded), WAN throughput, gateway CPU, Wi-Fi clients.</summary>
    public async Task<List<TimelineLane>> LanesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        if (to - from > MaxRange) from = to - MaxRange;
        var lanes = new List<TimelineLane>();
        try
        {
            var (gatewayMac, counterIfs) = await _isp.GetWanCounterInterfacesAsync(ct);
            var window = LaneWindow(from, to);
            var latencyTask = LatencyAsync(from, to, window, ct);
            var ratesTask = gatewayMac != null && counterIfs.Count > 0
                ? _influx.QueryGatewayWanRatesAsync(gatewayMac, [counterIfs[0]], from, to, window, ct: ct)
                : Task.FromResult<IReadOnlyList<MonitoringInfluxClient.WanRatePoint>>([]);
            var cpuTask = gatewayMac != null
                ? _influx.QueryDeviceCpuAsync(gatewayMac, from, to, window, ct)
                : Task.FromResult<IReadOnlyList<(DateTime Time, double Value)>>([]);
            var clientsTask = _influx.QueryWifiClientCountsAsync(from, to, window, ct);
            var phyTask = _influx.QueryWifiPhyByBandAsync(from, to, window, ct);
            await Task.WhenAll(latencyTask, ratesTask, cpuTask, clientsTask, phyTask);
            var latency = latencyTask.Result;
            var rates = ratesTask.Result;

            var load = await ClassifyAsync(rates, ct);
            bool Loaded(DateTime t) => load.TryGetValue(Floor(t, window), out var w) && (w.IsLoadedDown || w.IsLoadedUp);
            bool Idle(DateTime t) => !load.TryGetValue(Floor(t, window), out var w) || w.IsIdle;

            lanes.Add(new TimelineLane
            {
                Key = "latency",
                Title = "WAN Latency",
                Subtitle = "Idle vs loaded, ms",
                SeriesAName = "idle",
                SeriesBName = "loaded",
                SeriesA = latency.Select(p => (p.Time, Idle(p.Time) ? p.Rtt : null)).ToList(),
                SeriesB = latency.Select(p => (p.Time, Loaded(p.Time) ? p.Rtt : null)).ToList(),
            });
            lanes.Add(new TimelineLane
            {
                Key = "loss",
                Title = "WAN Loss",
                Subtitle = "Idle vs loaded, %",
                SeriesAName = "idle",
                SeriesBName = "loaded",
                SeriesA = latency.Select(p => (p.Time, Idle(p.Time) ? p.Loss : null)).ToList(),
                SeriesB = latency.Select(p => (p.Time, Loaded(p.Time) ? p.Loss : null)).ToList(),
            });
            lanes.Add(new TimelineLane
            {
                Key = "throughput",
                Title = "WAN Throughput",
                Subtitle = "Mbps",
                SeriesAName = "down",
                SeriesBName = "up",
                SeriesA = rates.Select(r => (r.Time, r.DownloadBps / 1e6)).ToList(),
                SeriesB = rates.Select(r => (r.Time, r.UploadBps / 1e6)).ToList(),
            });
            if (gatewayMac != null)
            {
                lanes.Add(new TimelineLane
                {
                    Key = "cpu",
                    Title = "Gateway CPU",
                    Subtitle = "%",
                    SeriesAName = "CPU",
                    SeriesA = cpuTask.Result.Select(h => (h.Time, (double?)h.Value)).ToList(),
                });
            }
            // Past two days the counts are per day (QueryWifiClientCountsAsync).
            lanes.Add(ClientsLane(clientsTask.Result, to, to - from > TimeSpan.FromDays(2) ? TimeSpan.FromDays(1) : window, from));
            lanes.Add(PhyLane(phyTask.Result, window));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Timeline lanes could not be read");
        }

        return lanes.Select(l => new TimelineLane
        {
            Key = l.Key,
            Title = l.Title,
            Subtitle = l.Subtitle,
            SeriesAName = l.SeriesAName,
            SeriesBName = l.SeriesBName,
            SeriesA = l.SeriesA,
            SeriesB = l.SeriesB,
            Stack = l.Stack,
            Lines = l.Lines,
            LatestA = Format(l.Key, l.SeriesA.LastOrDefault(p => p.Value != null).Value),
            LatestB = Format(l.Key, l.SeriesB.LastOrDefault(p => p.Value != null).Value),
        }).ToList();
    }

    private static readonly (string Band, string Name, string Color)[] Bands =
        [("2.4ghz", "2.4 GHz", "var(--band-24-color)"), ("5ghz", "5 GHz", "var(--band-5-color)"), ("6ghz", "6 GHz", "var(--band-6-color)")];

    /// <summary>Wi-Fi clients stacked by band, 2.4 GHz at the bottom; series A is the total.</summary>
    internal static TimelineLane ClientsLane(IReadOnlyList<(DateTime Time, string Band, int Clients)> counts, DateTime? end = null, TimeSpan? step = null, DateTime? start = null)
    {
        // Flux clamps the first window's start to the range start; snap to its epoch grid so the zero-fill lands on the same slots.
        if (step is { } g && g > TimeSpan.Zero)
            counts = counts.Select(c => (FloorEpoch(c.Time, g), c.Band, c.Clients)).ToList();
        var times = counts.Select(c => c.Time).Distinct().Order().ToList();
        var byKey = counts.GroupBy(c => (c.Time, c.Band)).ToDictionary(g => g.Key, g => g.Sum(c => c.Clients));
        // A window with no clients returns no row: fill the grid from the window holding the range start, so zero plots as zero.
        if (step is { } s && s > TimeSpan.Zero && times.Count > 0)
        {
            var first = times[0];
            if (start is { } b) while (first > b) first -= s;
            var grid = new List<DateTime>();
            for (var t = first; end is { } e0 ? t < e0 : t <= times[^1]; t += s) grid.Add(t);
            times = grid.Union(times).Order().ToList();
        }
        // Counts are stamped at their window's start (a whole day past two days), so the last one holds to the end.
        if (end is { } e && times.Count > 0 && times[^1] < e)
        {
            foreach (var (band, _, _) in Bands)
                if (byKey.TryGetValue((times[^1], band), out var last)) byKey[(e, band)] = last;
            times.Add(e);
        }
        var total = times.ToDictionary(t => t, _ => 0.0);
        var stack = new List<StackLayer>();
        foreach (var (band, name, color) in Bands)
        {
            var points = new List<(DateTime, double, double)>();
            foreach (var t in times)
            {
                var lower = total[t];
                total[t] = lower + byKey.GetValueOrDefault((t, band));
                points.Add((t, lower, total[t]));
            }
            if (points.Any(p => p.Item3 > p.Item2))
                stack.Add(new StackLayer(name, color, points, times.Count == 0 ? 0 : byKey.GetValueOrDefault((times[^1], band))));
        }
        return new TimelineLane
        {
            Key = "clients",
            Title = "Clients",
            Subtitle = "Wi-Fi, by band",
            SeriesAName = "Wi-Fi",
            SeriesA = times.Select(t => (t, (double?)total[t])).ToList(),
            Stack = stack,
        };
    }

    /// <summary>Mean TX PHY rate per band in Mbps, one line per band that has clients.</summary>
    internal static TimelineLane PhyLane(IReadOnlyList<(DateTime Time, string Band, double Kbps)> rates, TimeSpan? step = null) => new()
    {
        Key = "phy",
        Title = "Wi-Fi PHY",
        Subtitle = "TX rate by band, Mbps",
        Lines = Bands.Select(b => (b, Series: WithGaps(rates.Where(r => r.Band == b.Band).Select(r => (r.Time, (double?)(r.Kbps / 1000))).ToList(), step)))
            .Where(x => x.Series.Count > 0)
            .Select(x => new LaneLine(x.b.Name, x.b.Color, x.Series, x.Series[^1].Item2?.ToString("0")))
            .ToList(),
    };

    /// <summary>A null at each missing window, so the line breaks where a band had no clients instead of bridging the gap.</summary>
    private static List<(DateTime, double?)> WithGaps(List<(DateTime Time, double? Value)> series, TimeSpan? step)
    {
        if (step is not { } s || s <= TimeSpan.Zero || series.Count < 2) return series;
        var result = new List<(DateTime, double?)>();
        for (var i = 0; i < series.Count; i++)
        {
            if (i > 0 && series[i].Time - series[i - 1].Time > s) result.Add((series[i - 1].Time + s, null));
            result.Add(series[i]);
        }
        return result;
    }

    private async Task<Dictionary<DateTime, LoadWindow>> ClassifyAsync(IReadOnlyList<MonitoringInfluxClient.WanRatePoint> rates, CancellationToken ct)
    {
        if (rates.Count == 0) return [];
        var (down, up) = await _isp.GetExpectedWanSpeedsAsync(ct);
        return LoadClassifier.Classify(rates.Select(r => new ThroughputSample(r.Time, r.DownloadBps, r.UploadBps)).ToList(), down, up, _isp.Options);
    }

    private static DateTime Floor(DateTime t, TimeSpan window) =>
        new(t.Ticks - t.Ticks % window.Ticks, DateTimeKind.Utc);

    /// <summary>The start of the window holding t, on Flux's grid (aligned to the Unix epoch).</summary>
    private static DateTime FloorEpoch(DateTime t, TimeSpan window) =>
        DateTime.UnixEpoch + TimeSpan.FromTicks((t - DateTime.UnixEpoch).Ticks / window.Ticks * window.Ticks);

    private static string? Format(string key, double? v) => v == null ? null : key switch
    {
        "loss" => v.Value.ToString("0.0"),
        "latency" or "cpu" or "throughput" => TwoSignificant(v.Value),
        _ => v.Value.ToString("0"),
    };

    /// <summary>At least two significant digits: 940, 24, 9.3, 0.85. Whole numbers from 10 up.</summary>
    internal static string TwoSignificant(double v) =>
        Math.Abs(v) >= 10 ? v.ToString("0") : Math.Abs(v) >= 1 ? v.ToString("0.0") : v.ToString("0.00");

    /// <summary>A change's before/after: one hour each side, the mean of each metric (Firmware Rollout's report numbers).</summary>
    public async Task<List<MetricDelta>> BeforeAfterAsync(DateTime at, CancellationToken ct)
    {
        var hour = TimeSpan.FromHours(1);
        var result = new List<MetricDelta>();
        if (DateTime.UtcNow < at + TimeSpan.FromMinutes(5)) return result;
        try
        {
            var beforeTask = LanesAsync(at - hour, at, ct);
            var afterTask = LanesAsync(at, Min(at + hour, DateTime.UtcNow), ct);
            var before = await beforeTask;
            var after = await afterTask;
            void Add(string label, string key, bool b, string unit, string fmt)
            {
                var x = Mean(before, key, b);
                var y = Mean(after, key, b);
                if (x == null && y == null) return;
                var worse = x != null && y != null && y > x * 1.1;
                result.Add(new MetricDelta(label, x?.ToString(fmt) ?? "-", (y?.ToString(fmt) ?? "-") + unit, worse));
            }
            // No Wi-Fi client count: it follows the time of day, and Site Health's Wi-Fi row already rates the Wi-Fi.
            Add("Loaded latency", "latency", true, " ms", "0");
            Add("Loaded loss", "loss", true, "%", "0.0");
            Add("Gateway CPU", "cpu", false, "%", "0");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Before/after metrics could not be read");
        }
        return result;
    }

    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    private static double? Mean(List<TimelineLane> lanes, string key, bool seriesB)
    {
        var lane = lanes.FirstOrDefault(l => l.Key == key);
        var values = (seriesB ? lane?.SeriesB : lane?.SeriesA)?.Where(p => p.Value != null).Select(p => p.Value!.Value).ToList();
        return values is { Count: > 0 } ? values.Average() : null;
    }
}
