using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
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

    // Report and its chart clusters are published together as one immutable snapshot so a
    // reader can never pair a fresh cluster set with a stale report (the chart's
    // "+N ms hop" line labels must match the report's event labels). Single-reference
    // assignment makes the swap atomic; readers take one local copy.
    private sealed record Snapshot(IspHealthReport Report, List<AsnSeries> ChartClusters);
    private Snapshot? _cached;
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
        // The glanceable tile tolerates a longer staleness than the detail tab, so sitting on
        // Live View doesn't drive a full Influx recompute every CacheTtl. A recompute is still
        // kicked off once the tile crosses DashboardScoreTtl (or on first populate); the ISP
        // Health tab uses the shorter CacheTtl (via GetReportAsync) for fresher detail.
        var report = _cached?.Report;
        if (report != null && DateTime.UtcNow - report.ComputedAt < _options.DashboardScoreTtl)
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
        var cached = _cached?.Report;
        if (!forceRefresh && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
            return cached;

        await _computeLock.WaitAsync(ct);
        try
        {
            cached = _cached?.Report;
            if (!forceRefresh && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
                return cached;

            if (forceRefresh)
                _connectionService.ClearCaches();

            _computing = true;
            var (report, chartClusters) = await ComputeAsync(ct);
            if (report != null) _cached = new Snapshot(report, chartClusters);
            return report;
        }
        finally
        {
            _computing = false;
            _computeLock.Release();
        }
    }

    /// <summary>Pipeline readiness, for the tab's prerequisite funnels.</summary>
    /// <summary>
    /// Drop the cached report so the next <see cref="GetReportAsync"/> recomputes from current
    /// data. Called when Upstream Discovery is committed, so the "re-run discovery" banner
    /// clears as soon as the user revisits the tab, without a manual refresh.
    /// </summary>
    public void Invalidate() => _cached = null;

    public IspHealthStatus Status => _cached != null ? IspHealthStatus.Ready : _status;

    private async Task<(IspHealthReport? Report, List<AsnSeries> ChartClusters)> ComputeAsync(CancellationToken ct)
    {
        if (!_influx.IsConfigured && !await _influx.ReconfigureAsync(ct))
        {
            _status = IspHealthStatus.NotConfigured;
            return (null, new List<AsnSeries>());
        }

        AccessTechnology technology;
        List<MonitoringTarget> targets;
        // TargetId -> the monitored hop IPs proven upstream of it (its ancestors), from
        // Upstream Discovery's traces. ISP Health uses these to confirm one hop routes
        // through another before its jitter absolves the other. No live traceroute here.
        Dictionary<string, List<string>> ancestorIpsByTargetId;
        bool hopOrderKnown;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.Enabled)
            {
                _status = IspHealthStatus.NotConfigured;
                return (null, new List<AsnSeries>());
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

            // TODO (multi-WAN): discoveries are read across ALL WANs, not scoped to the WAN
            // being scored. UpstreamDiscovery rows carry WanInterface, but ISP Health scores
            // a single (primary) WAN and ancestry/hopOrderKnown here is global, so a second
            // WAN's discovery data could flip the absolve gate for a WAN that has none of its
            // own. Scope by WanInterface once ISP Health grades per-WAN. See TODO.md.
            var discoveries = await db.UpstreamDiscoveries.AsNoTracking()
                .Where(d => d.IsActive && d.MonitoringTargetId != null)
                .ToListAsync(ct);
            // TargetId -> ancestor hop IPs. Join discovery rows to the loaded targets by PK.
            var targetIdById = targets.ToDictionary(t => t.Id, t => t.TargetId);
            ancestorIpsByTargetId = discoveries
                .Where(d => targetIdById.ContainsKey(d.MonitoringTargetId!.Value))
                .GroupBy(d => targetIdById[d.MonitoringTargetId!.Value])
                .ToDictionary(
                    g => g.Key,
                    g => g.SelectMany(d => (d.AncestorHopIps ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            // Ancestor data exists when any row carries the (non-null) column - distinguishes
            // "no discovery yet / pre-ancestor data" from "on-path but no upstream ancestors".
            hopOrderKnown = discoveries.Any(d => d.AncestorHopIps != null);
        }

        var ispTargets = targets.Where(t => t.TargetType == MonitoringTargetType.AccessIsp).ToList();
        var transitTargets = targets.Where(t => t.TargetType == MonitoringTargetType.Transit).ToList();
        if (ispTargets.Count == 0 && transitTargets.Count == 0)
        {
            _status = IspHealthStatus.NeedsDiscovery;
            return (null, new List<AsnSeries>());
        }

        var profile = IspHealthProfiles.GetProfile(technology);
        if (profile == null)
        {
            _status = IspHealthStatus.NeedsTechnology;
            return (null, new List<AsnSeries>());
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

        // New installs: grade once a few hours of latency data exist, not before.
        // Enabled targets only - a disabled target's stale history must not satisfy the
        // gate when no enabled target has enough data yet.
        var earliestSample = ispTargets.Where(t => ispSeries.ContainsKey(t.TargetId)).Select(t => ispSeries[t.TargetId])
            .Concat(transitTargets.Where(t => transitSeries.ContainsKey(t.TargetId)).Select(t => transitSeries[t.TargetId]))
            .Where(s => s.Count > 0)
            .Select(s => s[0].Time)
            .DefaultIfEmpty(windowEnd)
            .Min();
        if ((windowEnd - earliestSample).TotalHours < _options.MinDataHours)
        {
            _status = IspHealthStatus.InsufficientData;
            return (null, new List<AsnSeries>());
        }

        var (firstHop, firstHopTargetId) = PickFirstCleanHop(ispTargets, ispSeries);
        // Public access hops only - the loaded-latency worst-hop scan must not include a
        // CPE-LAN-side gateway (RFC1918), which sits before the access bottleneck and
        // never sees access congestion.
        var accessHopSeries = ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId) && !NetworkUtilities.IsPrivateIpAddress(t.Address))
            .Select(t => ispSeries[t.TargetId])
            .ToList();
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
            // Enabled InternetService targets only - the influx query returns data for
            // every target ever tagged this type, including disabled ones, so join
            // through the enabled DB list before measuring the reach context.
            var internetDeltas = targets
                .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeries.ContainsKey(t.TargetId))
                .Select(t => SeriesStats.Median(internetSeries[t.TargetId].Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList()))
                .Where(m => m.HasValue)
                .Select(m => Math.Max(0, m!.Value - accessMedian.Value))
                .ToList();
            if (internetDeltas.Count > 0) internetMedianDelta = SeriesStats.Median(internetDeltas);
        }

        // Loss pool: ALL enabled AccessIsp + Transit targets plus well-known anycast DNS.
        // Every probe crosses the access link before reaching its target, so loss on ANY
        // of these is a signal of access-layer loss - including under load, where the
        // question is "did the saturated access link drop packets", not "did transit drop
        // because of my load" (it won't). Pooling many targets gives a denser, more robust
        // access-loss signal than one sparse hop. (Latency, by contrast, uses only the
        // nearest hop because far-hop RTT carries transit variance that isn't the access
        // link's loaded behavior - see PickFirstCleanHop / spec "Measurement sources".)
        var lossPool = new List<List<LatencySample>>();
        lossPool.AddRange(ispTargets.Where(t => ispSeries.ContainsKey(t.TargetId)).Select(t => ispSeries[t.TargetId]));
        lossPool.AddRange(transitTargets.Where(t => transitSeries.ContainsKey(t.TargetId)).Select(t => transitSeries[t.TargetId]));
        lossPool.AddRange(targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService
                && AnycastDnsIps.Contains(t.Address)
                && internetSeries.ContainsKey(t.TargetId))
            .Select(t => internetSeries[t.TargetId]));

        var (ispGrading, transitGrading, allClusters, chartClusters) = BuildAsnSeriesSets(ispTargets, transitTargets, ispSeries, transitSeries, ancestorIpsByTargetId);
        var internetTargetSeries = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeries.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = internetSeries[t.TargetId],
                HopIps = { t.Address },
                // Hops proven upstream of this destination, so its clean end-to-end jitter
                // can absolve an ICMP-deprioritized ISP hop it provably routes through.
                AncestorIps = ancestorIpsByTargetId.TryGetValue(t.TargetId, out var destAnc) ? destAnc : new List<string>()
            })
            .ToList();

        var congestionEvents = CongestionDetector.Detect(allClusters, _options);

        // Internet/CDN targets join step detection because routing shifts in a transit
        // network show up on every path that crosses it (per the real shift examples)
        var stepInput = allClusters.Concat(internetTargetSeries).ToList();
        var pathShifts = StepChangeDetector.Detect(stepInput, _options);

        // chartClusters (one line per cluster) is the chart view computed from the same
        // snapshot the detectors ran on, so deeper-cluster "+N ms hop" labels still match
        // event labels. It is published together with the report (see Snapshot).
        var inputs = new IspHealthInputs
        {
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            FirstHopSeries = firstHop,
            AccessHopSeries = accessHopSeries,
            FirstHopTargetId = firstHopTargetId,
            IspTargetSeries = ispTargetSeries,
            LossPoolSeries = lossPool,
            TransitAsnSeries = transitGrading,
            IspAsnSeries = ispGrading,
            DestinationSeries = internetTargetSeries,
            WanRates = wanRates,
            InternetMedianDeltaMs = internetMedianDelta,
            ExpectedDownloadMbps = expectedDown,
            ExpectedUploadMbps = expectedUp,
            ExpectedSpeedSource = expectedSource,
            WanSpeedTests = wanSpeedTests,
            CongestionEvents = congestionEvents,
            PathShifts = pathShifts,
            SmartQueuesEnabled = smartQueuesEnabled,
            HopOrderKnown = hopOrderKnown
        };

        var report = new IspHealthScorer(_options, _logger).Score(inputs, profile);
        _status = IspHealthStatus.Ready;
        _logger.LogDebug("ISP Health computed: {Score} ({Tech}), {Events} congestion events, {Shifts} path shifts",
            report.OverallScore, profile.DisplayName, congestionEvents.Count, pathShifts.Count);
        return (report, chartClusters);
    }

    /// <summary>
    /// Per-ASN RTT series for the tab chart (ISP + transit, 24 h, per-minute means)
    /// plus the cached report's events for chart annotations.
    /// </summary>
    public async Task<(List<AsnSeries> Series, IspHealthReport? Report)> GetAsnChartDataAsync(CancellationToken ct = default)
    {
        // Return the exact clusters the report's events were detected on, so chart
        // line labels and the event labels are guaranteed to agree (re-clustering
        // independently would round the "+N ms hop" names differently). Read the
        // snapshot once so the report and its clusters are always the same compute.
        await GetReportAsync(ct: ct);
        var snap = _cached;
        return (snap?.ChartClusters ?? new List<AsnSeries>(), snap?.Report);
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
    /// Builds the per-ASN series used for grading and detection:
    /// - user-added ISP endpoints (e.g. the ISP's own speedtest server) measure the
    ///   access ISP regardless of what ASN their address resolves to, so they fold
    ///   into the canonical ISP ASN discovered from the auto-discovered hops;
    /// - transit targets without a resolved ASN cannot be attributed and are skipped;
    /// - within each ASN, targets cluster by median RTT. Only the nearest cluster
    ///   (the first POP/handoff, within AsnHopClusterToleranceMs) is graded; farther
    ///   clusters still feed the detectors and chart as separately named series so
    ///   monitoring deep hops never inflates the ASN's grade.
    /// </summary>
    private (List<AsnSeries> IspGrading, List<AsnSeries> TransitGrading, List<AsnSeries> AllClusters, List<AsnSeries> ChartClusters) BuildAsnSeriesSets(
        List<MonitoringTarget> ispTargets,
        List<MonitoringTarget> transitTargets,
        Dictionary<string, List<LatencySample>> ispSeries,
        Dictionary<string, List<LatencySample>> transitSeries,
        Dictionary<string, List<string>> ancestorIpsByTargetId)
    {
        var ispOverrides = BuildIspAsnOverrides(ispTargets);
        // Congestion and path-shift detection still runs on clustered series so
        // events fire at the right granularity
        var (_, ispClusters, ispChart) = GroupAndCluster(ispTargets, ispSeries, ispOverrides, gradeLowestTargetOnly: true, ancestorIpsByTargetId);

        // Grade each ISP target individually: every hop's own loss, reach, and
        // congestion contribute to the ISP Network dimension instead of grading only
        // the first clean hop (jitter is graded ISP-wide in the scorer). The access
        // layer idle speed rating still uses FirstHopSeries (unchanged). AsnName carries
        // the ASN org name (not the per-hop target name) so the aggregate ISP card on
        // Networks on Your Path is labeled by the ASN; the per-hop table uses a separate
        // series (ispTargetSeries) that keeps each target's own name.
        var ispGrading = ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId))
            .Select(t =>
            {
                var resolvedAsn = t.AsnNumber ?? 0;
                var asnName = t.AsnName;
                if (ispOverrides != null && ispOverrides.TryGetValue(t.TargetId, out var o))
                {
                    resolvedAsn = o.Asn;
                    asnName ??= o.Name;
                }
                return new AsnSeries
                {
                    AsnNumber = resolvedAsn,
                    AsnName = asnName,
                    TargetIds = { t.TargetId },
                    Samples = ispSeries[t.TargetId],
                    RoleTargetIds = { t.TargetId },
                    HopIps = { t.Address },
                    AncestorIps = ancestorIpsByTargetId.TryGetValue(t.TargetId, out var anc) ? anc : new List<string>()
                };
            })
            .ToList();

        var attributedTransit = transitTargets.Where(t => t.AsnNumber is > 0).ToList();
        var (transitGrading, transitClusters, transitChart) = GroupAndCluster(attributedTransit, transitSeries, null, gradeLowestTargetOnly: false, ancestorIpsByTargetId);

        return (ispGrading, transitGrading,
            ispClusters.Concat(transitClusters).ToList(),
            ispChart.Concat(transitChart).ToList());
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

    private (List<AsnSeries> Grading, List<AsnSeries> AllClusters, List<AsnSeries> ChartClusters) GroupAndCluster(
        List<MonitoringTarget> targets,
        Dictionary<string, List<LatencySample>> seriesByTarget,
        Dictionary<string, (int Asn, string? Name)>? asnOverrides,
        bool gradeLowestTargetOnly,
        Dictionary<string, List<string>> ancestorIpsByTargetId)
    {
        var grading = new List<AsnSeries>();
        var allClusters = new List<AsnSeries>();
        // The chart shows one line per cluster: the nearest cluster stays whole even when
        // only its lowest hop is graded, so a co-located cluster is never split into a
        // graded hop plus an "(other hops)" twin. Detectors and grading keep their own
        // lists, so this affects display only.
        var chartClusters = new List<AsnSeries>();

        var groups = targets
            .Where(t => seriesByTarget.ContainsKey(t.TargetId))
            .GroupBy(t => asnOverrides != null && asnOverrides.TryGetValue(t.TargetId, out var o) ? o.Asn : t.AsnNumber ?? 0);

        foreach (var group in groups)
        {
            var asnName = group.Select(t => t.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                ?? (asnOverrides != null
                    ? group.Select(t => asnOverrides.TryGetValue(t.TargetId, out var o) ? o.Name : null).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                    : null)
                ?? group.Select(t => t.Name).FirstOrDefault();

            var byMedian = group
                .Select(t => (Target: t, Median: SeriesStats.Median(
                    seriesByTarget[t.TargetId].Where(s => s.RttAvgMs.HasValue).Select(s => s.RttAvgMs!.Value).ToList())))
                .Where(x => x.Median.HasValue)
                .OrderBy(x => x.Median!.Value)
                .ToList();
            if (byMedian.Count == 0) continue;

            var clusters = new List<List<(MonitoringTarget Target, double? Median)>>();
            foreach (var entry in byMedian)
            {
                var current = clusters.LastOrDefault();
                if (current == null || entry.Median!.Value - current[0].Median!.Value > _options.AsnHopClusterToleranceMs)
                {
                    current = new List<(MonitoringTarget, double?)>();
                    clusters.Add(current);
                }
                current.Add(entry);
            }

            var firstMin = clusters[0][0].Median!.Value;
            for (var i = 0; i < clusters.Count; i++)
            {
                // Chart line for this cluster: always the WHOLE cluster, one line each.
                // Single member -> its DB name; multi-member nearest -> ASN name; deeper
                // -> distance label. (Unlike the detector list below, the nearest cluster
                // is never peeled into a graded hop plus an "(other hops)" twin.)
                var fullTargets = clusters[i].Select(c => c.Target).ToList();
                chartClusters.Add(new AsnSeries
                {
                    AsnNumber = group.Key,
                    AsnName = fullTargets.Count == 1
                        ? fullTargets[0].Name
                        : i == 0 ? asnName : $"{asnName} (+{clusters[i][0].Median!.Value - firstMin:0} ms hop)",
                    TargetIds = fullTargets.Select(t => t.TargetId).ToList(),
                    Samples = fullTargets.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList()
                });

                var clusterTargets = gradeLowestTargetOnly && i == 0
                    ? new List<MonitoringTarget> { clusters[i][0].Target }
                    : clusters[i].Select(c => c.Target).ToList();

                // A cluster with a single member is labeled by that target's real DB
                // name; multi-member clusters keep the ASN label (nearest) or distance
                // label (deeper hops).
                var chartName = clusterTargets.Count == 1
                    ? clusterTargets[0].Name
                    : i == 0 ? asnName : $"{asnName} (+{clusters[i][0].Median!.Value - firstMin:0} ms hop)";

                allClusters.Add(new AsnSeries
                {
                    AsnNumber = group.Key,
                    AsnName = chartName,
                    TargetIds = clusterTargets.Select(t => t.TargetId).ToList(),
                    Samples = clusterTargets.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList()
                });

                // The graded series keeps the ASN name for the Networks on Your Path card.
                // The card's Mean RTT is the mean across the FULL nearest cluster (for the
                // ISP this is wider than the single graded hop), so every card computes it
                // the same way - the grade still uses Samples (the graded hop/cluster).
                if (i == 0)
                {
                    var nearestRtts = clusters[0]
                        .SelectMany(c => seriesByTarget[c.Target.TargetId])
                        .Where(s => s.RttAvgMs.HasValue)
                        .Select(s => s.RttAvgMs!.Value)
                        .ToList();
                    // Jitter and stability are scored from the farthest cluster when this
                    // ASN spans more than one: a near hop's jitter is often false (ICMP
                    // deprioritization), and the farther cluster - reached through the near
                    // one - is the honest read of the path's jitter. RTT and reach stay on
                    // the nearest cluster. Only for transit (full clusters graded); the ISP
                    // grades each hop on its own, so this carve-out does not apply there.
                    // The assimilation is gated on traceroute hop order: we only trust the
                    // farther cluster's lower jitter when Upstream Discovery recorded it
                    // strictly downstream of the nearest cluster (it actually routes through
                    // it). Without that proof we keep the nearest cluster's own jitter.
                    var jitterSource = new List<LatencySample>();
                    if (!gradeLowestTargetOnly && clusters.Count > 1)
                    {
                        var farthest = clusters[^1].Select(c => c.Target).ToList();
                        if (FarClusterRoutesThroughNear(clusters[0], clusters[^1], ancestorIpsByTargetId))
                        {
                            jitterSource = farthest.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList();
                        }
                    }
                    // This cluster's hop IPs and the union of their ancestors, so the scorer can
                    // confirm this transit routes through a given ISP hop (the hop is an ancestor).
                    var clusterAncestors = clusterTargets
                        .SelectMany(t => ancestorIpsByTargetId.TryGetValue(t.TargetId, out var anc) ? anc : Enumerable.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    grading.Add(new AsnSeries
                    {
                        AsnNumber = group.Key,
                        AsnName = asnName,
                        TargetIds = clusterTargets.Select(t => t.TargetId).ToList(),
                        Samples = clusterTargets.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList(),
                        NearestClusterMeanRttMs = nearestRtts.Count > 0 ? nearestRtts.Average() : null,
                        JitterSourceSamples = jitterSource,
                        HopIps = clusterTargets.Select(t => t.Address).ToList(),
                        AncestorIps = clusterAncestors,
                        // All of this ASN-role's hops, so congestion is attributed to the
                        // right card when the same ASN is both the access ISP and transit
                        RoleTargetIds = group.Select(t => t.TargetId).ToList()
                    });
                }

                // Hops displaced from the graded series stay visible to the detectors
                // (the chart shows them folded into the whole-cluster line above).
                if (gradeLowestTargetOnly && i == 0 && clusters[i].Count > 1)
                {
                    var others = clusters[i].Skip(1).Select(c => c.Target).ToList();
                    allClusters.Add(new AsnSeries
                    {
                        AsnNumber = group.Key,
                        AsnName = others.Count == 1 ? others[0].Name : $"{asnName} (other hops)",
                        TargetIds = others.Select(t => t.TargetId).ToList(),
                        Samples = others.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList()
                    });
                }
            }
        }
        return (grading, allClusters, chartClusters);
    }

    /// <summary>
    /// Confirms a farther RTT cluster is genuinely downstream of the nearer one using the
    /// ancestor sets stored at Upstream Discovery: some nearer-cluster hop must be in the
    /// farther cluster's ancestors, proving the route to the farther cluster passes through
    /// the nearer on a shared trace. Without that proof we decline to assimilate (never
    /// absolve on faith). Uses stored ancestors - no live traceroute is run.
    /// </summary>
    private static bool FarClusterRoutesThroughNear(
        List<(MonitoringTarget Target, double? Median)> nearCluster,
        List<(MonitoringTarget Target, double? Median)> farCluster,
        Dictionary<string, List<string>> ancestorIpsByTargetId)
    {
        var nearIps = nearCluster.Select(c => c.Target.Address)
            .Where(a => !string.IsNullOrEmpty(a)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var farAncestors = farCluster
            .SelectMany(c => ancestorIpsByTargetId.TryGetValue(c.Target.TargetId, out var anc) ? anc : Enumerable.Empty<string>())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return nearIps.Overlaps(farAncestors);
    }

    private static Dictionary<string, List<LatencySample>> ToSamples(
        Dictionary<string, List<MonitoringInfluxClient.LatencySeriesPoint>> raw)
    {
        return raw.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(p => new LatencySample(p.Time, p.RttAvgMs, p.RttMaxMs, p.JitterMs, p.LossPercent)).ToList());
    }
}
