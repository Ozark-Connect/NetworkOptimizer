using System.Collections.Concurrent;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services;

/// <inheritdoc />
public class ErrorCounterWindowService : IErrorCounterWindowService
{
    /// <summary>How far back the Dashboard cards count errors.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // Counters only move once per device poll, and the cards re-render every 10 s.
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly SiteContextService _siteContext;
    private readonly ILogger<ErrorCounterWindowService> _logger;
    private readonly ConcurrentDictionary<string, (DateTime At, object? Value)> _cache = new();

    public ErrorCounterWindowService(
        MonitoringInfluxRegistry influxRegistry,
        SiteContextService siteContext,
        ILogger<ErrorCounterWindowService> logger)
    {
        _influxRegistry = influxRegistry;
        _siteContext = siteContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task<PonErrorTotals?> GetOntTotalsAsync(int ontId) =>
        CachedAsync($"ont:{ontId}", async (from, to) =>
        {
            var series = await Influx.QueryOntAsync(from, to, ontId.ToString(), Bucket, includePon: true);
            return series.TryGetValue(ontId.ToString(), out var pts)
                ? Totals(
                    pts.Select(p => p.BipErrors), pts.Select(p => p.FecErrors),
                    pts.Select(p => p.Pon?.HecUncorrected), pts.Select(p => p.Pon?.GemRxDropped),
                    pts.Where(p => p.Pon is not null).Select(p => (p.Pon!.DsFecEnabled, p.Pon.UsFecEnabled)))
                : null;
        });

    /// <inheritdoc />
    public Task<PonErrorTotals?> GetSfpTotalsAsync(string deviceMac, string portName) =>
        CachedAsync($"sfp:{deviceMac}:{portName}", async (from, to) =>
        {
            var series = await Influx.QuerySfpPonByModulesAsync(new[] { (deviceMac, portName) }, from, to, Bucket);
            var pts = series.Values.FirstOrDefault();
            return pts is null
                ? null
                : Totals(
                    pts.Select(p => p.BipErrors), pts.Select(p => p.FecErrors),
                    pts.Select(p => p.HecUncorrected), pts.Select(p => p.GemRxDropped),
                    pts.Select(p => (p.DsFecEnabled, p.UsFecEnabled)));
        });

    /// <inheritdoc />
    public Task<CmErrorTotals?> GetCmTotalsAsync(int cmId) =>
        CachedAsync($"cm:{cmId}", async (from, to) =>
        {
            // The poller writes per-poll deltas with resets already handled, so these just add up.
            var series = await Influx.QueryCableModemAsync(from, to, cmId.ToString(), Bucket);
            return series.TryGetValue(cmId.ToString(), out var pts) ? CmTotals(pts) : null;
        });

    /// <summary>Sum of the per-poll deltas; null when no point in the window carries one.</summary>
    internal static CmErrorTotals? CmTotals(IReadOnlyCollection<MonitoringInfluxClient.CmPoint> pts) =>
        pts.Any(p => p.CorrDelta.HasValue || p.UncorrDelta.HasValue)
            ? new CmErrorTotals(pts.Sum(p => p.CorrDelta ?? 0), pts.Sum(p => p.UncorrDelta ?? 0))
            : null;

    /// <summary>
    /// Null when no counter moved or held across two readings, so a device that never reports
    /// them (a DDM-only ONT) gets no error row rather than "- / - / -".
    /// </summary>
    internal static PonErrorTotals? Totals(
        IEnumerable<long?> bip, IEnumerable<long?> fec, IEnumerable<long?> hec, IEnumerable<long?> drops,
        IEnumerable<(long? Ds, long? Us)> fecState)
    {
        var totals = new PonErrorTotals(
            CounterIncrements.Total(bip), CounterIncrements.Total(fec),
            CounterIncrements.Total(hec), CounterIncrements.Total(drops),
            // Same reading as the live path: either direction reporting FEC on means FEC is on.
            fecState.LastOrDefault(s => s.Ds.HasValue || s.Us.HasValue) is var (ds, us) && (ds.HasValue || us.HasValue)
                ? ds == 1 || us == 1
                : null);
        return totals is { Bip: null, Fec: null, HecUncorrected: null, GemRxDropped: null } ? null : totals;
    }

    // Resolved per call, like the sibling site-scoped services: the circuit's site can change.
    private MonitoringInfluxClient Influx => _influxRegistry.GetFor(_siteContext.Slug);

    private async Task<T?> CachedAsync<T>(string key, Func<DateTime, DateTime, Task<T?>> query) where T : class
    {
        key = $"{_siteContext.Slug}|{key}";
        var now = DateTime.UtcNow;
        if (_cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
            return (T?)hit.Value;

        T? value;
        try
        {
            value = await query(now - Window, now);
        }
        catch (Exception ex)
        {
            // The card falls back to the device's own cumulative counters.
            _logger.LogDebug(ex, "24-hour error counters unavailable for {Key}", key);
            value = null;
        }
        _cache[key] = (now, value);
        return value;
    }
}
