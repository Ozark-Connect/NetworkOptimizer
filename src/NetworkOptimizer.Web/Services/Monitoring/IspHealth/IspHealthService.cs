using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring.IspHealth;

/// <summary>
/// Orchestrates ISP Health: loads targets and settings, queries 24 h of latency and
/// WAN throughput from InfluxDB (read-only), runs the detectors and scorer, and
/// caches the report so the live view tiles can read the current score cheaply.
/// Registered as a singleton; all EF access goes through the context factory.
/// </summary>
public class IspHealthService
{
    private static readonly string[] AnycastDnsIps = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "8.8.4.4"];

    private readonly MonitoringInfluxClient _influx;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly ILogger<IspHealthService> _logger;
    private readonly IspHealthOptions _options = new();
    private readonly SemaphoreSlim _computeLock = new(1, 1);

    private IspHealthReport? _cachedReport;
    // The exact per-cluster series the cached report's events were detected on, so the
    // chart renders the same snapshot and its line labels match the event labels (the
    // "+N ms hop" names are rounded relative to a volatile median and must not be
    // recomputed independently per query).
    private List<AsnSeries>? _cachedChartClusters;
    private IspHealthStatus _status = IspHealthStatus.Computing;
    private volatile bool _computing;

    public IspHealthService(
        MonitoringInfluxClient influx,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        ILogger<IspHealthService> logger)
    {
        _influx = influx;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _logger = logger;
    }

    public IspHealthOptions Options => _options;

    /// <summary>
    /// Current score for the live view tiles without blocking. Kicks off a background
    /// recompute when the cache is empty or stale.
    /// </summary>
    public IspHealthSnapshot GetCachedScore()
    {
        var report = _cachedReport;
        if (report != null && DateTime.UtcNow - report.ComputedAt < _options.CacheTtl)
            return new IspHealthSnapshot(IspHealthStatus.Ready, report.OverallScore, report.ComputedAt);

        if (!_computing)
        {
            _ = Task.Run(async () =>
            {
                try { await GetReportAsync(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Background ISP Health compute failed"); }
            });
        }

        // Serve the stale report while the refresh runs; otherwise report pipeline state
        return report != null
            ? new IspHealthSnapshot(IspHealthStatus.Ready, report.OverallScore, report.ComputedAt)
            : new IspHealthSnapshot(_status, null, null);
    }

    public async Task<IspHealthReport?> GetReportAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cached = _cachedReport;
        if (!forceRefresh && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
            return cached;

        await _computeLock.WaitAsync(ct);
        try
        {
            cached = _cachedReport;
            if (!forceRefresh && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
                return cached;

            _computing = true;
            var report = await ComputeAsync(ct);
            if (report != null) _cachedReport = report;
            return report;
        }
        finally
        {
            _computing = false;
            _computeLock.Release();
        }
    }

    /// <summary>Pipeline readiness, for the tab's prerequisite funnels.</summary>
    public IspHealthStatus Status => _cachedReport != null ? IspHealthStatus.Ready : _status;

    private async Task<IspHealthReport?> ComputeAsync(CancellationToken ct)
    {
        if (!_influx.IsConfigured && !await _influx.ReconfigureAsync(ct))
        {
            _status = IspHealthStatus.NotConfigured;
            return null;
        }

        AccessTechnology technology;
        List<MonitoringTarget> targets;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.Enabled)
            {
                _status = IspHealthStatus.NotConfigured;
                return null;
            }

            // Access technology lives per-WAN in WanDiscoveryContexts (the wizard's
            // store, which replaced the global MonitoringSettings column); prefer the
            // primary WAN's context and fall back to the legacy global value.
            var wanContexts = await db.WanDiscoveryContexts.AsNoTracking().ToListAsync(ct);
            var primaryContext = wanContexts
                .OrderBy(c => string.Equals(c.WanInterface, "wan", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault(c => c.AccessTechnology != AccessTechnology.Unknown);
            technology = primaryContext?.AccessTechnology ?? settings.AccessTechnology;

            targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit
                    || t.TargetType == MonitoringTargetType.InternetService))
                .ToListAsync(ct);
        }

        var ispTargets = targets.Where(t => t.TargetType == MonitoringTargetType.AccessIsp).ToList();
        var transitTargets = targets.Where(t => t.TargetType == MonitoringTargetType.Transit).ToList();
        if (ispTargets.Count == 0 && transitTargets.Count == 0)
        {
            _status = IspHealthStatus.NeedsDiscovery;
            return null;
        }

        var profile = IspHealthProfiles.GetProfile(technology);
        if (profile == null)
        {
            _status = IspHealthStatus.NeedsTechnology;
            return null;
        }

        var windowEnd = DateTime.UtcNow;
        var windowStart = windowEnd.AddHours(-_options.ScoreWindowHours);
        // Fine-grained join window so short load bursts (speed tests, downloads)
        // classify as loaded instead of diluting into minute-level means
        var aggregate = TimeSpan.FromSeconds(_options.LoadWindowSeconds);

        var ispSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.AccessIsp, windowStart, windowEnd, aggregate, ct);
        var transitSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.Transit, windowStart, windowEnd, aggregate, ct);
        var internetSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.InternetService, windowStart, windowEnd, aggregate, ct);
        var ratesTask = QueryWanRatesAsync(windowStart, windowEnd, aggregate, ct);
        var speedsTask = ResolveExpectedSpeedsAsync(ct);
        var speedTestsTask = LoadWanSpeedTestsAsync(windowEnd, ct);
        await Task.WhenAll(ispSeriesTask, transitSeriesTask, internetSeriesTask, ratesTask, speedsTask, speedTestsTask);

        var ispSeries = ToSamples(await ispSeriesTask);
        var transitSeries = ToSamples(await transitSeriesTask);
        var internetSeries = ToSamples(await internetSeriesTask);
        var wanRates = await ratesTask;
        var (expectedDown, expectedUp, expectedSource, smartQueuesEnabled) = await speedsTask;
        var wanSpeedTests = await speedTestsTask;

        // New installs: grade once a few hours of latency data exist, not before
        var earliestSample = ispSeries.Values.Concat(transitSeries.Values)
            .Where(s => s.Count > 0)
            .Select(s => s[0].Time)
            .DefaultIfEmpty(windowEnd)
            .Min();
        if ((windowEnd - earliestSample).TotalHours < _options.MinDataHours)
        {
            _status = IspHealthStatus.InsufficientData;
            return null;
        }

        var (firstHop, firstHopTargetId) = PickFirstCleanHop(ispTargets, ispSeries);
        var ispTargetSeries = ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = ispSeries[t.TargetId]
            })
            .ToList();

        // How far the internet sits beyond the access hop here: the rural/metro
        // context the transit reach ceiling normalizes against
        double? internetMedianDelta = null;
        var accessMedian = SeriesStats.Median(firstHop.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList());
        if (accessMedian.HasValue)
        {
            var internetDeltas = internetSeries.Values
                .Select(samples => SeriesStats.Median(samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList()))
                .Where(m => m.HasValue)
                .Select(m => Math.Max(0, m!.Value - accessMedian.Value))
                .ToList();
            if (internetDeltas.Count > 0) internetMedianDelta = SeriesStats.Median(internetDeltas);
        }

        var lossPool = new List<List<LatencySample>>();
        lossPool.AddRange(ispTargets.Where(t => ispSeries.ContainsKey(t.TargetId)).Select(t => ispSeries[t.TargetId]));
        lossPool.AddRange(transitTargets.Where(t => transitSeries.ContainsKey(t.TargetId)).Select(t => transitSeries[t.TargetId]));
        lossPool.AddRange(targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService
                && AnycastDnsIps.Contains(t.Address)
                && internetSeries.ContainsKey(t.TargetId))
            .Select(t => internetSeries[t.TargetId]));

        var (ispGrading, transitGrading, allClusters) = BuildAsnSeriesSets(ispTargets, transitTargets, ispSeries, transitSeries);
        var internetTargetSeries = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeries.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = internetSeries[t.TargetId]
            })
            .ToList();

        var congestionEvents = CongestionDetector.Detect(allClusters, _options);

        // Internet/CDN targets join step detection because routing shifts in a transit
        // network show up on every path that crosses it (per the real shift examples)
        var stepInput = allClusters.Concat(internetTargetSeries).ToList();
        var pathShifts = StepChangeDetector.Detect(stepInput, _options);

        // Cache the exact clusters the events were detected on for the chart
        _cachedChartClusters = allClusters;

        var inputs = new IspHealthInputs
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            FirstHopSeries = firstHop,
            FirstHopTargetId = firstHopTargetId,
            IspTargetSeries = ispTargetSeries,
            LossPoolSeries = lossPool,
            TransitAsnSeries = transitGrading,
            IspAsnSeries = ispGrading,
            AllClusters = allClusters,
            WanRates = wanRates,
            InternetMedianDeltaMs = internetMedianDelta,
            ExpectedDownloadMbps = expectedDown,
            ExpectedUploadMbps = expectedUp,
            ExpectedSpeedSource = expectedSource,
            WanSpeedTests = wanSpeedTests,
            CongestionEvents = congestionEvents,
            PathShifts = pathShifts,
            SmartQueuesEnabled = smartQueuesEnabled
        };

        var report = new IspHealthScorer(_options).Score(inputs, profile);
        _status = IspHealthStatus.Ready;
        _logger.LogDebug("ISP Health computed: {Score} ({Tech}), {Events} congestion events, {Shifts} path shifts",
            report.OverallScore, profile.DisplayName, congestionEvents.Count, pathShifts.Count);
        return report;
    }

    /// <summary>
    /// Per-ASN RTT series for the tab chart (ISP + transit, 24 h, per-minute means)
    /// plus the cached report's events for chart annotations.
    /// </summary>
    public async Task<(List<AsnSeries> Series, IspHealthReport? Report)> GetAsnChartDataAsync(CancellationToken ct = default)
    {
        // Return the exact clusters the report's events were detected on, so chart
        // line labels and the event labels are guaranteed to agree (re-clustering
        // independently would round the "+N ms hop" names differently)
        var report = await GetReportAsync(ct: ct);
        return (_cachedChartClusters ?? new List<AsnSeries>(), report);
    }

    private async Task<List<ThroughputSample>> QueryWanRatesAsync(DateTime from, DateTime to, TimeSpan aggregate, CancellationToken ct)
    {
        try
        {
            var devices = await _connectionService.GetDiscoveredDevicesAsync(ct);
            var gw = devices?.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);
            if (gw?.Mac == null || gw.WanInterfaceNames == null || gw.WanInterfaceNames.Count == 0)
                return new List<ThroughputSample>();

            var rates = await _influx.QueryGatewayWanRatesAsync(gw.Mac, gw.WanInterfaceNames, from, to, aggregate, ct);
            return rates.Select(r => new ThroughputSample(r.Time, r.DownloadBps, r.UploadBps)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not query WAN rates");
            return new List<ThroughputSample>();
        }
    }

    /// <summary>Expected plan speeds for callers outside the scoring pipeline (e.g. loaded-loss investigation).</summary>
    public async Task<(double? DownMbps, double? UpMbps)> GetExpectedWanSpeedsAsync(CancellationToken ct = default)
    {
        var (down, up, _, _) = await ResolveExpectedSpeedsAsync(ct);
        return (down, up);
    }

    /// <summary>
    /// Expected speeds are configured values, never measured: the UniFi WAN provider
    /// capabilities (ISP speeds the user set in UniFi Network) with the Adaptive SQM
    /// nominal speeds as fallback.
    /// </summary>
    private async Task<(double? Down, double? Up, string? Source, bool SmartQueues)> ResolveExpectedSpeedsAsync(CancellationToken ct)
    {
        double? down = null, up = null;
        string? source = null;
        var smartQueues = false;
        try
        {
            var networks = await _connectionService.GetNetworksAsync(ct);
            var wanNets = networks
                .Where(n => string.Equals(n.Purpose, "wan", StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => string.Equals(n.WanNetworkgroup, "wan", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();
            var primary = wanNets.FirstOrDefault();
            if (primary != null)
            {
                if (primary.WanDownloadMbps > 0) down = primary.WanDownloadMbps;
                if (primary.WanUploadMbps > 0) up = primary.WanUploadMbps;
                if (down != null || up != null) source = "UniFi Network";
                smartQueues = primary.WanSmartqEnabled;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not read UniFi WAN provider capabilities");
        }

        if (down == null || up == null)
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var sqmWan = await db.SqmWanConfigurations.AsNoTracking()
                .OrderBy(c => c.WanNumber)
                .FirstOrDefaultAsync(ct);
            if (sqmWan != null)
            {
                down ??= sqmWan.NominalDownloadMbps;
                up ??= sqmWan.NominalUploadMbps;
                source ??= "Adaptive SQM settings";
            }
        }
        return (down, up, source, smartQueues);
    }

    /// <summary>
    /// Server/gateway WAN speed tests only: Cloudflare and UWN runs. Client-initiated
    /// WAN tests (OpenSpeedTest from a browser via an external server) are excluded
    /// because the client's own link contaminates the measurement.
    /// </summary>
    private async Task<List<SpeedTestSample>> LoadWanSpeedTestsAsync(DateTime windowEnd, CancellationToken ct)
    {
        try
        {
            var since = windowEnd.AddDays(-_options.SpeedTestFallbackDays);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var results = await db.Iperf3Results.AsNoTracking()
                .Where(r => r.Success
                    && r.TestTime >= since
                    && (r.Direction == SpeedTestDirection.CloudflareWan
                        || r.Direction == SpeedTestDirection.CloudflareWanGateway
                        || r.Direction == SpeedTestDirection.UwnWan
                        || r.Direction == SpeedTestDirection.UwnWanGateway)
                    && (r.WanNetworkGroup == null || r.WanNetworkGroup.ToLower() == "wan"))
                .OrderByDescending(r => r.TestTime)
                .Select(r => new { r.TestTime, r.DownloadBitsPerSecond, r.UploadBitsPerSecond, r.PingMs, r.DownloadLatencyMs, r.UploadLatencyMs })
                .ToListAsync(ct);
            return results
                .Select(r => new SpeedTestSample(r.TestTime, r.DownloadBitsPerSecond / 1_000_000.0, r.UploadBitsPerSecond / 1_000_000.0,
                    r.PingMs, r.DownloadLatencyMs, r.UploadLatencyMs))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not load WAN speed test results");
            return new List<SpeedTestSample>();
        }
    }

    /// <summary>
    /// The first clean ISP hop: the enabled AccessIsp target with the lowest median
    /// RTT over the window, matching the live ISP RTT card's nearest-hop semantics.
    /// </summary>
    private static (List<LatencySample> Samples, string? TargetId) PickFirstCleanHop(
        List<MonitoringTarget> ispTargets,
        Dictionary<string, List<LatencySample>> ispSeries)
    {
        List<LatencySample>? best = null;
        string? bestId = null;
        double? bestMedian = null;
        foreach (var target in ispTargets)
        {
            if (!ispSeries.TryGetValue(target.TargetId, out var samples)) continue;
            var rtts = samples.Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList();
            var median = SeriesStats.Median(rtts);
            if (median == null) continue;
            if (bestMedian == null || median.Value < bestMedian.Value)
            {
                bestMedian = median;
                best = samples;
                bestId = target.TargetId;
            }
        }
        return (best ?? new List<LatencySample>(), bestId);
    }

    /// <summary>
    /// Builds two views of the monitored networks. Common rules: user-added ISP
    /// endpoints fold into the canonical ISP ASN; transit targets without a resolved
    /// ASN are skipped; within each ASN, co-located hops cluster by median RTT within
    /// AsnHopClusterToleranceMs.
    /// - GRADING (per ASN): the ISP grade is the single lowest-RTT hop (matching the
    ///   live ISP RTT card); transit grades the nearest cluster. Deeper clusters never
    ///   inflate the grade.
    /// - CHART CLUSTERS (chart + detection): every cluster as one grouped line, named
    ///   by its representative (nearest, lowest-RTT) target's real DB name - the same
    ///   labels the Network Performance charts use. A single-target cluster is just
    ///   that target's name. Detection runs per cluster, not per individual target.
    /// </summary>
    private (List<AsnSeries> IspGrading, List<AsnSeries> TransitGrading, List<AsnSeries> ChartClusters) BuildAsnSeriesSets(
        List<MonitoringTarget> ispTargets,
        List<MonitoringTarget> transitTargets,
        Dictionary<string, List<LatencySample>> ispSeries,
        Dictionary<string, List<LatencySample>> transitSeries)
    {
        var ispOverrides = BuildIspAsnOverrides(ispTargets);
        var ispGrading = GroupAndGrade(ispTargets, ispSeries, ispOverrides, gradeLowestTargetOnly: true);
        var ispClusters = BuildClusters(ispTargets, ispSeries, ispOverrides);

        var attributedTransit = transitTargets.Where(t => t.AsnNumber is > 0).ToList();
        var transitGrading = GroupAndGrade(attributedTransit, transitSeries, null, gradeLowestTargetOnly: false);
        var transitClusters = BuildClusters(attributedTransit, transitSeries, null);

        return (ispGrading, transitGrading, ispClusters.Concat(transitClusters).ToList());
    }

    /// <summary>
    /// Maps user-added AccessIsp targets onto the canonical ISP ASN (the most common
    /// ASN among auto-discovered access hops). Their own address may resolve to a
    /// different or missing ASN, but they still measure the access ISP's network.
    /// </summary>
    private static Dictionary<string, (int Asn, string? Name)>? BuildIspAsnOverrides(List<MonitoringTarget> ispTargets)
    {
        var canonical = ispTargets
            .Where(t => t.AutoDiscovered && t.AsnNumber is > 0)
            .GroupBy(t => t.AsnNumber!.Value)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        if (canonical == null) return null;

        var name = canonical.Select(t => t.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        return ispTargets
            .Where(t => !t.AutoDiscovered)
            .ToDictionary(t => t.TargetId, _ => (canonical.Key, name));
    }

    /// <summary>
    /// The graded series per ASN: the single lowest-RTT hop (gradeLowestTargetOnly,
    /// for the ISP, matching the live ISP RTT card) or the nearest cluster of hops
    /// (transit). Deeper hops are excluded so they never inflate the grade.
    /// </summary>
    private List<AsnSeries> GroupAndGrade(
        List<MonitoringTarget> targets,
        Dictionary<string, List<LatencySample>> seriesByTarget,
        Dictionary<string, (int Asn, string? Name)>? asnOverrides,
        bool gradeLowestTargetOnly)
    {
        var grading = new List<AsnSeries>();
        foreach (var group in GroupByAsn(targets, seriesByTarget, asnOverrides))
        {
            var clusters = ClusterByRtt(group, seriesByTarget);
            if (clusters.Count == 0) continue;
            var gradedTargets = gradeLowestTargetOnly
                ? new List<MonitoringTarget> { clusters[0][0] }
                : clusters[0];
            grading.Add(MakeSeries(group.Key, AsnDisplayName(group, asnOverrides), gradedTargets, seriesByTarget));
        }
        return grading;
    }

    /// <summary>
    /// One grouped series per cluster for the chart and detectors, labeled with the
    /// cluster's representative (nearest, lowest-RTT) target's real DB name. Co-located
    /// hops are grouped into a single line; a single-target cluster is just that name.
    /// </summary>
    private List<AsnSeries> BuildClusters(
        List<MonitoringTarget> targets,
        Dictionary<string, List<LatencySample>> seriesByTarget,
        Dictionary<string, (int Asn, string? Name)>? asnOverrides)
    {
        var result = new List<AsnSeries>();
        foreach (var group in GroupByAsn(targets, seriesByTarget, asnOverrides))
        {
            foreach (var cluster in ClusterByRtt(group, seriesByTarget))
            {
                result.Add(MakeSeries(group.Key, cluster[0].Name, cluster, seriesByTarget));
            }
        }
        return result;
    }

    private static IEnumerable<IGrouping<int, MonitoringTarget>> GroupByAsn(
        List<MonitoringTarget> targets,
        Dictionary<string, List<LatencySample>> seriesByTarget,
        Dictionary<string, (int Asn, string? Name)>? asnOverrides)
        => targets
            .Where(t => seriesByTarget.ContainsKey(t.TargetId))
            .GroupBy(t => asnOverrides != null && asnOverrides.TryGetValue(t.TargetId, out var o) ? o.Asn : t.AsnNumber ?? 0);

    private static string? AsnDisplayName(IGrouping<int, MonitoringTarget> group, Dictionary<string, (int Asn, string? Name)>? asnOverrides)
        => group.Select(t => t.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n))
            ?? (asnOverrides != null
                ? group.Select(t => asnOverrides.TryGetValue(t.TargetId, out var o) ? o.Name : null).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                : null)
            ?? group.Select(t => t.Name).FirstOrDefault();

    /// <summary>Greedy clusters of an ASN's hops, ascending by median RTT, split when a hop exceeds the tolerance from its cluster's nearest member.</summary>
    private List<List<MonitoringTarget>> ClusterByRtt(
        IGrouping<int, MonitoringTarget> group,
        Dictionary<string, List<LatencySample>> seriesByTarget)
    {
        var byMedian = group
            .Select(t => (Target: t, Median: SeriesStats.Median(
                seriesByTarget[t.TargetId].Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList())))
            .Where(x => x.Median.HasValue)
            .OrderBy(x => x.Median!.Value)
            .ToList();

        var clusters = new List<List<MonitoringTarget>>();
        var clusterMins = new List<double>();
        foreach (var entry in byMedian)
        {
            if (clusters.Count == 0 || entry.Median!.Value - clusterMins[^1] > _options.AsnHopClusterToleranceMs)
            {
                clusters.Add(new List<MonitoringTarget>());
                clusterMins.Add(entry.Median!.Value);
            }
            clusters[^1].Add(entry.Target);
        }
        return clusters;
    }

    private static AsnSeries MakeSeries(int asn, string? name, List<MonitoringTarget> targets,
        Dictionary<string, List<LatencySample>> seriesByTarget)
        => new()
        {
            AsnNumber = asn,
            AsnName = name,
            TargetIds = targets.Select(t => t.TargetId).ToList(),
            Samples = targets.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList()
        };

    private static Dictionary<string, List<LatencySample>> ToSamples(
        Dictionary<string, List<MonitoringInfluxClient.LatencySeriesPoint>> raw)
    {
        return raw.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(p => new LatencySample(p.Time, p.RttAvgMs, p.RttMaxMs, p.JitterMs, p.LossPercent)).ToList());
    }
}
