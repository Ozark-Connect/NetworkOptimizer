using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

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

    // The internet endpoints SHOWN on the outage waterfall: just the two canonical anycast
    // resolvers (Cloudflare, Google). 5+ internet rows is just clutter - two well-known resolvers
    // convey "internet reachable" plainly. Detection still triggers on every internet target; this
    // only trims the displayed rows.
    private static readonly string[] OutageInternetIps = ["1.1.1.1", "8.8.8.8"];

    private readonly MonitoringInfluxClient _influx;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly SiteDbContextFactory _siteDbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly PhysicalLinkResolver _physicalLinkResolver;
    private readonly ILogger<IspHealthService> _logger;
    private readonly string _siteSlug;
    private readonly bool _isDefault;
    private readonly IspHealthOptions _options = new();
    private const int MaxCustomWindowHours = 720;  // 30-day cap on the date/time filter, matching the UI
    private readonly SemaphoreSlim _computeLock = new(1, 1);

    // Report and its chart clusters are published together as one immutable snapshot so a
    // reader can never pair a fresh cluster set with a stale report (the chart's
    // "+N ms hop" line labels must match the report's event labels). Single-reference
    // assignment makes the swap atomic; readers take one local copy.
    private sealed record Snapshot(IspHealthReport Report, List<AsnSeries> ChartClusters);
    // Result of one core compute, before it is published (or not) to instance state.
    private sealed record ComputeOutcome(IspHealthStatus Status, IspHealthReport? Report, List<AsnSeries> ChartClusters);
    // Most-recent custom-window result, so the chart's follow-up fetch for the same window
    // reuses it instead of re-running the heavy query. Never read by the canonical 48 h paths.
    private sealed record CustomWindowSnapshot(DateTime Start, DateTime End, IspHealthReport Report, List<AsnSeries> ChartClusters, DateTime ComputedAt);
    private Snapshot? _cached;
    private CustomWindowSnapshot? _customCache;
    private IspHealthStatus _status = IspHealthStatus.Computing;
    private volatile bool _computing;
    // Trailing window (hours) the last successful auto-compute used. Drops down the ladder when a
    // longer window exceeds the compute budget on this hardware; resets to 0 on process restart so the
    // configured target is re-probed once after each deploy. 0 until the first auto-compute runs.
    private volatile int _effectiveWindowHours;
    // Configured target window (hours), cached from MonitoringSettings on each auto-compute so the
    // dashboard tile and tab can read it without a DB hit. 0 until the first auto-compute runs.
    private volatile int _configuredWindowHours;

    public IspHealthService(
        MonitoringInfluxRegistry influxRegistry,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        SiteConnectionRegistry siteConnections,
        PhysicalLinkResolver physicalLinkResolver,
        ILogger<IspHealthService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _influx = influxRegistry.GetFor(_siteSlug);
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _connectionService = siteConnections.GetFor(_siteSlug);
        _physicalLinkResolver = physicalLinkResolver;
        _logger = logger;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private async Task<NetworkOptimizerDbContext> CreateSiteDbAsync(CancellationToken ct)
    {
        if (!_isDefault)
            return _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);
        return await _dbFactory.CreateDbContextAsync(ct);
    }

    /// <summary>
    /// Persists the user's chosen physical-link source (used when more than one monitored
    /// device matches the WAN's access technology) and forces a recompute so the Physical
    /// Link factor reflects the pick. Pass null to clear the selection.
    /// </summary>
    public async Task SetPhysicalLinkSourceAsync(string? sourceKey, CancellationToken ct = default)
    {
        await using (var db = await CreateSiteDbAsync(ct))
        {
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings == null) return;
            settings.PhysicalLinkSourceKey = string.IsNullOrWhiteSpace(sourceKey) ? null : sourceKey;
            settings.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await GetReportAsync(forceRefresh: true, ct);
    }

    /// <summary>
    /// Overrides the scored access technology from the ISP Health selector and recomputes. Writes
    /// it the same way Upstream Discovery commits it: to the primary WAN's discovery context,
    /// created if missing. That is the row the scorer reads and the one a later discovery run
    /// preserves (it only proposes a technology when none is set), so the override sticks until the
    /// user changes it again here or in the discovery review. The legacy
    /// MonitoringSettings.AccessTechnology is intentionally left untouched - it is read only as a
    /// fallback for installs that predate the per-WAN context.
    /// </summary>
    public async Task SetAccessTechnologyAsync(AccessTechnology technology, CancellationToken ct = default)
    {
        await using (var db = await CreateSiteDbAsync(ct))
        {
            // Primary WAN context, wan-first like the reader's ordering - but NOT filtered to
            // non-Unknown: setting it when it is currently unset is the whole point. Create it if
            // the table is empty, matching Upstream Discovery's create-if-missing on commit.
            var ctxRow = (await db.WanDiscoveryContexts.ToListAsync(ct))
                .OrderBy(c => string.Equals(c.WanInterface, "wan", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (ctxRow == null)
            {
                ctxRow = new WanDiscoveryContext { WanInterface = "wan" };
                db.WanDiscoveryContexts.Add(ctxRow);
            }
            ctxRow.AccessTechnology = technology;
            ctxRow.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        await GetReportAsync(forceRefresh: true, ct);
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
            if (report != null)
                _cached = new Snapshot(report, chartClusters);
            else if (forceRefresh)
                // A forced recompute that lost readiness (e.g. the technology was unset) must drop
                // the stale snapshot, otherwise Status keeps reporting Ready off the old cache and
                // the panel shows a generic error instead of the right prerequisite funnel.
                _cached = null;
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

    /// <summary>The trailing window (hours) the auto-computed score and default view currently use.
    /// Falls below the configured target on slower hardware that can't finish the longer window inside
    /// the compute budget. 0 until the first auto-compute completes.</summary>
    public int EffectiveWindowHours => _effectiveWindowHours;

    /// <summary>The configured target window (hours) the auto-compute aims for (per-site setting, or
    /// the built-in default). 0 until the first auto-compute runs.</summary>
    public int ConfiguredWindowHours => _configuredWindowHours;

    /// <summary>
    /// Re-probe the configured target window on the next auto-compute. Wired to the "reduced window"
    /// badge so a user who thinks the hardware can now handle the full window (e.g. after an upgrade)
    /// can ask for it: resets the fallback to start the ladder at the target again, and drops the
    /// cached shorter report so the next read recomputes.
    /// </summary>
    public void RetryConfiguredWindow()
    {
        _effectiveWindowHours = 0;
        _cached = null;
    }

    private async Task<(IspHealthReport? Report, List<AsnSeries> ChartClusters)> ComputeAsync(CancellationToken ct)
    {
        // Canonical/auto-computed path. It targets the configured window (default 48 h) but drops down
        // ScoreWindowLadderHours whenever a window's compute exceeds ComputeBudget, so on slower NAS
        // hardware the default view and dashboard score fall back to a window the box can actually
        // finish (24 h, then 16 h) instead of hanging past the HTTP timeout. Publishes the readiness
        // status the dashboard tile reads.
        var ceiling = await ResolveConfiguredWindowHoursAsync(ct);
        _configuredWindowHours = ceiling;
        var budget = ResolveComputeBudget();

        // Always attempt the configured target first, then the standard rungs strictly below it, so a
        // ceiling that isn't itself a ScoreWindowLadderHours value (e.g. 36 h) still tries the target
        // before falling back rather than jumping straight to the nearest shorter rung.
        var ladder = new[] { ceiling }
            .Concat(_options.ScoreWindowLadderHours.Where(h => h < ceiling))
            .Where(h => h >= _options.MinDataHours)
            .Distinct()
            .OrderByDescending(h => h)
            .ToList();
        if (ladder.Count == 0) ladder.Add(Math.Max(ceiling, _options.MinDataHours));

        // Resume at the current effective rung so a box that already fell back doesn't re-attempt the
        // too-slow longer windows on every refresh. A process restart resets _effectiveWindowHours, so
        // the ceiling is re-probed once on the first compute after each deploy.
        var startIdx = 0;
        if (_effectiveWindowHours > 0)
        {
            var resume = ladder.FindIndex(h => h <= _effectiveWindowHours);
            startIdx = resume < 0 ? 0 : resume;
        }

        for (var i = startIdx; i < ladder.Count; i++)
        {
            var hours = ladder[i];
            var windowEnd = DateTime.UtcNow;
            var windowStart = windowEnd.AddHours(-hours);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budgetCts.CancelAfter(budget);
            try
            {
                var outcome = await ComputeCoreAsync(windowStart, windowEnd, null, budgetCts.Token);
                _effectiveWindowHours = hours;
                _status = outcome.Status;
                _logger.LogDebug("ISP Health auto-compute at {Hours}h completed in {Ms}ms (status {Status})",
                    hours, sw.ElapsedMilliseconds, outcome.Status);
                if (hours < ceiling)
                    _logger.LogInformation(
                        "ISP Health auto-compute using a {Hours}h window (target {Ceiling}h): the longer window exceeded the {Budget}s time budget on this hardware",
                        hours, ceiling, (int)budget.TotalSeconds);
                return (outcome.Report, outcome.ChartClusters);
            }
            catch (OperationCanceledException) when (budgetCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                var next = i + 1 < ladder.Count ? ladder[i + 1] : hours;
                _effectiveWindowHours = next;
                _logger.LogInformation(
                    "ISP Health {Hours}h auto-compute exceeded the {Budget}s time budget after {Ms}ms; falling back to {Next}h",
                    hours, (int)budget.TotalSeconds, sw.ElapsedMilliseconds, next);
            }
        }

        // Every rung exceeded the budget: leave the funnel status so the tile/tab report progress and
        // the next cycle retries. Don't publish a stale report.
        _logger.LogWarning(
            "ISP Health auto-compute could not finish any window ({Ladder}h) within the {Budget}s budget",
            string.Join("/", ladder), (int)budget.TotalSeconds);
        _status = IspHealthStatus.Computing;
        return (null, new List<AsnSeries>());
    }

    /// <summary>
    /// Configured target window (hours) from MonitoringSettings, floored at MinDataHours and defaulting
    /// to the built-in ScoreWindowHours when unset or unreadable.
    /// </summary>
    private async Task<int> ResolveConfiguredWindowHoursAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            var hours = settings?.IspHealthScoreWindowHours ?? _options.ScoreWindowHours;
            return hours >= _options.MinDataHours ? hours : _options.ScoreWindowHours;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not read the configured score window; using default {Default}h", _options.ScoreWindowHours);
            return _options.ScoreWindowHours;
        }
    }

    /// <summary>
    /// Per-attempt compute budget: the ISP_HEALTH_COMPUTE_BUDGET_SECONDS env var when set (used to
    /// force the window fallback on fast hardware for testing, or to tune for a specific box), else the
    /// built-in <see cref="IspHealthOptions.ComputeBudget"/> default.
    /// </summary>
    private TimeSpan ResolveComputeBudget()
    {
        var raw = Environment.GetEnvironmentVariable("ISP_HEALTH_COMPUTE_BUDGET_SECONDS");
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            return TimeSpan.FromSeconds(seconds);
        return _options.ComputeBudget;
    }

    /// <summary>
    /// Report for the ISP Health tab's date/time filter over an arbitrary window. Never touches
    /// the cached 48 h report, the readiness status, or the dashboard tile - the default and
    /// auto-computed paths stay on the trailing 48 h window. The most recent custom window is
    /// briefly cached so the chart's follow-up fetch for the same window skips the heavy query.
    /// </summary>
    public async Task<(IspHealthReport? Report, List<AsnSeries> ChartClusters)> ComputeForWindowAsync(
        DateTime windowStart, DateTime windowEnd, bool forceRefresh = false, CancellationToken ct = default)
    {
        // Enforce the filter's window bounds on the real data path. The UI clamps too, but this is the
        // single chokepoint every custom-window caller (report and chart endpoint) funnels through, so a
        // sub-minimum (or over-max) request can't slip past into an empty result. Pin the end and expand
        // the start back, exactly as the UI does; min ties to the scoring floor, max is the 30-day cap.
        var minSpan = TimeSpan.FromHours(_options.MinDataHours);
        var maxSpan = TimeSpan.FromHours(MaxCustomWindowHours);
        if (windowEnd - windowStart < minSpan) windowStart = windowEnd - minSpan;
        else if (windowEnd - windowStart > maxSpan) windowStart = windowEnd - maxSpan;

        var cached = _customCache;
        if (!forceRefresh && cached != null && cached.Start == windowStart && cached.End == windowEnd
            && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
            return (cached.Report, cached.ChartClusters);

        // A window longer than the canonical re-covers the recent period at a coarser aggregate,
        // which can inflate bucket-p90 burst detection into congestion events the authoritative
        // fine-resolution 48 h view never sees. Gate the recent (canonical-covered) portion against
        // the canonical report so those artifacts drop, while older history keeps its own detection.
        // Only when the canonical computed successfully (non-null); otherwise no gating (never drop
        // events against a missing reference). GetReportAsync is cache-served, so this is cheap.
        IReadOnlyList<CongestionEvent>? referenceEvents = null;
        if ((windowEnd - windowStart).TotalHours > _options.ScoreWindowHours + 0.5
            && DateTime.UtcNow - windowEnd < TimeSpan.FromHours(1))
        {
            referenceEvents = (await GetReportAsync(ct: ct))?.CongestionEvents;
        }

        var outcome = await ComputeCoreAsync(windowStart, windowEnd, referenceEvents, ct);
        if (outcome.Report != null)
            _customCache = new CustomWindowSnapshot(windowStart, windowEnd, outcome.Report, outcome.ChartClusters, DateTime.UtcNow);
        return (outcome.Report, outcome.ChartClusters);
    }

    /// <summary>
    /// Drops congestion events in the canonical-covered recent window (the trailing
    /// <see cref="IspHealthOptions.ScoreWindowHours"/>) that the fine-resolution canonical report
    /// did not also find - coarse-aggregate burst artifacts a long viewing window invents. An event
    /// older than that window has no canonical counterpart to check against, so it is kept. A match
    /// is a time overlap plus a shared bottleneck hop or ASN (loose, so a real event the canonical
    /// localized to a slightly different hop is never dropped).
    /// </summary>
    private List<CongestionEvent> GateAgainstCanonical(
        List<CongestionEvent> events, IReadOnlyList<CongestionEvent> canonical, DateTime windowEnd)
    {
        var recentStart = windowEnd.AddHours(-_options.ScoreWindowHours);
        return events.Where(e =>
            e.Start < recentStart
            || canonical.Any(r =>
                r.Start < e.End && e.Start < r.End
                && ((e.BottleneckHopIp != null && r.BottleneckHopIp == e.BottleneckHopIp)
                    || r.AsnNumbers.Any(a => a != 0 && e.AsnNumbers.Contains(a)))))
            .ToList();
    }

    private async Task<ComputeOutcome> ComputeCoreAsync(DateTime windowStart, DateTime windowEnd,
        IReadOnlyList<CongestionEvent>? referenceEvents, CancellationToken ct)
    {
        if (!_influx.IsConfigured && !await _influx.ReconfigureAsync(ct))
            return new ComputeOutcome(IspHealthStatus.NotConfigured, null, new List<AsnSeries>());

        AccessTechnology technology;
        List<MonitoringTarget> targets;
        // Enabled fabric (UniFi device) targets, used only to find the LAN gateway's monitoring
        // target for outage scoping (gateway-unreachable => LAN/gateway outage, not WAN).
        List<MonitoringTarget> fabricTargets;
        // TargetId -> the monitored hop IPs proven upstream of it (its ancestors), from
        // Upstream Discovery's traces. ISP Health uses these to confirm one hop routes
        // through another before its jitter absolves the other. No live traceroute here.
        Dictionary<string, List<string>> ancestorIpsByTargetId;
        // TargetId -> persisted hop distance (lowest TTL seen across traces). The canonical
        // nearest-first ordering for the outage shape; absent for targets never traced
        // (the trace map landed post-launch), where the caller falls back to RTT.
        Dictionary<string, int> hopNumberByTargetId;
        bool hopOrderKnown;
        await using (var db = await CreateSiteDbAsync(ct))
        {
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.Enabled)
                return new ComputeOutcome(IspHealthStatus.NotConfigured, null, new List<AsnSeries>());

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

            fabricTargets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && t.TargetType == MonitoringTargetType.Fabric && t.DeviceMac != null)
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
            // Canonical hop distance per target (lowest TTL across traces) for outage ordering.
            hopNumberByTargetId = discoveries
                .Where(d => targetIdById.ContainsKey(d.MonitoringTargetId!.Value))
                .GroupBy(d => targetIdById[d.MonitoringTargetId!.Value])
                .ToDictionary(g => g.Key, g => g.Min(d => d.HopNumber));
        }

        var ispTargets = targets.Where(t => t.TargetType == MonitoringTargetType.AccessIsp).ToList();
        // WoodyNet / PCH (AS42, AS715) and similar IXP/anycast-DNS infrastructure are not
        // transit; drop them so they never enter scoring, the per-ASN cards, or the chart clusters.
        var transitTargets = targets.Where(t => t.TargetType == MonitoringTargetType.Transit
            && !(t.AsnNumber is int a && WellKnownAsns.NonTransitInfrastructure.Contains(a))).ToList();
        if (ispTargets.Count == 0 && transitTargets.Count == 0)
            return new ComputeOutcome(IspHealthStatus.NeedsDiscovery, null, new List<AsnSeries>());

        var profile = IspHealthProfiles.GetProfile(technology);
        if (profile == null)
            return new ComputeOutcome(IspHealthStatus.NeedsTechnology, null, new List<AsnSeries>());

        // First gateway from the cached UniFi device list (shadow-mode multi-gateway isn't handled
        // yet - first gateway is fine), matched to its fabric monitoring target by MAC so we can pull
        // its loss for outage scoping. Null when no gateway is monitored - outage scoping then stays
        // unchanged (no Local scope possible).
        static string MacKey(string? m) => new string((m ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        var gatewayDevice = (await _connectionService.GetDiscoveredDevicesAsync(ct)).FirstOrDefault(d => d.Type.IsGateway());
        var gatewayTarget = gatewayDevice == null ? null
            : fabricTargets.FirstOrDefault(t => MacKey(t.DeviceMac) == MacKey(gatewayDevice.Mac));

        // Fine-grained join window so short load bursts (speed tests, downloads) classify as
        // loaded instead of diluting into minute-level means. Longer (filter-selected) windows
        // coarsen it to keep the point count bounded; the canonical 48 h window lands on exactly
        // LoadWindowSeconds, so the auto-computed report is unchanged.
        var aggregate = TimeSpan.FromSeconds(Math.Max(
            _options.LoadWindowSeconds, (windowEnd - windowStart).TotalSeconds / 25000.0));

        // All target types read at the fine window. Coarsening transit/internet RTT bought a modest
        // 48h deserialize win but shifted congestion localization (the bottleneck walk keys on RTT
        // bursts, which a coarse mean blunts), so it diverged from the fine-resolution attribution on
        // transit-heavy paths. Kept fine everywhere; the compute-time wins now come from the in-memory
        // detector/scorer paths, not from coarsening the input.
        var ispSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.AccessIsp, windowStart, windowEnd, aggregate, ct);
        var transitSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.Transit, windowStart, windowEnd, aggregate, ct);
        var internetSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.InternetService, windowStart, windowEnd, aggregate, ct);
        var ratesTask = QueryWanRatesAsync(windowStart, windowEnd, aggregate, ct);
        var speedsTask = ResolveExpectedSpeedsAsync(ct);
        var speedTestsTask = LoadWanSpeedTestsAsync(windowStart, windowEnd, ct);
        var gatewaySeriesTask = gatewayTarget == null
            ? Task.FromResult(new List<MonitoringInfluxClient.LatencySeriesPoint>())
            : _influx.QueryLatencyDetailByTargetIdAsync(gatewayTarget.TargetId, windowStart, windowEnd, aggregate, ct);
        await Task.WhenAll(ispSeriesTask, transitSeriesTask, internetSeriesTask, ratesTask, speedsTask, speedTestsTask, gatewaySeriesTask);

        var ispSeries = ToSamples(await ispSeriesTask);
        var transitSeries = ToSamples(await transitSeriesTask);
        var internetSeries = ToSamples(await internetSeriesTask);
        var wanRates = await ratesTask;
        var (expectedDown, expectedUp, expectedSource, smartQueuesEnabled) = await speedsTask;
        var wanSpeedTests = await speedTestsTask;
        var gatewaySamples = (await gatewaySeriesTask)
            .Select(p => new LatencySample(p.Time, p.RttAvgMs, p.RttMaxMs, p.JitterMs, p.LossPercent)).ToList();

        // New installs: grade once a few hours of latency data exist, not before.
        // Enabled targets only - a disabled target's stale history must not satisfy the
        // gate when no enabled target has enough data yet.
        var earliestSample = ispTargets.Where(t => ispSeries.ContainsKey(t.TargetId)).Select(t => ispSeries[t.TargetId])
            .Concat(transitTargets.Where(t => transitSeries.ContainsKey(t.TargetId)).Select(t => transitSeries[t.TargetId]))
            .Where(s => s.Count > 0)
            .Select(s => s[0].Time)
            .DefaultIfEmpty(windowEnd)
            .Min();
        // New-install / sparse-window guard: too little data to score. Fires only when the data is
        // both shorter than MinDataHours AND does not reach near the window start - a fresh install (or
        // a window predating collection) has its earliest sample well inside the window. An established
        // site's earliest sample sits at the window edge, so a small custom window clamped to the
        // minimum still scores instead of tripping this on the first poll gap.
        if ((windowEnd - earliestSample).TotalHours < _options.MinDataHours
            && earliestSample > windowStart.AddMinutes(15))
            return new ComputeOutcome(IspHealthStatus.InsufficientData, null, new List<AsnSeries>());

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

        var (ispGrading, transitGrading, allClusters, ispChart, transitChart) = BuildAsnSeriesSets(ispTargets, transitTargets, ispSeries, transitSeries, ancestorIpsByTargetId);
        var chartClusters = ispChart.Concat(transitChart).ToList();
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
                AncestorIps = ancestorIpsByTargetId.TryGetValue(t.TargetId, out var destAnc) ? destAnc : new List<string>(),
                // Internet/CDN endpoint (by TargetType): path-shift correlation prefers an
                // on-path ISP/transit hop over these as the event label.
                IsDestination = true
            })
            .ToList();

        // Surface the well-known anycast DNS endpoints (Cloudflare 1.1.1.1, Google 8.8.8.8)
        // as their own lines on the Per-Network RTT chart. They already feed loss, path-shift,
        // and congestion detection (as destination witnesses); they are also exceptionally
        // stable, so plotting them gives a known-good baseline to read a noisy ISP or transit
        // hop against. Only the anycast DNS targets are charted, not arbitrary discovered
        // InternetService destinations.
        chartClusters.AddRange(internetTargetSeries
            .Where(s => s.HopIps.Any(AnycastDnsIps.Contains)));

        // Per-target (hop-granularity) series for the congestion localizer: clustering would
        // lump a clean middle hop with a hot one and re-merge an off-path ASN, so detection and
        // localization run at the individual hop. Destinations come in as witnesses only.
        AsnSeries PerTargetSeries(MonitoringTarget t, Dictionary<string, List<LatencySample>> series, bool isDestination) => new()
        {
            AsnNumber = t.AsnNumber ?? 0,
            AsnName = t.Name,
            TargetIds = { t.TargetId },
            Samples = series[t.TargetId],
            HopIps = { t.Address },
            AncestorIps = ancestorIpsByTargetId.TryGetValue(t.TargetId, out var anc) ? anc : new List<string>(),
            IsDestination = isDestination
        };
        var localizerSeries = new List<AsnSeries>();
        localizerSeries.AddRange(ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId))
            .Select(t => PerTargetSeries(t, ispSeries, false)));
        localizerSeries.AddRange(transitTargets
            .Where(t => t.AsnNumber is > 0 && transitSeries.ContainsKey(t.TargetId))
            .Select(t => PerTargetSeries(t, transitSeries, false)));
        localizerSeries.AddRange(internetTargetSeries);

        // Hop distance per IP (from the saved trace map), the nearest public access hop(s),
        // and WAN utilization over time - the localizer's topology and load context.
        var hopNumberByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in targets)
        {
            if (string.IsNullOrEmpty(t.Address) || !hopNumberByTargetId.TryGetValue(t.TargetId, out var hop)) continue;
            if (!hopNumberByIp.TryGetValue(t.Address, out var existing) || hop < existing)
                hopNumberByIp[t.Address] = hop;
        }
        var accessEgressIps = ispTargets
            .Where(t => !string.IsNullOrEmpty(t.Address) && !NetworkUtilities.IsPrivateIpAddress(t.Address)
                && hopNumberByTargetId.ContainsKey(t.TargetId))
            .GroupBy(t => hopNumberByTargetId[t.TargetId])
            .OrderBy(g => g.Key)
            .FirstOrDefault()?.Select(t => t.Address)
            ?? Enumerable.Empty<string>();
        var loadByTime = wanRates.Select(r =>
        {
            double? util = null;
            if (expectedDown is > 0 || expectedUp is > 0)
            {
                var d = expectedDown is > 0 && r.DownloadBps.HasValue ? r.DownloadBps.Value / (expectedDown.Value * 1_000_000) : 0;
                var u = expectedUp is > 0 && r.UploadBps.HasValue ? r.UploadBps.Value / (expectedUp.Value * 1_000_000) : 0;
                util = Math.Max(d, u);
            }
            return (r.Time, Utilization: util);
        }).ToList();
        var congestionTopology = new CongestionTopology
        {
            AccessEgressHopIps = new HashSet<string>(accessEgressIps, StringComparer.OrdinalIgnoreCase),
            HopNumberByIp = hopNumberByIp,
            Load = loadByTime,
            HasTraceMap = hopOrderKnown
        };
        // Compute-budget checkpoints between the heavy in-memory phases: the Influx reads above honor
        // the token, but the detectors/scorer are CPU loops, so a deadline that fires mid-compute is
        // caught at the next phase boundary and abandons the attempt (the auto path then drops a rung).
        ct.ThrowIfCancellationRequested();
        var congestionEvents = CongestionLocalizer.Localize(localizerSeries, congestionTopology, _options);
        if (referenceEvents != null)
            congestionEvents = GateAgainstCanonical(congestionEvents, referenceEvents, windowEnd);
        // On a long (coarse-aggregate) window, snap congestion boundaries back to fine resolution so
        // a marginal event's 15-min bucket edges don't land off where the canonical view would place
        // them. No-op at canonical resolution; reads run concurrently (see method).
        await RefineCongestionBoundariesAsync(congestionEvents, aggregate, ct);
        foreach (var ce in congestionEvents)
            _logger.LogDebug(
                "ISP Health congestion: {Disposition} at {Hop} ({Label}) conf={Confidence} load={Load} - {Reason}",
                ce.Disposition, ce.BottleneckHopIp ?? "?",
                ce.BottleneckLabel ?? string.Join(",", ce.AsnNames), ce.Confidence, ce.LoadCoincident, ce.AttributionReason);

        // Internet/CDN targets join step detection because routing shifts in a transit
        // network show up on every path that crosses it (per the real shift examples)
        var stepInput = allClusters.Concat(internetTargetSeries).ToList();
        var pathShifts = StepChangeDetector.Detect(stepInput, _options);

        // Outage detection: the internet targets going dark defines an outage; every hop is
        // carried (ordered nearest-first by the hop map, RTT tiebreaker) to shape it and
        // attribute the break. A monitoring gap has no samples and so is never flagged. The
        // trigger keeps ALL internet targets (robust detection); only the waterfall's internet
        // rows are trimmed to the two canonical resolvers below.
        var internetTriggerTargets = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeries.ContainsKey(t.TargetId))
            .Select(t => (IReadOnlyList<LatencySample>)internetSeries[t.TargetId])
            .ToList();
        int ClusterHopNumber(AsnSeries s) => s.TargetIds
            .Select(tid => hopNumberByTargetId.TryGetValue(tid, out var hn) ? hn : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue).Min();
        double MedianRtt(AsnSeries s) => SeriesStats.Median(
            s.Samples.Where(x => x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList()) ?? double.MaxValue;
        // The two internet rows for the waterfall: prefer Cloudflare/Google, but if the user
        // doesn't monitor them, fall back to the two nearest other internet targets so the
        // waterfall still shows an internet-reachability row.
        var displayInternet = internetTargetSeries
            .Where(s => s.HopIps.Any(ip => OutageInternetIps.Contains(ip)))
            .Concat(internetTargetSeries
                .Where(s => !s.HopIps.Any(ip => OutageInternetIps.Contains(ip)))
                .OrderBy(MedianRtt))
            .Take(2)
            .ToList();
        // Each waterfall row is labeled by its ASN. Access ISP and Transit can both be the same
        // ASN (e.g. AT&T is both the access network and a transit hop), so a transit row whose ASN
        // also appears in the access layer is suffixed " Transit" to disambiguate it.
        var accessAsnNumbers = ispTargets.Where(t => t.AsnNumber is > 0).Select(t => t.AsnNumber!.Value).ToHashSet();
        var accessAsnName = ispTargets.Select(t => AsnNameCleanup.Clean(t.AsnName)).FirstOrDefault(n => !string.IsNullOrEmpty(n));
        var transitAsnNameByNumber = transitTargets
            .Where(t => t.AsnNumber is > 0 && !string.IsNullOrEmpty(t.AsnName))
            .GroupBy(t => t.AsnNumber!.Value)
            .ToDictionary(g => g.Key, g => AsnNameCleanup.Clean(g.Select(t => t.AsnName).First()) ?? "");
        string TransitLabel(AsnSeries s)
        {
            // Single-member transit clusters carry the target's own name; prefer the ASN. Multi /
            // deeper clusters already carry an ASN-based name ("ASN (+N ms hop)"), so keep it.
            var asn = transitAsnNameByNumber.GetValueOrDefault(s.AsnNumber);
            var label = s.TargetIds.Count == 1 && !string.IsNullOrEmpty(asn) ? asn : AsnNameCleanup.Clean(s.AsnName) ?? asn ?? "transit";
            if (accessAsnNumbers.Contains(s.AsnNumber) && !label.EndsWith("Transit", StringComparison.OrdinalIgnoreCase))
                label += " Transit";
            return label;
        }
        // Waterfall composition:
        //  - Access ISP targets broken out per target (Groupable, labeled by access ASN) so each
        //    access hop's own outage timing shows; the detector re-collapses shared signatures.
        //  - Transit kept as the per-ASN RTT clusters (the Per-Network RTT grouping), untouched.
        //  - Internet trimmed to two rows (displayInternet).
        var outageSources = ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId))
            .Select(t => (Series: new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = ispSeries[t.TargetId],
                HopIps = { t.Address }
            }, Groupable: true, AsnLabel: AsnNameCleanup.Clean(t.AsnName) ?? accessAsnName))
            .Concat(transitChart.Select(s => (Series: s, Groupable: false, AsnLabel: (string?)TransitLabel(s))))
            .Concat(displayInternet.Select(s => (Series: s, Groupable: false, AsnLabel: (string?)null)));
        var orderedWanHops = outageSources
            .Select(x => new
            {
                x.Groupable,
                x.AsnLabel,
                Name = x.Series.AsnName ?? x.Series.TargetIds.FirstOrDefault() ?? "hop",
                Series = (IReadOnlyList<LatencySample>)x.Series.Samples,
                HopNumber = ClusterHopNumber(x.Series),
                Rtt = MedianRtt(x.Series)
            })
            .OrderBy(x => x.HopNumber).ThenBy(x => x.Rtt)
            .ToList();
        // The LAN gateway is the nearest hop (Depth 0) when monitored; WAN hops shift one deeper.
        // Its loss lets the detector tell a LAN/gateway outage from a WAN outage. Absent => unchanged.
        var gatewayHop = gatewaySamples.Count > 0
            ? new OutageDetector.Hop(gatewayDevice?.Name is { Length: > 0 } gn ? gn : "Gateway",
                0, gatewaySamples, Groupable: false, AsnLabel: null, IsGateway: true)
            : null;
        var baseDepth = gatewayHop != null ? 1 : 0;
        var outageHops = (gatewayHop != null ? new[] { gatewayHop } : Array.Empty<OutageDetector.Hop>())
            .Concat(orderedWanHops.Select((x, i) =>
                new OutageDetector.Hop(x.Name, baseDepth + i, x.Series, x.Groupable, x.AsnLabel)))
            .ToList();
        ct.ThrowIfCancellationRequested();
        var outages = OutageDetector.Detect(internetTriggerTargets, outageHops, _options);
        // Second pass: coincident partial-loss disruptions (the path getting lossy but not dark)
        // across the full set of monitored hops, excluding windows already flagged as blackouts.
        var partialDisruptions = OutageDetector.DetectPartial(
            outageHops, outages.Select(o => (o.Start, o.End)).ToList(), _options);
        outages = outages.Concat(partialDisruptions).OrderBy(o => o.Start).ToList();

        // Weight each outage by the time-of-day usage fingerprint so a drop during heavy-usage hours
        // counts in full and one during typically-idle hours dings less. Null fingerprint (weighting
        // off, or too few days of data) leaves every UsageWeight at 1.0 - no grade-down.
        var usageFingerprint = await BuildUsageFingerprintAsync(windowEnd, ct);
        if (usageFingerprint != null)
        {
            var usageZone = TimeZoneInfo.Local;
            foreach (var o in outages)
                o.UsageWeight = UsageWeighting.Weight(
                    usageFingerprint, UsageWeighting.LocalHoursSpanned(o.Start, o.End, usageZone), _options.UsageWeightFloor);
        }

        // chartClusters (one line per cluster) is the chart view computed from the same
        // snapshot the detectors ran on, so deeper-cluster "+N ms hop" labels still match
        // event labels. It is published together with the report (see Snapshot).
        var primaryWanInterface = await GetPrimaryWanInterfaceAsync(ct);
        var loadExclusions = await BuildSqmProbeExclusionsAsync(windowStart, windowEnd, primaryWanInterface, ct);
        var adaptiveSqmEnabled = await IsAdaptiveSqmEnabledAsync(primaryWanInterface, ct);

        // Match the WAN's access technology to one monitored physical device (ONT/SFP, cable
        // modem, or cellular modem) and aggregate its window metrics for the Physical Link factor.
        var physical = await _physicalLinkResolver.ResolveAsync(technology, windowStart, windowEnd, aggregate, ct);

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
            Outages = outages,
            SmartQueuesEnabled = smartQueuesEnabled,
            AdaptiveSqmEnabled = adaptiveSqmEnabled,
            HopOrderKnown = hopOrderKnown,
            LoadExclusionWindows = loadExclusions,
            PhysicalLink = physical.Input
        };

        ct.ThrowIfCancellationRequested();
        var report = new IspHealthScorer(_options, _logger).Score(inputs, profile);
        report.AccessTechnology = technology;
        report.PhysicalLinkCandidates = physical.Candidates;
        report.PhysicalLinkSelectedKey = physical.SelectedKey;
        report.PhysicalLinkMedium = physical.Input?.Medium;
        report.PhysicalLinkAmbiguous = physical.Ambiguous;
        _logger.LogDebug("ISP Health computed: {Score} ({Tech}), {Events} congestion events, {Shifts} path shifts",
            report.OverallScore, profile.DisplayName, congestionEvents.Count, pathShifts.Count);
        return new ComputeOutcome(IspHealthStatus.Ready, report, chartClusters);
    }

    /// <summary>
    /// Per-ASN RTT series for the tab chart (ISP + transit) plus the report's events for chart
    /// annotations. With no window it serves the cached 48 h report; with an explicit window
    /// (the tab's date/time filter) it computes that window off-cache, so the chart follows the
    /// filter without disturbing the canonical 48 h view.
    /// </summary>
    public async Task<(List<AsnSeries> Series, IspHealthReport? Report)> GetAsnChartDataAsync(
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        if (from.HasValue && to.HasValue)
        {
            var (windowReport, windowClusters) = await ComputeForWindowAsync(from.Value, to.Value, ct: ct);
            return (windowClusters, windowReport);
        }
        // Return the exact clusters the report's events were detected on, so chart
        // line labels and the event labels are guaranteed to agree (re-clustering
        // independently would round the "+N ms hop" names differently). Read the
        // snapshot once so the report and its clusters are always the same compute.
        await GetReportAsync(ct: ct);
        var snap = _cached;
        return (snap?.ChartClusters ?? new List<AsnSeries>(), snap?.Report);
    }

    /// <summary>
    /// Report for an explicit window (the ISP Health tab's date/time filter). Bypasses the 48 h
    /// cache and never publishes status, so the dashboard tile and default view stay on 48 h.
    /// </summary>
    public async Task<IspHealthReport?> GetReportForWindowAsync(DateTime windowStart, DateTime windowEnd, bool forceRefresh = false, CancellationToken ct = default)
    {
        var (report, _) = await ComputeForWindowAsync(windowStart, windowEnd, forceRefresh, ct);
        return report;
    }

    private async Task<List<ThroughputSample>> QueryWanRatesAsync(DateTime from, DateTime to, TimeSpan aggregate, CancellationToken ct)
    {
        try
        {
            var (mac, ifNames) = await ResolveWanCounterAsync(ct);
            if (mac == null || ifNames == null || ifNames.Count == 0)
                return new List<ThroughputSample>();

            var rates = await _influx.QueryGatewayWanRatesAsync(mac, ifNames, from, to, aggregate, ct);
            return rates.Select(r => new ThroughputSample(r.Time, r.DownloadBps, r.UploadBps)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not query WAN rates");
            return new List<ThroughputSample>();
        }
    }

    /// <summary>
    /// Resolves the gateway MAC and the CONFIGURED primary WAN's SNMP counter interface(s) - the same
    /// WAN as the expected speeds and SQM exclusion (e.g. "eth6" for a VLAN-tagged primary), not the
    /// live active uplink. Falls back to the active uplink only if the config-primary can't be
    /// resolved, so analysis still runs. Returns (null, null) when no gateway is discovered.
    /// </summary>
    private async Task<(string? Mac, List<string>? IfNames)> ResolveWanCounterAsync(CancellationToken ct)
    {
        var devices = await _connectionService.GetDiscoveredDevicesAsync(ct);
        var gw = devices?.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);
        if (gw?.Mac == null)
            return (null, null);

        var primaryIfaces = await _connectionService.GetPrimaryWanInterfacesAsync(ct);
        var wanCounterNames = !string.IsNullOrEmpty(primaryIfaces?.CounterIfName)
            ? new List<string> { primaryIfaces!.CounterIfName! }
            : gw.WanInterfaceNames;
        if (wanCounterNames == null || wanCounterNames.Count == 0)
        {
            _logger.LogDebug("ISP Health: no WAN counter interface resolved");
            return (gw.Mac, null);
        }
        if (primaryIfaces?.CounterIfName == null)
            _logger.LogDebug("ISP Health: primary WAN unresolved, falling back to active uplink {Ifaces}", string.Join(",", wanCounterNames));
        return (gw.Mac, wanCounterNames);
    }

    /// <summary>
    /// Hour-of-day usage fingerprint from the WAN throughput we already record (no new measurement):
    /// per local hour-of-day, the fraction of sampled time the line was actively in use (DS/US above
    /// the configured active thresholds). Drives time-of-day outage weighting. Returns null - so
    /// weighting falls back to a flat 1.0 and outages are NOT graded down - when usage weighting is
    /// off, no gateway/data is found, or the data spans fewer than <see cref="IspHealthOptions.UsageFingerprintMinHours"/>
    /// hours (too little to read a time-of-day pattern). Uses whatever history exists up to the
    /// lookback; the lookback is a ceiling, not a requirement, so ~a day of data is enough to attempt one.
    /// </summary>
    private async Task<double[]?> BuildUsageFingerprintAsync(DateTime windowEnd, CancellationToken ct)
    {
        if (!_options.UsageWeightingEnabled) return null;
        try
        {
            var (mac, ifNames) = await ResolveWanCounterAsync(ct);
            if (mac == null || ifNames == null || ifNames.Count == 0) return null;

            var from = windowEnd.AddDays(-_options.UsageFingerprintLookbackDays);
            // Active usage is sustained (streaming, calls, uploads); a 5-min mean is plenty to catch
            // it and keeps the lookback series small.
            var rates = await _influx.QueryGatewayWanRatesAsync(mac, ifNames, from, windowEnd, TimeSpan.FromMinutes(5), ct);
            if (rates.Count == 0) return null;

            var tz = TimeZoneInfo.Local;
            var active = new double[24];
            var total = new double[24];
            DateTime? earliest = null, latest = null;
            foreach (var r in rates)
            {
                var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.Time, DateTimeKind.Utc), tz);
                total[local.Hour] += 1;
                if (r.DownloadBps > _options.UsageActiveDownstreamBps || r.UploadBps > _options.UsageActiveUpstreamBps)
                    active[local.Hour] += 1;
                if (earliest is null || r.Time < earliest) earliest = r.Time;
                if (latest is null || r.Time > latest) latest = r.Time;
            }
            // Need roughly a full daily cycle of data to read a time-of-day pattern; less than that
            // can't distinguish "busy hour" from "quiet hour", so leave outages unweighted.
            var spanHours = earliest is { } e && latest is { } l ? (l - e).TotalHours : 0;
            if (spanHours < _options.UsageFingerprintMinHours) return null;

            var fraction = new double[24];
            for (var h = 0; h < 24; h++)
                fraction[h] = total[h] > 0 ? active[h] / total[h] : 0.0;
            _logger.LogDebug("ISP Health: usage fingerprint over {Span:0} h of data, peak-hour active {Peak:P0}", spanHours, fraction.Max());
            return fraction;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health: usage fingerprint build failed");
            return null;
        }
    }

    /// <summary>Expected plan speeds for callers outside the scoring pipeline (e.g. loaded-loss investigation).</summary>
    public async Task<(double? DownMbps, double? UpMbps)> GetExpectedWanSpeedsAsync(CancellationToken ct = default)
    {
        var (down, up, _, _) = await ResolveExpectedSpeedsAsync(ct);
        return (down, up);
    }

    /// <summary>
    /// The exact set of target IDs whose loss ISP Health pools into the Packet Loss and Loaded Loss
    /// factors: every enabled access ISP hop, every enabled transit hop except non-transit IXP /
    /// anycast infrastructure (WoodyNet / PCH), and the well-known anycast DNS resolvers. Kept here as
    /// the single source of the pool definition (mirrors the lossPool built in ComputeCoreAsync) so the
    /// Investigate loss highlight can average the very pool the score is graded on instead of a
    /// per-type approximation, and the two can never drift.
    /// </summary>
    public async Task<List<string>> GetLossPoolTargetIdsAsync(CancellationToken ct = default)
    {
        await using var db = await CreateSiteDbAsync(ct);
        var targets = await db.MonitoringTargets.AsNoTracking()
            .Where(t => t.Enabled && (t.TargetType == MonitoringTargetType.AccessIsp
                || t.TargetType == MonitoringTargetType.Transit
                || t.TargetType == MonitoringTargetType.InternetService))
            .ToListAsync(ct);
        return targets
            .Where(t => t.TargetType == MonitoringTargetType.AccessIsp
                || (t.TargetType == MonitoringTargetType.Transit
                    && !(t.AsnNumber is int a && WellKnownAsns.NonTransitInfrastructure.Contains(a)))
                || (t.TargetType == MonitoringTargetType.InternetService && AnycastDnsIps.Contains(t.Address)))
            .Select(t => t.TargetId)
            .ToList();
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
            var primary = UniFiConnectionService.ResolvePrimaryWanNetwork(networks, _logger);
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
            await using var db = await CreateSiteDbAsync(ct);
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

    private static readonly TimeSpan SqmProbeDuration = TimeSpan.FromSeconds(30);

    private async Task<string?> GetPrimaryWanInterfaceAsync(CancellationToken ct)
    {
        try
        {
            return await _connectionService.GetPrimaryWanDataPathInterfaceAsync(ct);
        }
        catch { return null; }
    }

    private async Task<List<(DateTime Start, DateTime End)>> BuildSqmProbeExclusionsAsync(
        DateTime windowStart, DateTime windowEnd, string? primaryWanInterface, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(primaryWanInterface)) return new List<(DateTime, DateTime)>();

        await using var db = await CreateSiteDbAsync(ct);
        var sqmConfigs = await db.SqmWanConfigurations.AsNoTracking()
            .Where(c => c.Enabled)
            .ToListAsync(ct);
        sqmConfigs = sqmConfigs
            .Where(c => string.Equals(c.Interface, primaryWanInterface, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sqmConfigs.Count == 0) return new List<(DateTime, DateTime)>();

        // Schedule hours are in the gateway's local time (crontab timezone). The app runs
        // on the same network, so server local time matches. Convert to UTC for comparison
        // with the ISP Health window (all UTC).
        var localZone = TimeZoneInfo.Local;
        var exclusions = new List<(DateTime Start, DateTime End)>();
        foreach (var config in sqmConfigs)
        {
            var probeTimes = new[] {
                (config.SpeedtestMorningHour, config.SpeedtestMorningMinute),
                (config.SpeedtestEveningHour, config.SpeedtestEveningMinute)
            };
            // The loop walks LOCAL calendar days but seeds `day` from UTC window bounds, so the
            // -24h start and +1-day end overshoot are LOAD-BEARING: they guarantee every probe
            // whose UTC instant lands in [windowStart, windowEnd] is generated regardless of the
            // local UTC offset (real offsets reach +-14h). The `utcProbe >= windowStart &&
            // <= windowEnd` filter below trims the overshoot. Do not "tighten" this to .Date
            // without the buffer - it would drop boundary probes for any non-UTC zone.
            for (var day = windowStart.AddHours(-24).Date; day <= windowEnd.Date; day = day.AddDays(1))
            {
                foreach (var (hour, minute) in probeTimes)
                {
                    var localProbe = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
                    // A probe time inside the DST spring-forward gap is an invalid local time;
                    // ConvertTimeToUtc would throw. The probe never runs at a nonexistent
                    // wall-clock time anyway, so skip the exclusion for that day.
                    if (localZone.IsInvalidTime(localProbe)) continue;
                    var utcProbe = TimeZoneInfo.ConvertTimeToUtc(localProbe, localZone);
                    if (utcProbe >= windowStart && utcProbe <= windowEnd)
                        exclusions.Add((utcProbe, utcProbe + SqmProbeDuration));
                }
            }
        }
        return exclusions;
    }

    /// <summary>
    /// True when OUR Adaptive SQM is enabled and configured for the primary WAN (an enabled
    /// <see cref="SqmWanConfiguration"/> matching the interface). Distinct from UniFi's base
    /// Smart Queues toggle (<see cref="IspHealthInputs.SmartQueuesEnabled"/>); the loaded-loss
    /// recommendation uses this so it never tells a user to "consider Adaptive SQM" when they
    /// already run it.
    /// </summary>
    private async Task<bool> IsAdaptiveSqmEnabledAsync(string? primaryWanInterface, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(primaryWanInterface)) return false;
        await using var db = await CreateSiteDbAsync(ct);
        var sqmConfigs = await db.SqmWanConfigurations.AsNoTracking()
            .Where(c => c.Enabled)
            .ToListAsync(ct);
        return sqmConfigs.Any(c => string.Equals(c.Interface, primaryWanInterface, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Server/gateway WAN speed tests only: Cloudflare and UWN runs. Client-initiated
    /// WAN tests (OpenSpeedTest from a browser via an external server) are excluded
    /// because the client's own link contaminates the measurement.
    /// </summary>
    private async Task<List<SpeedTestSample>> LoadWanSpeedTestsAsync(DateTime windowStart, DateTime windowEnd, CancellationToken ct)
    {
        try
        {
            // Reach back the WIDER of the selected window or the fallback floor: a long window
            // (e.g. 30 d) finds its best demonstrated capacity across the whole window, while a
            // short window keeps the SpeedTestFallbackDays floor so a sparse run of tests still
            // yields a recent capacity number. Bounded above by windowEnd for historical windows.
            var fallbackStart = windowEnd.AddDays(-_options.SpeedTestFallbackDays);
            var since = windowStart < fallbackStart ? windowStart : fallbackStart;
            await using var db = await CreateSiteDbAsync(ct);
            var results = await db.Iperf3Results.AsNoTracking()
                .Where(r => r.Success
                    && r.TestTime >= since
                    && r.TestTime <= windowEnd
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
    private (List<AsnSeries> IspGrading, List<AsnSeries> TransitGrading, List<AsnSeries> AllClusters, List<AsnSeries> IspChart, List<AsnSeries> TransitChart) BuildAsnSeriesSets(
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
            ispChart, transitChart);
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
            // The stored AsnName was cleaned by CleanOrgName at discovery/add time (industry
            // suffixes). Re-run the lighter AsnNameCleanup here so brand overrides (e.g. Arelion
            // Sweden -> Arelion) apply to already-stored names without needing re-discovery.
            var asnName = AsnNameCleanup.Clean(
                group.Select(t => t.AsnName).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                ?? (asnOverrides != null
                    ? group.Select(t => asnOverrides.TryGetValue(t.TargetId, out var o) ? o.Name : null).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                    : null)
                ?? group.Select(t => t.Name).FirstOrDefault());

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
                    Samples = clusterTargets.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList(),
                    // Hop IPs and proven-upstream ancestors so the congestion localizer can
                    // place this cluster on the trace map and walk the bottleneck.
                    HopIps = clusterTargets.Select(t => t.Address).ToList(),
                    AncestorIps = clusterTargets
                        .SelectMany(t => ancestorIpsByTargetId.TryGetValue(t.TargetId, out var anc) ? anc : Enumerable.Empty<string>())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
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
                        Samples = others.SelectMany(t => seriesByTarget[t.TargetId]).OrderBy(s => s.Time).ToList(),
                        HopIps = others.Select(t => t.Address).ToList(),
                        AncestorIps = others
                            .SelectMany(t => ancestorIpsByTargetId.TryGetValue(t.TargetId, out var anc) ? anc : Enumerable.Empty<string>())
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList()
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

    /// <summary>
    /// On a long viewing window the report is computed on a coarse aggregate (the point-count cap),
    /// so a marginal congestion event's 15-min bucket boundaries can land a bucket off from where the
    /// fine-resolution canonical view places them. For each event, re-query its target(s) at the
    /// canonical fine aggregate over just the event's neighborhood, re-run detection, and snap
    /// Start/End to the overlapping fine run. No-op at canonical resolution (aggregate already fine);
    /// the per-event neighborhood reads fire concurrently so the added wall-clock is ~one small read.
    /// </summary>
    private async Task RefineCongestionBoundariesAsync(
        List<CongestionEvent> events, TimeSpan aggregate, CancellationToken ct)
    {
        var fine = TimeSpan.FromSeconds(_options.LoadWindowSeconds);
        if (events.Count == 0 || aggregate <= fine) return;

        // Enough clean baseline on each side of a bounded event for fine re-detection to anchor.
        var pad = TimeSpan.FromHours(2);

        var refined = await Task.WhenAll(events.Select(async e =>
        {
            var runs = new List<(DateTime Start, DateTime End)>();
            foreach (var tid in e.TargetIds)
            {
                var pts = await _influx.QueryLatencyDetailByTargetIdAsync(tid, e.Start - pad, e.End + pad, fine, ct);
                if (pts.Count == 0) continue;
                var series = new AsnSeries
                {
                    TargetIds = { tid },
                    Samples = pts.Select(p => new LatencySample(p.Time, p.RttAvgMs, p.RttMaxMs, p.JitterMs, p.LossPercent)).ToList()
                };
                foreach (var r in CongestionDetector.DetectForSeries(series, _options))
                    if (r.Start < e.End && e.Start < r.End) // overlaps the coarse event
                        runs.Add((r.Start, r.End));
            }
            return (Event: e, Runs: runs);
        }));

        // Fine re-detection (with a 2 h clean pad on each side for a baseline, read across the view's
        // edge) is ground truth. Coarse aggregation INFLATES bucket-p90, so "fires at the coarse
        // aggregate but not at full resolution" is the signature of a coarse artifact - e.g. a
        // window-edge p90 phantom on a flat hop - not a real event. Drop those; a genuine event
        // reproduces against the padded fine baseline at both resolutions.
        var phantoms = refined.Where(r => r.Runs.Count == 0).Select(r => r.Event).ToHashSet();
        foreach (var (e, runs) in refined)
        {
            if (runs.Count == 0) continue;
            e.Start = runs.Min(r => r.Start);
            e.End = runs.Max(r => r.End);
        }
        events.RemoveAll(phantoms.Contains);
    }

    private static Dictionary<string, List<LatencySample>> ToSamples(
        Dictionary<string, List<MonitoringInfluxClient.LatencySeriesPoint>> raw)
    {
        return raw.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Select(p => new LatencySample(p.Time, p.RttAvgMs, p.RttMaxMs, p.JitterMs, p.LossPercent)).ToList());
    }
}
