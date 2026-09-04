using System.Collections.Concurrent;
using NetworkOptimizer.WiFi.Providers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Which bands each client has been seen on, per site.
///
/// The lookback is long by design, so re-running it on every page refresh would be waste rather
/// than freshness: a band a client has associated on does not stop being one it supports.
/// </summary>
public sealed class MeasuredClientBandCache
{
    /// <summary>How long an answer stands before it is queried again.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The site's cached answer, or null when there is none or it has aged out.</summary>
    public IReadOnlyDictionary<string, IReadOnlyCollection<string>>? Get(string slug, DateTime now)
        => _entries.TryGetValue(slug, out var entry) && now - entry.At <= Ttl ? entry.Bands : null;

    /// <summary>Records the site's answer.</summary>
    public void Set(string slug, IReadOnlyDictionary<string, IReadOnlyCollection<string>> bands, DateTime now)
        => _entries[slug] = new Entry(bands, now);

    private readonly record struct Entry(IReadOnlyDictionary<string, IReadOnlyCollection<string>> Bands, DateTime At);
}

/// <summary>
/// Serves AP-measured client readings out of the monitoring time series.
///
/// Nothing here polls an access point. The AP Agents are already read every thirty seconds by
/// <see cref="ApAgentTelemetryCollector"/>, which writes the agent's numbers for the access points
/// it covers and leaves the rest to the console tier, so the series already holds the best
/// available data per access point and reading it again live would duplicate that load per view.
/// </summary>
public sealed class InfluxMeasuredClientSource : IMeasuredWirelessClientSource
{
    /// <summary>
    /// How old the newest reading may be and still stand. Matches
    /// <see cref="ApAgentCoverageLedger.ClaimTtl"/>, which is the system's existing definition of an
    /// access point still being the agent's to write: the collector folds on a thirty-second window,
    /// so this rides out a missed window and no more.
    /// </summary>
    public static readonly TimeSpan MaxReadingAge = ApAgentCoverageLedger.ClaimTtl;

    /// <summary>Queried range for live state. Wider than the age gate so the newest row is reachable.</summary>
    private static readonly TimeSpan LiveQueryWindow = TimeSpan.FromMinutes(3);

    /// <summary>How far back band evidence is drawn from. A day covers a client's normal routine.</summary>
    private static readonly TimeSpan BandLookback = TimeSpan.FromHours(24);

    private readonly MonitoringInfluxRegistry _influxRegistry;
    private readonly ApAgentTelemetryRegistry _telemetryRegistry;
    private readonly MeasuredClientBandCache _bandCache;
    private readonly ILogger<InfluxMeasuredClientSource> _logger;
    private readonly string _siteSlug;

    /// <summary>Creates the source for the site in context.</summary>
    public InfluxMeasuredClientSource(
        MonitoringInfluxRegistry influxRegistry,
        ApAgentTelemetryRegistry telemetryRegistry,
        MeasuredClientBandCache bandCache,
        SiteContextService siteContext,
        ILogger<InfluxMeasuredClientSource> logger)
    {
        _influxRegistry = influxRegistry;
        _telemetryRegistry = telemetryRegistry;
        _bandCache = bandCache;
        _logger = logger;
        _siteSlug = siteContext.Slug;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MeasuredWirelessClient>>> GetMeasuredClientsAsync(
        IReadOnlyCollection<string> apMacs,
        CancellationToken cancellationToken = default)
    {
        var empty = (IReadOnlyDictionary<string, IReadOnlyList<MeasuredWirelessClient>>)
            new Dictionary<string, IReadOnlyList<MeasuredWirelessClient>>();
        if (apMacs.Count == 0) return empty;

        // Asked per access point, and answered from memory: a site with no agent covering anything
        // costs nothing here and reaches the console path exactly as it does today.
        var collector = _telemetryRegistry.GetFor(_siteSlug);
        var covered = apMacs
            .Select(mac => mac.Trim().ToLowerInvariant())
            .Where(mac => mac.Length > 0 && collector.CoversAp(mac))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (covered.Count == 0) return empty;

        var influx = _influxRegistry.GetFor(_siteSlug);
        if (!influx.IsConfigured) return empty;

        var now = DateTime.UtcNow;
        var rows = await influx.QueryWifiClientSamplesAsync(
            clientMac: null, from: now - LiveQueryWindow, to: now, aggregateWindow: null, ct: cancellationToken);
        if (rows.Count == 0) return empty;

        var bands = await GetObservedBandsAsync(influx, now, cancellationToken);
        var measured = MeasuredClientReducer.Reduce(rows, covered, bands, now, MaxReadingAge);

        _logger.LogDebug("AP-measured readings cover {Aps} access points and {Clients} clients (site {Site})",
            measured.Count, measured.Sum(kv => kv.Value.Count), _siteSlug);

        return measured;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MeasuredClientSample>> GetMeasuredClientHistoryAsync(
        string clientMac,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan bucket,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(clientMac) || end <= start) return Array.Empty<MeasuredClientSample>();

        var influx = _influxRegistry.GetFor(_siteSlug);
        if (!influx.IsConfigured) return Array.Empty<MeasuredClientSample>();

        // No source filter, unlike live state: over a range our own series is the record, whichever
        // tier wrote it, and the console report is only there for the buckets we never held.
        var rows = await influx.QueryWifiClientSamplesAsync(
            clientMac, start.UtcDateTime, end.UtcDateTime, bucket, cancellationToken);

        return MeasuredClientReducer.ReduceHistory(rows, bucket);
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?> GetObservedBandsAsync(
        Storage.Services.MonitoringInfluxClient influx,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var cached = _bandCache.Get(_siteSlug, now);
        if (cached != null) return cached;

        var bands = await influx.QueryWifiClientBandsAsync(now - BandLookback, now, cancellationToken);
        _bandCache.Set(_siteSlug, bands, now);
        return bands;
    }
}
