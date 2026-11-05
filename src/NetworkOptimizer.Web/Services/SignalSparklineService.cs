using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class SignalSparklineService : ISignalSparklineService
{
    /// <summary>How far back a sparkline reaches.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // 96 points across the card. The panel appends the live reading as the last point, so a
    // cache this long never leaves the line behind the value beside it.
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<SignalSparklineService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, IReadOnlyList<SparkPoint>? Value)> _cache = new();

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

    // Resolved per call, like the sibling site-scoped services: the circuit's site can change.
    private MonitoringInfluxClient Influx => _influxRegistry.GetFor(_siteContext.Slug);

    private async Task<IReadOnlyList<SparkPoint>?> CachedAsync(
        string key, string measurement, (string, string)[] tags, string field)
    {
        key = $"{_siteContext.Slug}|{key}";
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
            return hit.Value;

        IReadOnlyList<SparkPoint>? value;
        try
        {
            var rows = await Influx.QueryFieldSeriesAsync(measurement, tags, field, now - Window, now, Bucket);
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
