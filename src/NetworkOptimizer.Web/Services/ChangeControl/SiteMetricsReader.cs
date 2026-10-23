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
}

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

    /// <summary>The lanes for a range: WAN latency and loss (idle vs loaded), WAN throughput, gateway CPU, Wi-Fi clients.</summary>
    public async Task<List<TimelineLane>> LanesAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        if (to - from > MaxRange) from = to - MaxRange;
        var lanes = new List<TimelineLane>();
        try
        {
            var (gatewayMac, counterIfs) = await _isp.GetWanCounterInterfacesAsync(ct);
            var window = Window(to - from, Math.Max(10, _isp.Options.LoadWindowSeconds));
            var latency = await _influx.QueryMeanIspTransitLatencyAsync(from, to, await _isp.GetLossPoolTargetIdsAsync(ct), window, null, ct);
            IReadOnlyList<MonitoringInfluxClient.WanRatePoint> rates = [];
            if (gatewayMac != null && counterIfs.Count > 0)
                rates = await _influx.QueryGatewayWanRatesAsync(gatewayMac, [counterIfs[0]], from, to, window, ct: ct);

            var load = await ClassifyAsync(rates, ct);
            bool Loaded(DateTime t) => load.TryGetValue(Floor(t, window), out var w) && (w.IsLoadedDown || w.IsLoadedUp);
            bool Idle(DateTime t) => !load.TryGetValue(Floor(t, window), out var w) || w.IsIdle;

            lanes.Add(new TimelineLane
            {
                Key = "latency", Title = "WAN Latency", Subtitle = "Idle vs loaded, ms", SeriesAName = "idle", SeriesBName = "loaded",
                SeriesA = latency.Select(p => (p.Time, Idle(p.Time) ? p.RttAvgMs : null)).ToList(),
                SeriesB = latency.Select(p => (p.Time, Loaded(p.Time) ? p.RttAvgMs : null)).ToList(),
            });
            lanes.Add(new TimelineLane
            {
                Key = "loss", Title = "WAN Loss", Subtitle = "Idle vs loaded, %", SeriesAName = "idle", SeriesBName = "loaded",
                SeriesA = latency.Select(p => (p.Time, Idle(p.Time) ? p.LossPercent : null)).ToList(),
                SeriesB = latency.Select(p => (p.Time, Loaded(p.Time) ? p.LossPercent : null)).ToList(),
            });
            lanes.Add(new TimelineLane
            {
                Key = "throughput", Title = "WAN Throughput", Subtitle = "Mbps", SeriesAName = "down", SeriesBName = "up",
                SeriesA = rates.Select(r => (r.Time, r.DownloadBps / 1e6)).ToList(),
                SeriesB = rates.Select(r => (r.Time, r.UploadBps / 1e6)).ToList(),
            });
            if (gatewayMac != null)
            {
                var health = await _influx.QueryDeviceHealthAsync(gatewayMac, from, to, window, ct);
                lanes.Add(new TimelineLane
                {
                    Key = "cpu", Title = "Gateway CPU", Subtitle = "%", SeriesAName = "CPU",
                    SeriesA = health.Select(h => (h.Time, h.CpuPercent)).ToList(),
                });
            }
            var clients = await _influx.QueryWifiClientSamplesAsync(null, from, to, window, ct);
            lanes.Add(new TimelineLane
            {
                Key = "clients", Title = "Clients", Subtitle = "Wi-Fi", SeriesAName = "Wi-Fi",
                SeriesA = clients.GroupBy(c => Floor(c.Time, window))
                    .OrderBy(g => g.Key)
                    .Select(g => (g.Key, (double?)g.Select(c => c.ClientMac).Distinct().Count())).ToList(),
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Timeline lanes could not be read");
        }

        return lanes.Select(l => new TimelineLane
        {
            Key = l.Key, Title = l.Title, Subtitle = l.Subtitle, SeriesAName = l.SeriesAName, SeriesBName = l.SeriesBName,
            SeriesA = l.SeriesA, SeriesB = l.SeriesB,
            LatestA = Format(l.Key, l.SeriesA.LastOrDefault(p => p.Value != null).Value),
            LatestB = Format(l.Key, l.SeriesB.LastOrDefault(p => p.Value != null).Value),
        }).ToList();
    }

    private async Task<Dictionary<DateTime, LoadWindow>> ClassifyAsync(IReadOnlyList<MonitoringInfluxClient.WanRatePoint> rates, CancellationToken ct)
    {
        if (rates.Count == 0) return [];
        var (down, up) = await _isp.GetExpectedWanSpeedsAsync(ct);
        return LoadClassifier.Classify(rates.Select(r => new ThroughputSample(r.Time, r.DownloadBps, r.UploadBps)).ToList(), down, up, _isp.Options);
    }

    private static DateTime Floor(DateTime t, TimeSpan window) =>
        new(t.Ticks - t.Ticks % window.Ticks, DateTimeKind.Utc);

    private static string? Format(string key, double? v) => v == null ? null : key switch
    {
        "loss" => v.Value.ToString("0.0"),
        "throughput" => v.Value.ToString("0"),
        _ => v.Value.ToString("0"),
    };

    /// <summary>A change's before/after: one hour each side, the mean of each metric (Firmware Rollout's report numbers).</summary>
    public async Task<List<MetricDelta>> BeforeAfterAsync(DateTime at, CancellationToken ct)
    {
        var hour = TimeSpan.FromHours(1);
        var result = new List<MetricDelta>();
        if (DateTime.UtcNow < at + TimeSpan.FromMinutes(5)) return result;
        try
        {
            var before = await LanesAsync(at - hour, at, ct);
            var after = await LanesAsync(at, Min(at + hour, DateTime.UtcNow), ct);
            void Add(string label, string key, bool b, string unit, string fmt, bool higherIsWorse = true)
            {
                var x = Mean(before, key, b);
                var y = Mean(after, key, b);
                if (x == null && y == null) return;
                var worse = x != null && y != null && (higherIsWorse ? y > x * 1.1 : y < x * 0.9);
                result.Add(new MetricDelta(label, x?.ToString(fmt) ?? "-", (y?.ToString(fmt) ?? "-") + unit, worse));
            }
            Add("Wi-Fi clients", "clients", false, "", "0", higherIsWorse: false);
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
