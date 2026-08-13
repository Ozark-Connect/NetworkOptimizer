using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;

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
    // The UniFi wan key ("wan2") this instance grades, or null for the configured-primary
    // instance - which is every install's only instance until it has more than one WAN.
    // The primary instance resolves its wan key per compute (today's behavior, unchanged);
    // a scoped instance grades exactly the WAN it was created for.
    private readonly string? _scopedWanKey;
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
    // Set by Invalidate() to force the next read to recompute while KEEPING _cached, so the
    // glanceable tiles keep serving the prior score instead of dropping to a setup prerequisite.
    private bool _recomputePending;
    private volatile bool _computing;
    // Trailing window (hours) the last successful auto-compute used. Drops down the ladder when a
    // longer window exceeds the compute budget on this hardware; resets to 0 on process restart so the
    // configured target is re-probed once after each deploy. 0 until the first auto-compute runs.
    private volatile int _effectiveWindowHours;
    // Configured target window (hours), cached from MonitoringSettings on each auto-compute so the
    // dashboard tile and tab can read it without a DB hit. 0 until the first auto-compute runs.
    private volatile int _configuredWindowHours;
    /// <summary>Connected agents, for naming the boxes a policy route might steer. Null in tests.</summary>
    private readonly AgentTunnelRegistry? _tunnelRegistry;
    /// <summary>Resolves which box actually probes the unassigned targets. Null in tests.</summary>
    private readonly AgentProbeResultSink? _probeSink;
    /// <summary>Tells a gateway-resident agent from one on the LAN. Null in tests.</summary>
    private readonly AgentOnGatewayDetector? _onGatewayDetector;

    public IspHealthService(
        MonitoringInfluxRegistry influxRegistry,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SiteDbContextFactory siteDbFactory,
        SiteConnectionRegistry siteConnections,
        PhysicalLinkResolver physicalLinkResolver,
        ILogger<IspHealthService> logger,
        AgentTunnelRegistry? tunnelRegistry = null,
        AgentProbeResultSink? probeSink = null,
        AgentOnGatewayDetector? onGatewayDetector = null,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        string? wanInterface = null)
    {
        _tunnelRegistry = tunnelRegistry;
        _probeSink = probeSink;
        _onGatewayDetector = onGatewayDetector;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _scopedWanKey = string.IsNullOrWhiteSpace(wanInterface)
            ? null : GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface.Trim());
        _influx = influxRegistry.GetFor(_siteSlug);
        _dbFactory = dbFactory;
        _siteDbFactory = siteDbFactory;
        _connectionService = siteConnections.GetFor(_siteSlug);
        _physicalLinkResolver = physicalLinkResolver;
        _logger = logger;
    }

    /// <summary>
    /// The UniFi wan key this instance grades, or null for the configured-primary instance
    /// (which resolves its WAN per compute). Registry key, and what the UI selectors route on.
    /// </summary>
    public string? ScopedWanInterface => _scopedWanKey;

    /// <summary>
    /// Every site (the home site included) reads its expected ISP plan speeds from the UniFi
    /// Console, so computing ISP Health before that connection is up would cache a report with
    /// an unscored Speed vs Plan factor. Gates every compute entry point; a managed site's
    /// connection simply comes up later (over its agent tunnel) than the home site's.
    /// </summary>
    // A remembered WAN profile is enough to grade a site whose console is unreachable - the scored
    // inputs are latency and throughput history out of InfluxDB, and the console only supplied the
    // expected speeds. Resolved per compute rather than cached: a site that has never had a
    // successful console read still has nothing to score against.
    private bool CanCompute => _connectionService.IsConnected || _hasRememberedWanSpeeds;

    private bool _hasRememberedWanSpeeds;

    /// <summary>
    /// Whether any WAN has speeds remembered from an earlier console read. Refreshed on each compute
    /// so a site that gains its first successful read starts computing without a restart.
    /// </summary>
    private async Task RefreshRememberedWanSpeedsAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            _hasRememberedWanSpeeds = await db.WanProfiles
                .AnyAsync(w => w.DownloadMbps != null || w.UploadMbps != null, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check for remembered WAN speeds");
        }
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
            var rows = await db.WanDiscoveryContexts.ToListAsync(ct);
            // The SCORED WAN's context row - a scoped instance writes its own WAN's technology,
            // never the primary's. The primary resolves its key like the compute does (configured
            // role first, "wan"-first guess offline) - and is NOT filtered to non-Unknown:
            // setting it when it is currently unset is the whole point. Create it if missing,
            // matching Upstream Discovery's create-if-missing on commit.
            var writeKey = _scopedWanKey
                ?? await ResolveConfiguredPrimaryWanKeyAsync(ct)
                ?? ResolvePrimaryWanKey(rows);
            var ctxRow = rows.FirstOrDefault(c => string.Equals(
                GatewayWanHelper.WanInterfaceKeyFromKey(c.WanInterface ?? ""),
                GatewayWanHelper.WanInterfaceKeyFromKey(writeKey), StringComparison.OrdinalIgnoreCase));
            if (ctxRow == null)
            {
                ctxRow = new WanDiscoveryContext { WanInterface = writeKey };
                db.WanDiscoveryContexts.Add(ctxRow);
            }
            ctxRow.AccessTechnology = technology;
            ctxRow.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);
        }
        await GetReportAsync(forceRefresh: true, ct);
    }

    /// <summary>
    /// Marks the outage starting at <paramref name="outageStartUtc"/> as user-caused ("that
    /// was me" - their own maintenance, e.g. pulling the coax to add a pad), which excludes it
    /// from the score and the findings, then recomputes. Keyed on the onset time and matched
    /// by tolerance (<see cref="IspHealthOptions.OutageAckMatchToleranceSeconds"/>), because a
    /// recompute can shift a detected outage's boundaries by a bucket.
    /// </summary>
    public async Task AcknowledgeOutageAsync(DateTime outageStartUtc, CancellationToken ct = default)
    {
        await using (var db = await CreateSiteDbAsync(ct))
        {
            var tolerance = TimeSpan.FromSeconds(_options.OutageAckMatchToleranceSeconds);
            var exists = (await db.OutageAcknowledgements.AsNoTracking().ToListAsync(ct))
                .Any(a => (a.OutageStartUtc - outageStartUtc).Duration() <= tolerance);
            if (!exists)
            {
                db.OutageAcknowledgements.Add(new OutageAcknowledgement
                {
                    OutageStartUtc = outageStartUtc,
                    AcknowledgedAt = DateTime.UtcNow
                });
                await db.SaveChangesAsync(ct);
            }
        }
        await GetReportAsync(forceRefresh: true, ct);
    }

    /// <summary>Removes a "that was me" acknowledgement (tolerance-matched) and recomputes.</summary>
    public async Task UnacknowledgeOutageAsync(DateTime outageStartUtc, CancellationToken ct = default)
    {
        await using (var db = await CreateSiteDbAsync(ct))
        {
            var tolerance = TimeSpan.FromSeconds(_options.OutageAckMatchToleranceSeconds);
            var matches = (await db.OutageAcknowledgements.ToListAsync(ct))
                .Where(a => (a.OutageStartUtc - outageStartUtc).Duration() <= tolerance)
                .ToList();
            if (matches.Count > 0)
            {
                db.OutageAcknowledgements.RemoveRange(matches);
                await db.SaveChangesAsync(ct);
            }
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
        if (report != null && !_recomputePending && DateTime.UtcNow - report.ComputedAt < _options.DashboardScoreTtl)
            return new IspHealthSnapshot(IspHealthStatus.Ready, report.OverallScore, report.ComputedAt);

        // Managed site whose console isn't up yet: serve any stale report, but don't kick a
        // compute until the connection lands (expected ISP speeds come from the console).
        if (!CanCompute)
            return report != null
                ? new IspHealthSnapshot(IspHealthStatus.Ready, report.OverallScore, report.ComputedAt)
                : new IspHealthSnapshot(IspHealthStatus.AwaitingConnection, null, null);

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
        // A pending invalidation forces a recompute like forceRefresh, but without the connection-
        // cache clear below - the scored inputs changed (a target was toggled), not the console data.
        var mustRecompute = forceRefresh || _recomputePending;
        var cached = _cached?.Report;
        if (!mustRecompute && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
            return cached;

        // The console supplies the expected ISP speeds, so a site that has never had a successful
        // read has nothing to score against and still waits. One that has cannot be held back by an
        // unreachable console: the scored inputs are history out of InfluxDB, and an offline site is
        // exactly when someone wants to look at it. Checked here rather than cached at startup so a
        // site gains computing on its first successful read without a restart.
        if (!_connectionService.IsConnected)
            await RefreshRememberedWanSpeedsAsync(ct);

        // Serve any existing report; otherwise publish AwaitingConnection for the funnels.
        // Keep _recomputePending set so the recompute happens once the connection lands.
        if (!CanCompute)
        {
            if (cached == null)
                _status = IspHealthStatus.AwaitingConnection;
            return cached;
        }

        await _computeLock.WaitAsync(ct);
        try
        {
            cached = _cached?.Report;
            mustRecompute = forceRefresh || _recomputePending;
            if (!mustRecompute && cached != null && DateTime.UtcNow - cached.ComputedAt < _options.CacheTtl)
                return cached;

            if (forceRefresh)
                _connectionService.ClearCaches();

            _computing = true;
            var (report, chartClusters) = await ComputeAsync(ct);
            // One forced recompute consumed; future reads fall back to normal TTL rules.
            _recomputePending = false;
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
    /// Force the next <see cref="GetReportAsync"/> / custom-window read to recompute from current
    /// data, without discarding the last score. Called when Upstream Discovery is committed (so the
    /// "re-run discovery" banner clears on the next visit) and when the scored target set changes
    /// (e.g. a flaky target is disabled), so the score refreshes without a manual refresh.
    /// The cached report is KEPT so the glanceable Live-view tiles keep showing the prior score
    /// (Ready) while the recompute runs, instead of dropping to a "set up ISP Health" prerequisite
    /// state. The custom-window dedup cache is cleared (the detail tab renders its own loading state).
    /// </summary>
    public void Invalidate()
    {
        _recomputePending = true;
        _customCache = null;
    }

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
        // No console connection means no expected ISP speeds to score against; skip the heavy
        // compute rather than cache a plan-less custom-window report.
        if (!CanCompute)
            return (null, new List<AsnSeries>());

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

    /// <summary>
    /// Rounds the aggregate down to whole units - days, then hours, then minutes, then seconds.
    ///
    /// This is not a nicety: until the Flux duration renderer was corrected it truncated to whole
    /// units on the way out, so a computed 103.97 s ran as 60 s and ISP Health's real resolution on a
    /// 30-day window was 60 s, not 104 s. Rendering it exactly would silently coarsen every detector
    /// and score on long windows, so the historical value is reproduced here deliberately instead.
    /// Charts are left on the corrected value - they target ~150 points and do not care - but ISP
    /// Health's resolution is a correctness property of the outage, congestion and loss analysis, and
    /// is not something to change as a side effect of fixing a renderer.
    /// </summary>
    internal static TimeSpan SnapAggregate(TimeSpan window)
    {
        var seconds =
            window.TotalDays >= 1 ? Math.Floor(window.TotalDays) * 86400
            : window.TotalHours >= 1 ? Math.Floor(window.TotalHours) * 3600
            : window.TotalMinutes >= 1 ? Math.Floor(window.TotalMinutes) * 60
            : Math.Floor(window.TotalSeconds);
        return TimeSpan.FromSeconds(Math.Max(1, seconds));
    }

    private async Task<ComputeOutcome> ComputeCoreAsync(DateTime windowStart, DateTime windowEnd,
        IReadOnlyList<CongestionEvent>? referenceEvents, CancellationToken ct)
    {
        // Only the auto-compute path was timed, so a custom window - which is where the expensive
        // long spans are actually requested - had no cost signal at all. The budget's failure mode is
        // silently dropping the user to a shorter window, so changes near the query path need a
        // before/after number rather than an assumption.
        var computeSw = System.Diagnostics.Stopwatch.StartNew();
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
        // Onsets of outages the user marked "that was me", stamped onto the detected events
        // below (tolerance-matched; a recompute can shift a boundary by a bucket).
        List<DateTime> ackedOutageStarts;
        // The WAN this report grades, in MonitoringTarget.WanInterface's namespace (the UniFi WAN
        // name, "wan"/"wan2" - NOT the data-path ifname GetPrimaryWanInterfaceAsync returns).
        // Every input below - targets, discoveries, latency series, counters, expected speeds -
        // is scoped to this one WAN, so a second WAN's data can never leak into this report.
        string? primaryWanKey;
        string scoredWanKey;
        // True for the configured-primary instance (_scopedWanKey null): it additionally owns
        // every row with no WAN stamped (hand-added and legacy targets), preserving single-WAN
        // behavior exactly. A scoped instance owns only rows stamped with its own wan key.
        var primaryScope = _scopedWanKey == null;
        // The wan-tag scope the latency reads filter on (see MonitoringInfluxClient.LatencyWanScope).
        MonitoringInfluxClient.LatencyWanScope? wanScope;
        await using (var db = await CreateSiteDbAsync(ct))
        {
            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            if (settings == null || !settings.Enabled)
                return new ComputeOutcome(IspHealthStatus.NotConfigured, null, new List<AsnSeries>());

            // Access technology lives per-WAN in WanDiscoveryContexts (the wizard's
            // store, which replaced the global MonitoringSettings column). Same wan-first
            // ordering as before, but the interface NAME and without an access-technology
            // filter: a WAN whose technology was never set still owns its targets. Falls back to
            // "wan" so a site with no discovery context yet still scopes to the conventional primary.
            var wanContexts = await db.WanDiscoveryContexts.AsNoTracking().ToListAsync(ct);
            // Primary is a ROLE: ask the console which group holds it (any wanN can); the
            // name-ordered context guess is the offline fallback only.
            primaryWanKey = await ResolveConfiguredPrimaryWanKeyAsync(ct) ?? ResolvePrimaryWanKey(wanContexts);
            scoredWanKey = _scopedWanKey ?? primaryWanKey;

            // The scored WAN's OWN discovery context decides its technology; the legacy global
            // MonitoringSettings value is the primary's fallback only (installs predating the
            // per-WAN context). A scoped WAN with no technology set funnels to NeedsTechnology
            // below rather than borrowing the primary's - grading LTE against fiber thresholds
            // is exactly the mispairing per-WAN scoring exists to kill.
            var scoredContext = wanContexts.FirstOrDefault(c =>
                string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(c.WanInterface ?? ""),
                    GatewayWanHelper.WanInterfaceKeyFromKey(scoredWanKey), StringComparison.OrdinalIgnoreCase));
            technology = scoredContext?.AccessTechnology is { } t && t != AccessTechnology.Unknown
                ? t
                : primaryScope ? settings.AccessTechnology : AccessTechnology.Unknown;

            targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit
                    || t.TargetType == MonitoringTargetType.InternetService
                    // Custom targets a user added (e.g. a known-stable CMTS/PoP ping) join the
                    // destination witness pool: once traced (ancestry), a clean end-to-end reading
                    // absolves the on-path ISP/transit hops it routes through, same strict gate as
                    // an Internet target. Not graded as an ISP/transit card themselves.
                    || t.TargetType == MonitoringTargetType.Custom))
                .ToListAsync(ct);
            // Scope to the WAN being graded. In memory (case-insensitive like every other
            // WanInterface comparison), and null-WanInterface rows go to the primary only -
            // hand-added and legacy targets were always primary-path measurements.
            targets = ScopeTargetsToWan(targets, scoredWanKey, includeUnassigned: primaryScope);

            // Fabric targets stay unscoped: the LAN gateway is shared by every WAN, and its
            // series only scopes outages (gateway-unreachable => LAN outage, not WAN).
            fabricTargets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && t.TargetType == MonitoringTargetType.Fabric && t.DeviceMac != null)
                .ToListAsync(ct);

            // Discoveries scoped like the targets: this WAN's rows, plus unstamped legacy rows
            // for the primary only. Ancestry, hopOrderKnown, and the hop-number map all follow,
            // so another WAN's trace data can never flip this WAN's jitter-absolve gate or
            // hop ordering - and a scoped WAN with no discovery of its own conservatively
            // reads as "no trace map" (hopOrderKnown false) instead of borrowing one.
            var discoveries = (await db.UpstreamDiscoveries.AsNoTracking()
                .Where(d => d.IsActive && d.MonitoringTargetId != null)
                .ToListAsync(ct))
                .Where(d => string.IsNullOrEmpty(d.WanInterface)
                    ? primaryScope
                    : string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(d.WanInterface),
                        GatewayWanHelper.WanInterfaceKeyFromKey(scoredWanKey), StringComparison.OrdinalIgnoreCase))
                .ToList();

            // Latency reads filter the Influx `wan` tag to this WAN's series: untagged points
            // for the primary, a WAN's context tag values for a scoped WAN (see BuildWanScope).
            var bindingContexts = await db.WanContexts.AsNoTracking().ToListAsync(ct);
            // No contexts means nothing has ever written a wan tag here, so there is nothing to
            // filter apart: the primary instance reads exactly the unfiltered query it always
            // has. That keeps every single-WAN install on the query shape that is already proven
            // in the field rather than on a tag-absence predicate for no gain.
            wanScope = primaryScope && bindingContexts.Count == 0
                ? null
                : BuildWanScope(bindingContexts, scoredWanKey, primaryScope);
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

            ackedOutageStarts = await db.OutageAcknowledgements.AsNoTracking()
                .Select(a => a.OutageStartUtc)
                .ToListAsync(ct);
        }

        var ispTargets = targets.Where(t => t.TargetType == MonitoringTargetType.AccessIsp).ToList();
        // WoodyNet / PCH (AS42, AS715) and similar IXP/anycast-DNS infrastructure are not
        // transit; drop them so they never enter scoring, the per-ASN cards, or the chart clusters.
        var transitTargets = targets.Where(t => t.TargetType == MonitoringTargetType.Transit
            && !(t.AsnNumber is int a && WellKnownAsns.NonTransitInfrastructure.Contains(a))).ToList();
        if (ispTargets.Count == 0 && transitTargets.Count == 0)
            return new ComputeOutcome(IspHealthStatus.NeedsDiscovery, null, new List<AsnSeries>());

        // Proof the console actually served data. IsConnected only means the login succeeded, and
        // GetDevicesAsync returns an empty list rather than throwing, so without this a restart can
        // cache a report that silently lost the PPPoE overlay and expected WAN speeds. A null
        // report leaves _cached untouched, so the next read recomputes. Empty list rather than a
        // missing gateway: a site behind a third-party router has no gateway and must still score.
        var discoveredDevices = await _connectionService.GetDiscoveredDevicesAsync(ct);
        // Only suspect while the console CLAIMS to be up: a connected console serving nothing is
        // the transient this guard was written for. A console that is plainly down is not a
        // half-loaded one - it is a site someone is trying to look at after the fact, and the
        // expected speeds it used to supply now come from the remembered WAN profile. Deferring
        // there would refuse every offline site, which is the case the history exists for.
        if (discoveredDevices.Count == 0 && _connectionService.IsConnected)
        {
            _logger.LogWarning("ISP Health: the console returned no devices, so its data is not yet trustworthy; " +
                "deferring rather than caching a report computed without the console-derived inputs");
            return new ComputeOutcome(IspHealthStatus.AwaitingConnection, null, new List<AsnSeries>());
        }
        if (discoveredDevices.Count == 0)
            _logger.LogDebug("ISP Health: computing without the console (site offline); expected speeds come from " +
                "the remembered WAN profile and device-derived detail is omitted");

        var profile = IspHealthProfiles.GetProfile(technology);
        if (profile == null)
            return new ComputeOutcome(IspHealthStatus.NeedsTechnology, null, new List<AsnSeries>());

        // A PPPoE session costs latency and loaded loss on top of whatever the medium does, so it
        // is overlaid on the medium's profile rather than replacing it. Read from the gateway, not
        // from the user: the encapsulation and the medium are independent facts, and only the
        // medium needs asking for. Read off the SCORED WAN's own data-path interface - a PPPoE
        // secondary behind a plain-DHCP primary gets its overlay, and vice versa.
        // Null (couldn't tell) scores like false - there is nothing else it can do - but it is
        // logged as the unknown it is rather than passed off as a settled answer.
        var pppoeSession = await IsPppoeWanAsync(ct);
        if (pppoeSession == true)
            profile = IspHealthProfiles.ApplyPppoeSession(profile, technology);

        // First gateway from the device list above (shadow-mode multi-gateway isn't handled
        // yet - first gateway is fine), matched to its fabric monitoring target by MAC so we can pull
        // its loss for outage scoping. Null when no gateway is monitored - outage scoping then stays
        // unchanged (no Local scope possible).
        static string MacKey(string? m) => new string((m ?? "").Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant();
        var gatewayDevice = discoveredDevices.FirstOrDefault(d => d.Type.IsGateway());
        var gatewayTarget = gatewayDevice == null ? null
            : fabricTargets.FirstOrDefault(t => MacKey(t.DeviceMac) == MacKey(gatewayDevice.Mac));

        // Fine-grained join window so short load bursts (speed tests, downloads) classify as
        // loaded instead of diluting into minute-level means. Longer (filter-selected) windows
        // coarsen it to keep the point count bounded; the canonical 48 h window lands on exactly
        // LoadWindowSeconds, so the auto-computed report is unchanged.
        var aggregate = SnapAggregate(TimeSpan.FromSeconds(Math.Max(
            _options.LoadWindowSeconds, (windowEnd - windowStart).TotalSeconds / 25000.0)));

        // Outage detection reaches back a bounded lead-in BEFORE the window start so an outage already
        // in progress when the window opens is detected from its true onset instead of being clipped to
        // a mislabeled tail (a 47-min LAN/Gateway power outage whose window-start-clipped tail lost its
        // gateway-dark evidence and read as a path-wide ISP blip). The access-ISP, internet, and gateway
        // series are queried over this extended window; the score/congestion/loss-pool consumers below
        // are given the series trimmed back to [windowStart, windowEnd], so everything except outage
        // detection is byte-for-byte unchanged. Transit stays on the exact window - it only shapes the
        // waterfall and never decides the Local (gateway-dark) scope, so extending it buys nothing.
        var outageQueryStart = windowStart.AddHours(-_options.OutageDetectionLeadInHours);

        // All target types read at the fine window. Coarsening transit/internet RTT bought a modest
        // 48h deserialize win but shifted congestion localization (the bottleneck walk keys on RTT
        // bursts, which a coarse mean blunts), so it diverged from the fine-resolution attribution on
        // transit-heavy paths. Kept fine everywhere; the compute-time wins now come from the in-memory
        // detector/scorer paths, not from coarsening the input.
        // Everything above runs before a single query is issued - site database reads, target and
        // technology resolution, and console calls. The "fetch" figure lumped it in with the reads,
        // which measured the four latency queries at ~1s from the box while fetch showed ~6.8s.
        var setupMs = computeSw.ElapsedMilliseconds;
        // Every type-level read carries the wan-tag scope, so a second WAN's series never enter
        // this report even where a target id joined both (reassignment history). The gateway
        // (fabric) read below stays unscoped by design: the LAN gateway serves every WAN.
        var ispSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.AccessIsp, outageQueryStart, windowEnd, aggregate, wanScope, ct);
        var transitSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.Transit, windowStart, windowEnd, aggregate, wanScope, ct);
        var internetSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.InternetService, outageQueryStart, windowEnd, aggregate, wanScope, ct);
        var customSeriesTask = _influx.QueryLatencyDetailByTargetTypeAsync(MonitoringTargetType.Custom, windowStart, windowEnd, aggregate, wanScope, ct);
        // Rates keep a fine interval whatever the window length. Thinning them with everything else
        // destroys the only property that separates sustained load from a spike - whether neighboring
        // samples are loaded too - because a minute-long transfer and a one-sample counter artifact
        // both collapse to a single point. One series against ~25 per-target latency series, so the
        // extra rows are cheap relative to what the compute already carries.
        var rateAggregate = TimeSpan.FromSeconds(
            Math.Min(aggregate.TotalSeconds, Math.Max(_options.LoadWindowSeconds, _options.WanRateMaxAggregateSeconds)));
        var ratesTask = QueryWanRatesAsync(windowStart, windowEnd, rateAggregate, ct);
        var speedsTask = ResolveExpectedSpeedsAsync(ct);
        var speedTestsTask = LoadWanSpeedTestsAsync(windowStart, windowEnd, ct);
        var gatewaySeriesTask = gatewayTarget == null
            ? Task.FromResult(new List<MonitoringInfluxClient.LatencySeriesPoint>())
            : _influx.QueryLatencyDetailByTargetIdAsync(gatewayTarget.TargetId, outageQueryStart, windowEnd, aggregate, ct);
        await Task.WhenAll(ispSeriesTask, transitSeriesTask, internetSeriesTask, customSeriesTask, ratesTask, speedsTask, speedTestsTask, gatewaySeriesTask);
        // Split the compute at the point every query has returned. Three rounds of optimizing the rate
        // path moved the total by nothing, which means the cost is not where it was assumed to be -
        // and query time versus post-query work are the two halves that need different answers.
        var fetchMs = computeSw.ElapsedMilliseconds;

        // Extended (lead-in) series: used only to build the outage detector's trigger and hops below.
        var ispSeriesExt = ToSamples(await ispSeriesTask);
        var internetSeriesExt = ToSamples(await internetSeriesTask);
        // Window-trimmed series for every other consumer (scoring, congestion, loss pool, charts),
        // identical to what a plain [windowStart, windowEnd] query returned before the reach-back.
        static Dictionary<string, List<LatencySample>> TrimFrom(Dictionary<string, List<LatencySample>> src, DateTime from) =>
            src.ToDictionary(kv => kv.Key, kv => kv.Value.Where(s => s.Time >= from).ToList());
        var ispSeries = TrimFrom(ispSeriesExt, windowStart);
        var transitSeries = ToSamples(await transitSeriesTask);
        var internetSeries = TrimFrom(internetSeriesExt, windowStart);
        var customSeries = ToSamples(await customSeriesTask);
        var wanRates = await ratesTask;
        var (expectedDown, expectedUp, expectedSource, smartQueuesEnabled, scoredWan) = await speedsTask;

        // A counter reset at a link flap reports a rate the line cannot carry. Left in, one such
        // sample marks its window loaded and drags the flap's own loss into Loaded Loss.
        var sanitized = WanRateSanitizer.Filter(wanRates, expectedDown, expectedUp, _options);
        if (sanitized.Dropped > 0)
            _logger.LogInformation(
                "ISP Health: discarded {Dropped} of {Total} WAN rate sample(s) above {Multiple}x the {Down}/{Up} Mbps plan (counter artifacts)",
                sanitized.Dropped, wanRates.Count, _options.WanRateImplausibleMultiple, expectedDown, expectedUp);
        wanRates = sanitized.Samples;
        var wanSpeedTests = await speedTestsTask;
        // Gateway samples feed only the outage waterfall's gateway hop, so they carry the full extended
        // window (the detector clips each hop's series to the detected event span anyway).
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

        // Windows where a transit target sat at total loss for minutes: a routing/BGP change
        // (the hop left the forwarding path), not access-layer loss. Carved out of THAT
        // target's loss-pool contribution below - every other access/transit/DNS series keeps
        // feeding the pool through the same span, so pooled loss stays usable on paths with
        // several upstream ASNs - and surfaced as path events after outage detection. The
        // ASN's own grade uses the unfiltered grading series, so it still takes the hit.
        // Both rules: cleanly withdrawn (every sample dark for minutes) and flapping (mostly dark for
        // longer, answering the odd probe). Overlapping windows are harmless - masking is a range test.
        var transitDarkWindows = transitTargets
            .Where(t => transitSeries.ContainsKey(t.TargetId))
            .SelectMany(t => TransitUnreachableDetector.Detect(
                    t.TargetId, t.AsnNumber ?? 0, AsnNameCleanup.Clean(t.AsnName), transitSeries[t.TargetId], _options)
                .Concat(TransitUnreachableDetector.DetectMostlyDark(
                    t.TargetId, t.AsnNumber ?? 0, AsnNameCleanup.Clean(t.AsnName), transitSeries[t.TargetId], _options)))
            .ToList();
        // The same rule for access-ISP hops. When a network withdraws a route its own hops go dark
        // with it, and those are AccessIsp targets - so the carve-out that spared the transit
        // targets left their siblings inside the very same ASN pouring 100% loss into the pool,
        // while the timeline told the user the event was "excluded from the Packet Loss factor".
        // It is routing either side of the boundary; which target type happens to sit on the dark
        // hop does not change what caused it.
        var ispDarkWindows = ispTargets
            .Where(t => ispSeries.ContainsKey(t.TargetId))
            .SelectMany(t => TransitUnreachableDetector.Detect(
                    t.TargetId, t.AsnNumber ?? 0, AsnNameCleanup.Clean(t.AsnName), ispSeries[t.TargetId], _options)
                .Concat(TransitUnreachableDetector.DetectMostlyDark(
                    t.TargetId, t.AsnNumber ?? 0, AsnNameCleanup.Clean(t.AsnName), ispSeries[t.TargetId], _options)))
            .ToList();
        var darkWindows = transitDarkWindows.Concat(ispDarkWindows).ToList();
        var darkByTargetId = darkWindows
            .GroupBy(w => w.TargetId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Hops with a discovery row but HopNumber 0 answered pings yet never landed in a trace
        // (OLT/CMTS ICMP-deprioritization); only meaningful once there is trace data at all.
        var notTracedTargetIds = hopOrderKnown
            ? hopNumberByTargetId.Where(kv => kv.Value == 0).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Loss pool: ALL enabled AccessIsp + Transit targets plus well-known anycast DNS.
        // Every probe crosses the access link before reaching its target, so loss on ANY
        // of these is a signal of access-layer loss - including under load, where the
        // question is "did the saturated access link drop packets", not "did transit drop
        // because of my load" (it won't). Pooling many targets gives a denser, more robust
        // access-loss signal than one sparse hop. (Latency, by contrast, uses only the
        // nearest hop because far-hop RTT carries transit variance that isn't the access
        // link's loaded behavior - see PickFirstCleanHop / spec "Measurement sources".)
        // Transit series enter minus their unreachable windows (see transitDarkWindows above).
        // Built WITH target ids so flat-lined members can be identified before the ids are dropped:
        // the scorer's pool is anonymous (LatencySample carries no target), so this is the last point
        // where a target can be named.
        var identifiedPool = new List<LossPoolFilter.PoolEntry>();
        // Access hops that answer pings but sit on no traced path are excluded outright. Nothing
        // of yours crosses them, so their loss is not loss you suffered - it is a box beside the
        // road dropping the probes aimed at it. Their jitter was already discounted for exactly
        // this reason; the same logic was never carried over to loss, and one ICMP-deprioritized
        // OLT answering badly could hold the pooled figure up on its own.
        // ...unless they are ALL that this site has. An off-path OLT is weak evidence, but it is
        // the only access-layer member available on a network with nothing else pingable in front
        // of transit, and dropping it would leave access-layer loss measured entirely by hops
        // beyond the access network. Weak evidence in the right place beats none.
        var ispWithSeries = ispTargets.Where(t => ispSeries.ContainsKey(t.TargetId)).ToList();
        var onPathIsp = ispWithSeries.Where(t => !notTracedTargetIds.Contains(t.TargetId)).ToList();
        var ispForPool = onPathIsp.Count > 0 ? onPathIsp : ispWithSeries;

        var offPathIsp = ispWithSeries.Except(ispForPool).ToList();
        if (offPathIsp.Count > 0)
            _logger.LogDebug(
                "ISP Health: excluding {Count} off-path access hop(s) from the loss pool: {Targets}",
                offPathIsp.Count, string.Join(", ", offPathIsp.Select(t => t.Address)));
        else if (onPathIsp.Count == 0 && ispWithSeries.Count > 0)
            _logger.LogDebug(
                "ISP Health: keeping {Count} off-path access hop(s) in the loss pool - the site has no on-path access hop",
                ispWithSeries.Count);

        identifiedPool.AddRange(ispForPool
            .Select(t => new LossPoolFilter.PoolEntry(t.TargetId,
                darkByTargetId.TryGetValue(t.TargetId, out var ispDark)
                    ? ispSeries[t.TargetId].Where(s => !ispDark.Any(w => s.Time >= w.Start && s.Time <= w.End)).ToList()
                    : ispSeries[t.TargetId])));
        identifiedPool.AddRange(transitTargets.Where(t => transitSeries.ContainsKey(t.TargetId)).Select(t =>
            new LossPoolFilter.PoolEntry(t.TargetId,
                darkByTargetId.TryGetValue(t.TargetId, out var dark)
                    ? transitSeries[t.TargetId].Where(s => !dark.Any(w => s.Time >= w.Start && s.Time <= w.End)).ToList()
                    : transitSeries[t.TargetId])));
        // Anycast DNS goes in RAW, deliberately - no unreachable carve-out. Those addresses are
        // served from everywhere at once and effectively never have an outage of their own, so a
        // resolver going dark is the ISP failing to reach it, which is exactly the loss this pool
        // exists to catch. Carving it out for symmetry with the hops above would delete the
        // clearest outage signal there is.
        identifiedPool.AddRange(targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService
                && AnycastDnsIps.Contains(t.Address)
                && internetSeries.ContainsKey(t.TargetId))
            .Select(t => new LossPoolFilter.PoolEntry(t.TargetId, internetSeries[t.TargetId])));

        // A target dark for the whole window while its peers keep measuring is blocked or retired, not
        // losing; its constant 100% would swamp the pooled mean both loss factors are graded on.
        var flatlined = LossPoolFilter.FindFlatlined(identifiedPool, _options);
        if (flatlined.Count > 0)
            _logger.LogInformation("ISP Health: excluding {Count} flat-lined target(s) from the loss pool: {Targets}",
                flatlined.Count, string.Join(", ", flatlined));
        var lossPool = identifiedPool
            .Where(e => !flatlined.Contains(e.TargetId))
            .Select(e => e.Samples.ToList())
            .ToList();

        var trimAndMaskMs = computeSw.ElapsedMilliseconds - fetchMs;
        var (ispGrading, transitGrading, allClusters, ispChart, transitChart) = BuildAsnSeriesSets(ispTargets, transitTargets, ispSeries, transitSeries, ancestorIpsByTargetId);
        var asnBuildMs = computeSw.ElapsedMilliseconds - fetchMs - trimAndMaskMs;
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

        // User-added Custom targets (e.g. a known-stable CMTS/PoP ping) as destination witnesses:
        // a clean end-to-end reading absolves the ISP/transit hops in its traced ancestry (strict
        // routes-through gate). Witness-only - kept out of charts, loss pool, and localizer.
        var customWitnessSeries = targets
            .Where(t => t.TargetType == MonitoringTargetType.Custom && customSeries.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = customSeries[t.TargetId],
                HopIps = { t.Address },
                AncestorIps = ancestorIpsByTargetId.TryGetValue(t.TargetId, out var custAnc) ? custAnc : new List<string>(),
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
            L2NeighborIps = new HashSet<string>(
                targets.Where(t => t.DiscoveryMethod == DiscoveryMethod.L2Neighbor && !string.IsNullOrEmpty(t.Address))
                    .Select(t => t.Address),
                StringComparer.OrdinalIgnoreCase),
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
        var detectorsStartMs = computeSw.ElapsedMilliseconds;
        var pathShifts = StepChangeDetector.Detect(stepInput, _options);

        // Outage detection: the internet targets going dark defines an outage; every hop is
        // carried (ordered nearest-first by the hop map, RTT tiebreaker) to shape it and
        // attribute the break. A monitoring gap has no samples and so is never flagged. The
        // trigger keeps ALL internet targets (robust detection); only the waterfall's internet
        // rows are trimmed to the two canonical resolvers below. The trigger reads the extended
        // (lead-in) internet series so an outage straddling the window start is triggered from its
        // true onset - the reach-back that keeps a clipped LAN/Gateway outage from surfacing as a
        // path-wide ISP blip. Post-detection the events are filtered back to the window.
        var internetTriggerTargets = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeriesExt.ContainsKey(t.TargetId))
            .Select(t => (IReadOnlyList<LatencySample>)internetSeriesExt[t.TargetId])
            .ToList();
        int ClusterHopNumber(AsnSeries s) => s.TargetIds
            .Select(tid => hopNumberByTargetId.TryGetValue(tid, out var hn) ? hn : int.MaxValue)
            .DefaultIfEmpty(int.MaxValue).Min();
        double MedianRtt(AsnSeries s) => SeriesStats.Median(
            s.Samples.Where(x => x.RttAvgMs.HasValue).Select(x => x.RttAvgMs!.Value).ToList()) ?? double.MaxValue;
        // The two internet rows for the waterfall: prefer Cloudflare/Google, but if the user
        // doesn't monitor them, fall back to the two nearest other internet targets so the
        // waterfall still shows an internet-reachability row. Built on the extended (lead-in)
        // internet series - like the trigger, gateway, and access rows - so a straddling outage's
        // waterfall spans its true onset rather than just the in-window tail.
        var internetDisplaySources = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService && internetSeriesExt.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = internetSeriesExt[t.TargetId],
                HopIps = { t.Address }
            })
            .ToList();
        var displayInternet = internetDisplaySources
            .Where(s => s.HopIps.Any(ip => OutageInternetIps.Contains(ip)))
            .Concat(internetDisplaySources
                .Where(s => !s.HopIps.Any(ip => OutageInternetIps.Contains(ip)))
                .OrderBy(MedianRtt))
            .Take(2)
            .ToList();
        // Every internet destination on the WAN being graded - the partial pass's breadth evidence.
        // The target list is already scoped to this WAN (ScopeTargetsToWan: this WAN's rows, plus
        // null-WanInterface hand-added rows for the primary only), so no per-row WAN check remains -
        // a failover link's destinations can't manufacture breadth here because they never enter
        // `targets` at all.
        var breadthInternet = targets
            .Where(t => t.TargetType == MonitoringTargetType.InternetService
                && internetSeriesExt.ContainsKey(t.TargetId))
            .Select(t => new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = internetSeriesExt[t.TargetId],
                HopIps = { t.Address }
            })
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
        var accessAndTransitSources = ispTargets
            .Where(t => ispSeriesExt.ContainsKey(t.TargetId))
            .Select(t => (Series: new AsnSeries
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.Name,
                TargetIds = { t.TargetId },
                Samples = ispSeriesExt[t.TargetId],
                HopIps = { t.Address }
            }, Groupable: true, AsnLabel: AsnNameCleanup.Clean(t.AsnName) ?? accessAsnName, IsInternet: false))
            .Concat(transitChart.Select(s => (Series: s, Groupable: false, AsnLabel: (string?)TransitLabel(s), IsInternet: false)))
            .ToList();
        (AsnSeries Series, bool Groupable, string? AsnLabel, bool IsInternet) AsInternetRow(AsnSeries s) =>
            (Series: s, Groupable: false, AsnLabel: (string?)null, IsInternet: true);
        var outageSources = accessAndTransitSources.Concat(displayInternet.Select(AsInternetRow));
        // Same rows, but with EVERY internet destination instead of the two display ones - the
        // partial pass's breadth evidence (see below).
        var partialSources = accessAndTransitSources.Concat(breadthInternet.Select(AsInternetRow));
        // The LAN gateway is the nearest hop (Depth 0) when monitored; WAN hops shift one deeper.
        // Its loss lets the detector tell a LAN/gateway outage from a WAN outage. Absent => unchanged.
        var gatewayHop = gatewaySamples.Count > 0
            ? new OutageDetector.Hop(gatewayDevice?.Name is { Length: > 0 } gn ? gn : "Gateway",
                0, gatewaySamples, Groupable: false, AsnLabel: null, IsGateway: true)
            : null;
        var baseDepth = gatewayHop != null ? 1 : 0;
        // Both hop lists share every access and transit row, and the sort keys are pure functions of
        // the series - MedianRtt copies and sorts each one's RTTs, which is not free on a long
        // window and the compute already runs against a time budget. Derive each series' keys once.
        var sortKeys = new Dictionary<AsnSeries, (int HopNumber, double Rtt)>();
        (int HopNumber, double Rtt) SortKey(AsnSeries s)
        {
            if (!sortKeys.TryGetValue(s, out var k))
                sortKeys[s] = k = (ClusterHopNumber(s), MedianRtt(s));
            return k;
        }
        List<OutageDetector.Hop> BuildHops(IEnumerable<(AsnSeries Series, bool Groupable, string? AsnLabel, bool IsInternet)> sources)
        {
            var ordered = sources
                .Select(x => new
                {
                    x.Groupable,
                    x.AsnLabel,
                    x.IsInternet,
                    x.Series.AsnNumber,
                    Name = x.Series.AsnName ?? x.Series.TargetIds.FirstOrDefault() ?? "hop",
                    Series = (IReadOnlyList<LatencySample>)x.Series.Samples,
                    HopNumber = SortKey(x.Series).HopNumber,
                    Rtt = SortKey(x.Series).Rtt
                })
                .OrderBy(x => x.HopNumber).ThenBy(x => x.Rtt)
                .ToList();
            // Rows without a persisted hop number sorted to the end above - that Depth is a sort
            // position, not a path position, so they carry KnownPosition: false and never anchor
            // the detector's "break upstream of X" attribution (e.g. a hostname-based ISP target
            // the discovery traces never mapped). Internet endpoint rows are likewise flagged so
            // a destination can never be named as the hop the break sat beyond.
            return (gatewayHop != null ? new[] { gatewayHop } : Array.Empty<OutageDetector.Hop>())
                .Concat(ordered.Select((x, i) =>
                    new OutageDetector.Hop(x.Name, baseDepth + i, x.Series, x.Groupable, x.AsnLabel,
                        KnownPosition: x.HopNumber != int.MaxValue, IsInternet: x.IsInternet,
                        AsnNumber: x.AsnNumber)))
                .ToList();
        }
        var outageHops = BuildHops(outageSources);
        ct.ThrowIfCancellationRequested();
        var outages = OutageDetector.Detect(internetTriggerTargets, outageHops, _options);
        // Second pass: coincident partial-loss disruptions (the path getting lossy but not dark)
        // across the full set of monitored hops, excluding windows already flagged as blackouts.
        // Unlike the blackout pass - whose trigger is every internet target and whose HOPS are only
        // the shape - the partial pass reads breadth off the hop list itself, so it must see every
        // internet destination or the gate is judged on two rows. Trimming to the display pair let
        // an identical event trip at one site and not another purely because the first happened to
        // monitor one more transit hop (so its ASN split into one more RTT cluster).
        var partialDisruptions = OutageDetector.DetectPartial(
            BuildHops(partialSources), outages.Select(o => (o.Start, o.End)).ToList(), _options);
        outages = outages.Concat(partialDisruptions).OrderBy(o => o.Start).ToList();
        // Drop events that ended at or before the window start: the lead-in reach-back only exists to
        // capture an outage that STRADDLES the window start (its recovery is in-window), so an event
        // sitting entirely in the pre-window lead-in is outside this report and must not surface.
        // Straddling events keep their true onset (Start may precede windowStart) so ack-matching and
        // the LAN/Gateway attribution both read off the full shape, exactly as the 7-day view does.
        outages = outages.Where(o => o.End > windowStart).ToList();

        // Surface the transit-unreachable windows as path events, merged per ASN - unless the
        // span mostly sat inside a blackout outage, where the whole path was dark and the
        // outage machinery already owns the story (its window is masked from the loss factors
        // anyway). Informational like RTT-step shifts; the score effect is the loss-pool
        // carve-out above, never the event itself.
        var blackoutSpans = outages.Where(o => !o.IsPartial).Select(o => (o.Start, o.End)).ToList();
        double OverlapSeconds(DateTime s, DateTime e) => blackoutSpans.Sum(b =>
            Math.Max(0, (new DateTime(Math.Min(e.Ticks, b.End.Ticks)) - new DateTime(Math.Max(s.Ticks, b.Start.Ticks))).TotalSeconds));
        var unreachableEvents = TransitUnreachableDetector.MergeByAsn(darkWindows, _options)
            .Where(e => OverlapSeconds(e.Start, e.End) < (e.End - e.Start).TotalSeconds * 0.5)
            .Select(e => new PathShiftEvent
            {
                Time = e.Start,
                AsnNumber = e.AsnNumber > 0 ? e.AsnNumber : null,
                AsnName = e.AsnName,
                IsUnreachable = true,
                UnreachableEnd = e.End,
                CorrelatedTargetCount = e.TargetCount
            })
            .ToList();
        if (unreachableEvents.Count > 0)
        {
            foreach (var e in unreachableEvents)
                _logger.LogDebug("ISP Health: transit unreachable {Asn} {Start:HH:mm:ss} - {End:HH:mm:ss} ({Targets} target(s)); loss excluded from the access-layer pool",
                    e.AsnName ?? $"AS{e.AsnNumber}", e.Time, e.UnreachableEnd, e.CorrelatedTargetCount);
            pathShifts = pathShifts.Concat(unreachableEvents).OrderBy(p => p.Time).ToList();
        }

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

        // Stamp the user's "that was me" acknowledgements onto the detected events so the
        // scorer leaves them out of the penalty and the findings.
        if (ackedOutageStarts.Count > 0)
        {
            var ackTolerance = TimeSpan.FromSeconds(_options.OutageAckMatchToleranceSeconds);
            foreach (var o in outages)
                o.Acknowledged = ackedOutageStarts.Any(a => (a - o.Start).Duration() <= ackTolerance);
        }

        // chartClusters (one line per cluster) is the chart view computed from the same
        // snapshot the detectors ran on, so deeper-cluster "+N ms hop" labels still match
        // event labels. It is published together with the report (see Snapshot).
        // SQM probe exclusions and the Adaptive SQM flag key off the SCORED WAN's own
        // data-path interface (SqmWanConfigurations rows are per interface).
        var scoredDataPathInterface = await GetScoredWanDataPathInterfaceAsync(ct);
        var loadExclusions = await BuildSqmProbeExclusionsAsync(windowStart, windowEnd, scoredDataPathInterface, ct);
        var adaptiveSqmEnabled = await IsAdaptiveSqmEnabledAsync(scoredDataPathInterface, ct);

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
            TargetAddresses = await ResolveHopAddressesAsync(ispTargets, ct),
            LossPoolSeries = lossPool,
            LossPoolExcludedTargetIds = flatlined,
            GatewayLossSeries = gatewaySamples,
            TransitAsnSeries = transitGrading,
            IspAsnSeries = ispGrading,
            DestinationSeries = internetTargetSeries,
            WitnessSeries = customWitnessSeries,
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
            // Hops with a discovery row but HopNumber 0 answered pings yet never landed in a trace
            // (OLT/CMTS ICMP-deprioritization); only meaningful once we have trace data at all.
            NotTracedTargetIds = notTracedTargetIds,
            LoadExclusionWindows = loadExclusions,
            PhysicalLink = physical.Input
        };

        ct.ThrowIfCancellationRequested();
        var detectorsMs = computeSw.ElapsedMilliseconds - detectorsStartMs;
        var scoreStartMs = computeSw.ElapsedMilliseconds;
        var report = new IspHealthScorer(_options, _logger).Score(inputs, profile);
        var scoreMs = computeSw.ElapsedMilliseconds - scoreStartMs;
        report.AccessTechnology = technology;
        report.PhysicalLinkCandidates = physical.Candidates;
        report.PhysicalLinkSelectedKey = physical.SelectedKey;
        report.PhysicalLinkMedium = physical.Input?.Medium;
        report.PhysicalLinkAmbiguous = physical.Ambiguous;
        report.WanName = scoredWan?.Name;
        report.WanNetworkGroup = scoredWan?.NetworkGroup;
        report.WanInterface = scoredWan?.Interface;
        report.PppoeSession = pppoeSession == true;
        _logger.LogDebug("ISP Health computed: {Score} ({Tech}), {Events} congestion events, {Shifts} path shifts",
            report.OverallScore, profile.DisplayName, congestionEvents.Count, pathShifts.Count);
        _logger.LogDebug(
            "ISP Health compute timing: {Hours}h in {Ms}ms = setup {Setup} + query {Query} + trim/mask {Trim} + asn {Asn} + detect {Detect} + score {Score} + other {Other}; {Rates} rates, {LatencyPoints} latency points, {LossSeries} loss series",
            (windowEnd - windowStart).TotalHours.ToString("0.#"), computeSw.ElapsedMilliseconds,
            setupMs, fetchMs - setupMs, trimAndMaskMs, asnBuildMs, detectorsMs, scoreMs,
            computeSw.ElapsedMilliseconds - fetchMs - trimAndMaskMs - asnBuildMs - detectorsMs - scoreMs,
            wanRates.Count,
            ispSeries.Sum(kv => kv.Value.Count) + transitSeries.Sum(kv => kv.Value.Count)
                + internetSeries.Sum(kv => kv.Value.Count),
            lossPool.Count);
        return new ComputeOutcome(IspHealthStatus.Ready, report, chartClusters);
    }

    /// <summary>
    /// Whether the scored WAN carries its traffic over a PPPoE session, read from that WAN's
    /// data-path interface name (uplink_ifname) - "ppp0" is a PPPoE session and nothing else.
    /// Cheap: the underlying device call is already cached.
    ///
    /// Three-valued on purpose: false means we read an interface and it is not PPPoE, null means we
    /// could not tell. Neither default is safe - assuming PPPoE grades an ordinary line too
    /// leniently, assuming no PPPoE grades a real one too harshly. The resolver signals its
    /// failures by returning null rather than throwing, so the caller has to handle it explicitly.
    /// Warning-level because the answer is cached in the report for CacheTtl.
    /// </summary>
    private async Task<bool?> IsPppoeWanAsync(CancellationToken ct)
    {
        try
        {
            // Through the resolver, not the console directly: PPPoE is read off the interface NAME,
            // and the remembered profile holds that name, so an offline site keeps its overlay
            // instead of silently grading a PPPoE line against its medium's raw thresholds.
            var dataPath = await GetScoredWanDataPathInterfaceAsync(ct);
            if (string.IsNullOrEmpty(dataPath))
            {
                _logger.LogWarning("ISP Health could not resolve the scored WAN's data-path interface; " +
                    "scoring without the PPPoE overlay, so a PPPoE line will grade against its medium's " +
                    "unadjusted thresholds until the next recompute");
                return null;
            }

            var isPppoe = NetworkUtilities.IsPppoeInterface(dataPath);
            _logger.LogDebug("ISP Health: scored WAN data-path interface is {Interface}; PPPoE overlay {Applied}",
                dataPath, isPppoe ? "applied" : "not applicable");
            return isPppoe;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ISP Health could not resolve the scored WAN's data-path interface; " +
                "scoring without the PPPoE overlay until the next recompute");
            return null;
        }
    }

    /// <summary>
    /// Scoping helpers, static and internal so the single-WAN equivalence tests exercise the
    /// exact predicates the compute uses.
    /// </summary>
    /// <remarks>
    /// Rows with no WAN stamped (hand-added targets, rows predating per-WAN discovery) belong to
    /// the primary: they were always primary-path measurements, and dropping them would shrink
    /// the pool on exactly the installs that curated it. A scoped WAN owns only rows stamped
    /// with its own key.
    /// </remarks>
    internal static List<MonitoringTarget> ScopeTargetsToWan(
        List<MonitoringTarget> targets, string wanKey, bool includeUnassigned) =>
        // Keys normalized ("wan1" == "wan"): legacy installs stamped rows with the wan1 alias,
        // and an unnormalized comparison would silently drop them from their own report.
        targets.Where(t => MonitoringTarget.IsUnpinned(t.WanInterface)
                ? includeUnassigned
                : string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(t.WanInterface!),
                    GatewayWanHelper.WanInterfaceKeyFromKey(wanKey), StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>
    /// The configured primary WAN's key from a resolved networkconf row ("WAN2" -> "wan2"), or
    /// null when there is none to read. Primary is a ROLE, not a name: any wanN group can be the
    /// configured primary (failover priority / load-balance weight decide), so this - never a
    /// name-ordered guess - is the authoritative answer while the console can be asked.
    /// </summary>
    internal static string? ConfiguredPrimaryWanKey(NetworkInfo? primary) =>
        string.IsNullOrEmpty(primary?.WanNetworkgroup)
            ? null : GatewayWanHelper.WanInterfaceKeyFromKey(primary!.WanNetworkgroup!);

    /// <summary>Configured primary key from the console; null when it cannot be asked.</summary>
    private async Task<string?> ResolveConfiguredPrimaryWanKeyAsync(CancellationToken ct)
    {
        try
        {
            return ConfiguredPrimaryWanKey(await _connectionService.GetPrimaryWanNetworkAsync(ct));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not resolve the configured primary WAN from the console");
            return null;
        }
    }

    /// <summary>
    /// LAST-RESORT GUESS at the primary's wan key, for when the console cannot say which WAN
    /// holds the primary role: the conventional "wan"-group discovery row first, then any row,
    /// defaulting to "wan" with no rows at all. This is wrong exactly on an offline multi-WAN
    /// site whose configured primary is another group (WAN2-primary with a WAN1 failover) -
    /// there is nothing better to ask offline, and the next connected compute corrects it.
    /// Callers must prefer <see cref="ConfiguredPrimaryWanKey"/> whenever the console answers.
    /// </summary>
    internal static string ResolvePrimaryWanKey(IEnumerable<WanDiscoveryContext> contexts) =>
        GatewayWanHelper.WanInterfaceKeyFromKey(contexts
            .OrderBy(c => string.Equals(
                GatewayWanHelper.WanInterfaceKeyFromKey(c.WanInterface ?? ""), "wan", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .Select(c => c.WanInterface)
            .FirstOrDefault(w => !string.IsNullOrEmpty(w)) ?? "wan");

    /// <summary>
    /// The Influx wan-tag scope for the WAN being scored. Primary: untagged points (every point
    /// the primary path has ever written), plus the tag values of any context bound to the
    /// primary WAN - so a primary probed through an explicit context keeps those points too.
    /// Scoped WAN: its stable wan key (what the writers tag new points with,
    /// WanContext.InfluxWanTag) plus its contexts' display names, which tagged the points
    /// written before the stable-key tagging landed. Never untagged - untagged is the primary's.
    /// </summary>
    internal static MonitoringInfluxClient.LatencyWanScope BuildWanScope(
        IEnumerable<WanContext> contexts, string wanKey, bool primaryScope)
    {
        // Context match is key-normalized ("wan1" == "wan"), but the TAG VALUES stay raw: points
        // were written with each context's literal InfluxWanTag, so a legacy wan1-keyed context
        // contributes the "wan1" tag its points actually carry. The scoped WAN's own normalized
        // key is added for points the writers tag going forward. Note the wanKey parameter is
        // whatever key the caller RESOLVED (configured primary or scoped key) - never a literal.
        var normalizedKey = GatewayWanHelper.WanInterfaceKeyFromKey(wanKey);
        var tags = contexts
            .Where(c => !string.IsNullOrEmpty(c.WanInterface) && string.Equals(
                GatewayWanHelper.WanInterfaceKeyFromKey(c.WanInterface!), normalizedKey, StringComparison.OrdinalIgnoreCase))
            .SelectMany(c => new[] { c.InfluxWanTag, c.Name })
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (primaryScope)
            return MonitoringInfluxClient.LatencyWanScope.Primary(tags);
        if (!tags.Contains(normalizedKey, StringComparer.Ordinal))
            tags.Insert(0, normalizedKey);
        return MonitoringInfluxClient.LatencyWanScope.ForWan(tags);
    }

    /// <summary>
    /// The scored WAN's data-path interface: the primary resolver for the primary instance
    /// (unchanged, incl. its remembered-profile offline fallback), the WAN's own uplink for a
    /// scoped instance - live from the console when connected, from the WAN's remembered
    /// profile row when not.
    /// </summary>
    private async Task<string?> GetScoredWanDataPathInterfaceAsync(CancellationToken ct)
    {
        if (_scopedWanKey == null)
            return await GetPrimaryWanInterfaceAsync(ct);

        var group = GatewayWanHelper.WanNetworkGroupFromKey(_scopedWanKey);
        try
        {
            var ifaces = await _connectionService.GetWanInterfacesForGroupAsync(group, ct);
            var live = ifaces?.UplinkIfName ?? ifaces?.PhysicalIfName;
            if (!string.IsNullOrEmpty(live)) return live;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read WAN {Group}'s data-path interface from the console", group);
        }
        if (_connectionService.IsConnected) return null;
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            return await db.WanProfiles.AsNoTracking()
                .Where(w => w.WanNetworkgroup == group && w.DataPathInterface != null)
                .OrderByDescending(w => w.UpdatedAt)
                .Select(w => w.DataPathInterface)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read WAN {Group}'s remembered data-path interface", group);
            return null;
        }
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

            var rates = await _influx.QueryGatewayWanRatesAsync(mac, ifNames, from, to, aggregate, ct: ct);
            return rates.Select(r => new ThroughputSample(r.Time, r.DownloadBps, r.UploadBps)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not query WAN rates");
            return new List<ThroughputSample>();
        }
    }

    /// <summary>
    /// Resolves the gateway MAC and the SCORED WAN's SNMP counter interface(s) - the same WAN as
    /// the expected speeds and SQM exclusion (e.g. "eth6" for a VLAN-tagged WAN). The pairing is
    /// the point: these counters are divided by this WAN's plan speeds, and that load figure sets
    /// the Packet Loss ceiling quadratically (ScorePacketLoss), splits loaded from idle samples
    /// (LoadClassifier), and drives congestion load-coincidence (CongestionTopology.Load) - so
    /// another WAN's counters here mis-grade all three at once.
    ///
    /// Primary instance: configured primary live, then the primary's remembered profile row
    /// (same WAN, cached), then the live active uplink as the last resort so analysis still
    /// runs - that last step is the one place bytes can come from a different WAN than the
    /// plan speeds, and it is logged as such. Scoped instance: that WAN's own counter interface
    /// (live, then its profile row) and NOTHING cross-WAN - no active-uplink, no WAN1 fallback.
    /// </summary>
    private async Task<(string? Mac, List<string>? IfNames)> ResolveWanCounterAsync(CancellationToken ct)
    {
        var devices = await _connectionService.GetDiscoveredDevicesAsync(ct);
        var gw = devices?.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway);

        if (_scopedWanKey != null)
        {
            var group = GatewayWanHelper.WanNetworkGroupFromKey(_scopedWanKey);
            string? counter = null;
            try
            {
                var ifaces = await _connectionService.GetWanInterfacesForGroupAsync(group, ct);
                counter = ifaces?.CounterIfName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ISP Health: could not resolve WAN {Group}'s counter interface from the console", group);
            }
            var mac = gw?.Mac;
            if (string.IsNullOrEmpty(counter) || string.IsNullOrEmpty(mac))
            {
                try
                {
                    await using var db = await CreateSiteDbAsync(ct);
                    var profile = await db.WanProfiles.AsNoTracking()
                        .Where(w => w.WanNetworkgroup == group)
                        .OrderByDescending(w => w.UpdatedAt)
                        .FirstOrDefaultAsync(ct);
                    counter = string.IsNullOrEmpty(counter) ? profile?.CounterInterface : counter;
                    mac = string.IsNullOrEmpty(mac) ? profile?.GatewayMac : mac;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "ISP Health: could not read WAN {Group}'s remembered counter interface", group);
                }
            }
            if (string.IsNullOrEmpty(mac) || string.IsNullOrEmpty(counter))
            {
                _logger.LogDebug("ISP Health: no counter interface resolved for WAN {Group}; load context is empty for this report", group);
                return (mac, null);
            }
            return (mac, new List<string> { counter! });
        }

        if (gw?.Mac == null)
            return (null, null);

        var primaryIfaces = await _connectionService.GetPrimaryWanInterfacesAsync(ct);
        var wanCounterNames = !string.IsNullOrEmpty(primaryIfaces?.CounterIfName)
            ? new List<string> { primaryIfaces!.CounterIfName! }
            : null;
        // Config-primary unresolved: prefer the primary's own remembered counter interface (same
        // WAN, merely cached) before the live active uplink - during a failover the active uplink
        // is ANOTHER WAN, and its bytes against the primary's plan speeds understate load, which
        // relaxes into the strictest idle loss ceiling. The active uplink stays as the very last
        // resort so a site that never resolved a primary still gets load context.
        if (wanCounterNames == null)
        {
            try
            {
                // Prefer the CONFIGURED primary group's remembered row when the console can
                // still say which group holds the primary role; the first-by-group-name pick is
                // the last resort and is a documented GUESS - on a WAN2-primary site with a WAN1
                // failover row it returns the failover's counter. Nothing better exists offline
                // (WanProfile carries no primary marker); the next connected read corrects it.
                var cfgGroup = await ResolveConfiguredPrimaryWanKeyAsync(ct) is { } cfgKey
                    ? GatewayWanHelper.WanNetworkGroupFromKey(cfgKey) : null;
                await using var db = await CreateSiteDbAsync(ct);
                var remembered = await db.WanProfiles.AsNoTracking()
                    .Where(w => w.CounterInterface != null && (cfgGroup == null || w.WanNetworkgroup == cfgGroup))
                    .OrderBy(w => w.WanNetworkgroup)
                    .ThenByDescending(w => w.UpdatedAt)
                    .Select(w => w.CounterInterface)
                    .FirstOrDefaultAsync(ct);
                if (!string.IsNullOrEmpty(remembered))
                {
                    _logger.LogDebug("ISP Health: primary WAN unresolved, using its remembered counter interface {Iface}", remembered);
                    wanCounterNames = new List<string> { remembered! };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "ISP Health: could not read the remembered primary counter interface");
            }
        }
        wanCounterNames ??= gw.WanInterfaceNames;
        if (wanCounterNames == null || wanCounterNames.Count == 0)
        {
            _logger.LogDebug("ISP Health: no WAN counter interface resolved");
            return (gw.Mac, null);
        }
        if (primaryIfaces?.CounterIfName == null && ReferenceEquals(wanCounterNames, gw.WanInterfaceNames))
            _logger.LogDebug("ISP Health: primary WAN unresolved, falling back to active uplink {Ifaces} - " +
                "during a failover these are another WAN's counters paired with the primary's plan speeds",
                string.Join(",", wanCounterNames));
        return (gw.Mac, wanCounterNames);
    }

    /// <summary>
    /// The scored WAN's counter pairing (gateway MAC + counter interface names) for callers
    /// outside the scoring pipeline - the Investigate loaded-loss lookup and the WAN traffic
    /// reference - so they classify loaded-vs-idle against the same WAN whose latency they show.
    /// </summary>
    public async Task<(string? GatewayMac, List<string> CounterIfNames)> GetWanCounterInterfacesAsync(CancellationToken ct = default)
    {
        try
        {
            var (mac, ifNames) = await ResolveWanCounterAsync(ct);
            return (mac, ifNames ?? new List<string>());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not resolve the scored WAN's counter interfaces");
            return (null, new List<string>());
        }
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
            // ALL WANs, deliberately - the one input that widens across WANs. The fingerprint asks
            // "was the user doing anything in this hour", not "how loaded is the link being graded":
            // an hour carried by a secondary WAN is still an hour the user was active, so it must
            // not read idle and soften that hour's outage weighting. Identical across the per-WAN
            // instances by construction. The summed multi-interface read is opted into explicitly
            // (see QueryGatewayWanRatesAsync's contract); with one WAN the list has one name and
            // the query is byte-identical to before.
            var (mac, ifNames) = await ResolveAllWanCounterInterfacesAsync(ct);
            if (mac == null || ifNames.Count == 0) return null;

            var from = windowEnd.AddDays(-_options.UsageFingerprintLookbackDays);
            // Active usage is sustained (streaming, calls, uploads); a 5-min mean is plenty to catch
            // it and keeps the lookback series small.
            var rates = await _influx.QueryGatewayWanRatesAsync(mac, ifNames, from, windowEnd, TimeSpan.FromMinutes(5),
                sumAcrossInterfaces: true, ct: ct);
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

    /// <summary>
    /// Gateway MAC plus EVERY WAN's counter interface, for the all-WAN usage fingerprint only
    /// (see the summing contract on QueryGatewayWanRatesAsync). Live enumeration when the
    /// console answers, augmented by the remembered per-WAN profile rows so WANs the console
    /// currently omits (down, disabled) still contribute their recorded usage.
    /// </summary>
    private async Task<(string? Mac, List<string> IfNames)> ResolveAllWanCounterInterfacesAsync(CancellationToken ct)
    {
        string? mac = null;
        var names = new List<string>();
        try
        {
            var devices = await _connectionService.GetDiscoveredDevicesAsync(ct);
            mac = devices?.FirstOrDefault(d => d.Type == DeviceType.Gateway || d.HardwareType == DeviceType.Gateway)?.Mac;
            foreach (var wan in await _connectionService.GetAllWanInterfacesAsync(ct))
                if (!string.IsNullOrEmpty(wan.CounterIfName))
                    names.Add(wan.CounterIfName!);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health: could not enumerate WAN counter interfaces from the console");
        }
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            var profiles = await db.WanProfiles.AsNoTracking()
                .Where(w => w.CounterInterface != null)
                .ToListAsync(ct);
            foreach (var p in profiles)
                names.Add(p.CounterInterface!);
            mac ??= profiles.Select(p => p.GatewayMac).FirstOrDefault(m => !string.IsNullOrEmpty(m));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health: could not read the remembered WAN profiles for the usage fingerprint");
        }
        return (mac, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Expected plan speeds for callers outside the scoring pipeline (e.g. loaded-loss investigation).</summary>
    public async Task<(double? DownMbps, double? UpMbps)> GetExpectedWanSpeedsAsync(CancellationToken ct = default)
    {
        var (down, up, _, _, _) = await ResolveExpectedSpeedsAsync(ct);
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
        // Same WAN scope as the compute (ScopeTargetsToWan there), so the Investigate highlight
        // averages exactly the pool this instance's score is graded on - configured primary
        // first, name-ordered guess only offline, like the compute.
        var scoredKey = _scopedWanKey
            ?? await ResolveConfiguredPrimaryWanKeyAsync(ct)
            ?? ResolvePrimaryWanKey(await db.WanDiscoveryContexts.AsNoTracking().ToListAsync(ct));
        targets = ScopeTargetsToWan(targets, scoredKey, includeUnassigned: _scopedWanKey == null);
        // Flat-lined targets the last computed report dropped come out here too. Subtracting from the
        // report rather than re-deriving it keeps this the single definition: the exclusion is a
        // measurement judgment and this method only reads the database, so it cannot make it itself.
        var excluded = _cached?.Report.LossPoolExcludedTargetIds ?? Array.Empty<string>();
        return targets
            .Where(t => t.TargetType == MonitoringTargetType.AccessIsp
                || (t.TargetType == MonitoringTargetType.Transit
                    && !(t.AsnNumber is int a && WellKnownAsns.NonTransitInfrastructure.Contains(a)))
                || (t.TargetType == MonitoringTargetType.InternetService && AnycastDnsIps.Contains(t.Address)))
            .Select(t => t.TargetId)
            .Where(id => !excluded.Contains(id))
            .ToList();
    }

    /// <summary>Which WAN the report scored, as the console names it. Display only.</summary>
    private record WanIdentity(string? Name, string? NetworkGroup, string? Interface);

    /// <summary>
    /// Expected speeds are configured values, never measured: the UniFi WAN provider
    /// capabilities (ISP speeds the user set in UniFi Network) with the Adaptive SQM
    /// nominal speeds as fallback. The resolved WAN's identity rides along, since this is
    /// already where the primary WAN is picked and the report labels which link it graded.
    /// </summary>
    private async Task<(double? Down, double? Up, string? Source, bool SmartQueues, WanIdentity? Wan)> ResolveExpectedSpeedsAsync(CancellationToken ct)
    {
        double? down = null, up = null;
        string? source = null;
        var smartQueues = false;
        WanIdentity? wan = null;
        // A scoped instance reads ITS WAN's networkconf row; the primary instance keeps the
        // configured-primary resolution unchanged. There is deliberately no cross-WAN fallback
        // anywhere below: a WAN whose plan the console never reported ends unscored on Speed vs
        // Plan rather than graded against another WAN's plan.
        var scopedGroup = _scopedWanKey == null ? null : GatewayWanHelper.WanNetworkGroupFromKey(_scopedWanKey);
        try
        {
            var networks = await _connectionService.GetNetworksAsync(ct);
            var net = scopedGroup == null
                ? UniFiConnectionService.ResolvePrimaryWanNetwork(networks, _logger)
                : networks.FirstOrDefault(n => n.IsWan && n.Enabled
                    && string.Equals(n.WanNetworkgroup, scopedGroup, StringComparison.OrdinalIgnoreCase));
            if (net != null)
            {
                if (net.WanDownloadMbps > 0) down = net.WanDownloadMbps;
                if (net.WanUploadMbps > 0) up = net.WanUploadMbps;
                if (down != null || up != null) source = "UniFi Network";
                smartQueues = net.WanSmartqEnabled;
                wan = new WanIdentity(net.Name, net.WanNetworkgroup, net.WanIfname);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ISP Health could not read UniFi WAN provider capabilities");
        }

        // Remember what the console said, per WAN. This is what lets a site whose console has gone
        // away still be graded, and it is stored per WAN because plan speeds belong to a WAN:
        // every scored WAN writes its own row here, keyed by WanNetworkgroup.
        if (wan?.NetworkGroup is { Length: > 0 })
            await RememberWanSpeedsAsync(wan, down, up, ct);

        if (down == null || up == null)
        {
            await using var db = await CreateSiteDbAsync(ct);

            // What the console told us before it went away. This ranks ABOVE the Adaptive SQM
            // figure: it is a reading from the authoritative source that is merely out of date,
            // where the SQM value is a shaping target someone typed in - what to rate-limit to,
            // not what the ISP confirmed the line does.
            //
            // Primary with no console: we cannot ask which WAN holds the primary ROLE (WanProfile
            // carries no primary marker), so the first-by-group-name row is a documented GUESS -
            // on a WAN2-primary site whose WAN1 failover also has a remembered row, it grades
            // against the failover's plan until the console comes back. A scoped instance reads
            // exactly its own WAN's row - another WAN's row is never an answer.
            var remembered = await db.WanProfiles.AsNoTracking()
                .Where(w => scopedGroup == null || w.WanNetworkgroup == scopedGroup)
                .OrderBy(w => w.WanNetworkgroup)
                .ThenByDescending(w => w.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (remembered != null)
            {
                down ??= remembered.DownloadMbps;
                up ??= remembered.UploadMbps;
                if (down != null || up != null)
                    source ??= "UniFi Network (last known)";
                wan ??= new WanIdentity(remembered.Name, remembered.WanNetworkgroup, remembered.CounterInterface);
            }

            // Truly inferred, so it goes last: only reached when the console has never told us.
            // The primary keeps the lowest-numbered row (unchanged); a scoped WAN matches its
            // own WAN number and otherwise stays unscored.
            if (down == null || up == null)
            {
                var scopedWanNumber = _scopedWanKey == null ? 0 : GatewayWanHelper.WanIndexFromKey(_scopedWanKey);
                var sqmWan = await db.SqmWanConfigurations.AsNoTracking()
                    .Where(c => scopedWanNumber == 0 || c.WanNumber == scopedWanNumber)
                    .OrderBy(c => c.WanNumber)
                    .FirstOrDefaultAsync(ct);
                if (sqmWan != null)
                {
                    down ??= sqmWan.NominalDownloadMbps;
                    up ??= sqmWan.NominalUploadMbps;
                    source ??= "Adaptive SQM settings";
                }
            }
        }
        return (down, up, source, smartQueues, wan);
    }

    /// <summary>
    /// Creates the vantage a policy route already justifies, and hands it the unpinned targets it
    /// describes.
    /// <para>
    /// Load-balancing sites only. There an unpinned probe took whichever WAN the balancer picked,
    /// so its readings belong to no WAN - unless the operator steered that box down one on the
    /// gateway, which is an attribution that exists and only we were missing. The route names a
    /// MAC, so the vantage binds to the box it actually pins rather than to a guess.
    /// </para>
    /// <para>
    /// Short-circuits once a vantage covers the pinned WAN, which makes it idempotent: the first
    /// run creates it and every later one finds it and stops. Best effort in every direction - any
    /// gap and the targets stay unpinned, which is still the truthful answer here. Caller saves.
    /// </para>
    /// </summary>
    private async Task CreateVantageForRoutedProbesAsync(
        NetworkOptimizerDbContext db, IReadOnlyList<NetworkInfo> networks, CancellationToken ct)
    {
        try
        {
            // Belt and braces on top of the load-balance flag: a site with one WAN has nothing to
            // balance across, so whatever that flag says, an unpinned probe there took the only
            // WAN there is. Never let a mis-resolved flag mint a vantage on a single-WAN site.
            var wanCount = networks.Count(n => n.IsWan && n.Enabled);
            if (wanCount < 2)
            {
                _logger.LogDebug(
                    "Routed-probe vantage: site has {Count} enabled WAN(s); nothing to attribute", wanCount);
                return;
            }

            var unpinnedCount = await db.MonitoringTargets.CountAsync(
                t => t.WanContextId == null
                    && t.TargetType != MonitoringTargetType.Fabric
                    && (t.WanInterface == null || t.WanInterface == MonitoringTarget.UnpinnedWan), ct);
            if (unpinnedCount == 0) return;

            var api = _connectionService.Client;
            if (api == null) return;

            var routes = await api.GetTrafficRoutesAsync(ct);
            if (routes.Count == 0) return;

            var hosts = ProbeHosts();
            var plan = PinnedProbeContextBuilder.Build(
                routes, networks, await api.GetClientsAsync(ct), hosts);
            if (plan == null)
            {
                _logger.LogDebug(
                    "Routed-probe vantage: {Count} unpinned target(s), but no all-destinations route "
                    + "names any of this site's {Hosts} probing box(es)", unpinnedCount, hosts.Count);
                return;
            }

            // The route has to pin the box that ACTUALLY probes these targets. A route steering the
            // server says nothing about an agent's probes, and binding the vantage to the wrong box
            // is worse than saying nothing: a target whose vantage names an agent that is not the
            // one asking gets pushed to nobody, so it would stop being probed entirely.
            var collector = _probeSink == null ? null : await _probeSink.GetCollectorAgentIdAsync(_siteSlug, ct);
            if (plan.AgentId != collector)
            {
                _logger.LogDebug(
                    "Routed-probe vantage: the route pins {Pinned}, but {Collector} probes the unpinned "
                    + "targets - the route says nothing about those probes",
                    plan.AgentId?.ToString() ?? "the server",
                    collector?.ToString() ?? "the server");
                return;
            }

            // Last, because it is the only question here that can cost a console round trip: a
            // gateway-resident agent binds its probe source directly and needs no policy route, so
            // a route appearing to name it is a coincidence rather than the steering we are after.
            if (_onGatewayDetector != null
                && await _onGatewayDetector.IsIpOnGatewayAsync(_siteSlug, plan.MatchedLanIp, ct))
            {
                _logger.LogDebug(
                    "Routed-probe vantage: {Ip} is the gateway itself, which binds its own probe source - "
                    + "no policy route needed or believed", plan.MatchedLanIp);
                return;
            }

            // Already covered: nothing to create, and nothing to adopt that the existing vantage
            // did not already take.
            var existing = await db.WanContexts.FirstOrDefaultAsync(
                c => c.WanInterface == plan.WanInterface, ct);
            if (existing != null)
            {
                _logger.LogDebug(
                    "Routed-probe vantage: '{Name}' already covers {Wan}; leaving it alone",
                    existing.Name, plan.WanInterface);
                return;
            }

            var vantage = new WanContext
            {
                Name = plan.ContextName,
                Description = "Created automatically: a policy route sends this site's probes out this WAN.",
                AgentId = plan.AgentId,
                WanInterface = plan.WanInterface,
                CreatedAt = DateTime.UtcNow,
            };
            db.WanContexts.Add(vantage);
            await db.SaveChangesAsync(ct);

            var moved = await PrimaryWanVantageAdoption.AdoptUnpinnedTargetsAsync(db, vantage, ct);
            _logger.LogInformation(
                "Routed-probe vantage: created '{Name}' for {Wan} (agent {Agent}, kill switch {Kill}) "
                + "and gave it {Count} unpinned target(s)",
                vantage.Name, plan.WanInterface, plan.AgentId?.ToString() ?? "server",
                plan.KillSwitchEnabled ? "on" : "off", moved);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not check whether a policy route routes this site's probes");
        }
    }

    /// <summary>
    /// Every box that probes for this site: its connected agents on the addresses they announced,
    /// and the server on its own. A route names a MAC, so the candidates are what let a match say
    /// WHICH box is steered rather than only that one is.
    /// </summary>
    private List<PinnedProbeContextBuilder.ProbeHost> ProbeHosts()
    {
        var hosts = (_tunnelRegistry?.GetForSite(_siteSlug) ?? new List<AgentTunnelConnection>())
            .Where(c => !string.IsNullOrWhiteSpace(c.LanIp))
            .Select(c => new PinnedProbeContextBuilder.ProbeHost(c.AgentId, c.LanIp))
            .ToList();
        hosts.Add(new PinnedProbeContextBuilder.ProbeHost(
            null, NetworkUtilities.GetAllLocalIpAddresses().FirstOrDefault()));
        return hosts;
    }

    /// <summary>
    /// Stores this WAN's expected speeds so they outlive the console connection. One row per WAN
    /// group; a rename changes the display name, not the identity.
    /// </summary>
    private async Task RememberWanSpeedsAsync(WanIdentity wan, double? down, double? up, CancellationToken ct)
    {
        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            var row = await db.WanProfiles.FirstOrDefaultAsync(w => w.WanNetworkgroup == wan.NetworkGroup, ct);
            if (row == null)
            {
                row = new WanProfile { WanNetworkgroup = wan.NetworkGroup! };
                db.WanProfiles.Add(row);
            }
            // Both names, because they are not interchangeable. The data path is what SQM deploys on
            // and what PPPoE is read from; the counter interface is what throughput is keyed on, and
            // on a VLAN-tagged WAN that is the PHYSICAL port - the sub-interface's counters double.
            // Storing one and using it for the other's job silently reports the wrong throughput.
            // Keep the previous data path when the device read comes back empty on an otherwise
            // successful console read: overwriting it with the physical port would make a later
            // offline PPPoE check grade the line without its overlay, which is what splitting these
            // two columns exists to prevent. Scoped instances resolve THEIR WAN's data path; the
            // primary keeps the primary resolver.
            var liveDataPath = _scopedWanKey == null
                ? await _connectionService.GetPrimaryWanDataPathInterfaceAsync(ct)
                : (await _connectionService.GetWanInterfacesForGroupAsync(
                    GatewayWanHelper.WanNetworkGroupFromKey(_scopedWanKey), ct)) is { } scopedIfaces
                    ? scopedIfaces.UplinkIfName ?? scopedIfaces.PhysicalIfName
                    : null;
            var dataPath = liveDataPath ?? row.DataPathInterface ?? wan.Interface;
            row.DataPathInterface = dataPath;
            row.CounterInterface = NetworkUtilities.PreferredWanCounterInterface(wan.Interface, dataPath);

            // Stored WAN rates are keyed on gateway MAC AND interface, so every offline fallback
            // filters on this being present - without it they match no row and silently do nothing.
            // Normalized here so readers cannot disagree about the form.
            var gatewayMac = (await _connectionService.GetDiscoveredDevicesAsync(ct))
                .FirstOrDefault(d => d.Type == NetworkOptimizer.Core.Enums.DeviceType.Gateway
                    || d.HardwareType == NetworkOptimizer.Core.Enums.DeviceType.Gateway)?.Mac;
            if (!string.IsNullOrEmpty(gatewayMac))
                row.GatewayMac = gatewayMac.Replace("-", ":").ToLowerInvariant();
            row.Name = wan.Name;
            row.DownloadMbps = down;
            row.UploadMbps = up;
            row.UpdatedAt = DateTime.UtcNow;

            // Record which WAN holds the primary role, and whether the site load balances, while
            // a console is answering. Both are read where no console can be reached - the probe
            // push path has none at all - and both are otherwise guessed from the WAN's NAME,
            // which carries no role information. Exactly one row may claim primary, so the others
            // are cleared in the same save rather than left to accumulate stale claims.
            var networks = await _connectionService.GetNetworksAsync(ct);
            var primaryGroup = UniFiConnectionService.ResolvePrimaryWanNetwork(networks)?.WanNetworkgroup;
            if (!string.IsNullOrEmpty(primaryGroup))
            {
                var loadBalances = UniFiConnectionService.ResolveSiteLoadBalances(networks);
                foreach (var profile in await db.WanProfiles.ToListAsync(ct))
                {
                    profile.IsPrimary = string.Equals(
                        profile.WanNetworkgroup, primaryGroup, StringComparison.OrdinalIgnoreCase);
                    profile.SiteLoadBalances = loadBalances;
                }
                row.IsPrimary = string.Equals(row.WanNetworkgroup, primaryGroup, StringComparison.OrdinalIgnoreCase);
                row.SiteLoadBalances = loadBalances;

                // Only where the attribution is genuinely lost. A failover site's unpinned probes
                // already read as the primary, so there is nothing to recover and no vantage worth
                // creating; a load-balancing site's took whichever WAN the balancer picked, and a
                // policy route is the only thing that can say which.
                if (loadBalances)
                    await CreateVantageForRoutedProbesAsync(db, networks, ct);
            }
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remember the expected speeds for WAN {Wan}", wan.NetworkGroup);
        }
    }

    private static readonly TimeSpan SqmProbeDuration = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The primary WAN's data-path interface. Falls back to the remembered WAN profile so an
    /// offline site still resolves it - the interface is what the throughput series are keyed on,
    /// so without it a site with plenty of stored history reads as having none.
    ///
    /// The offline pick is first-by-group-name, a documented GUESS: primary is a role, so on an
    /// offline WAN2-primary site with a WAN1 failover row this returns the failover's data path
    /// (WanProfile carries no primary marker to prefer). Per-WAN scoring resolves its own WAN's
    /// interface from its own row and never lands here.
    /// </summary>
    private async Task<string?> GetPrimaryWanInterfaceAsync(CancellationToken ct)
    {
        try
        {
            var live = await _connectionService.GetPrimaryWanDataPathInterfaceAsync(ct);
            if (!string.IsNullOrEmpty(live)) return live;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the primary WAN interface from the console");
        }

        // ONLY when the console is down. A connected console that answers with nothing keeps
        // answering nothing, exactly as before: eth0, eth6.100 and ppp0 all resolve live on that
        // path, and substituting a remembered name there would change an online result on the
        // strength of a stale row. The cache exists for the site with no console to ask.
        if (_connectionService.IsConnected) return null;

        try
        {
            await using var db = await CreateSiteDbAsync(ct);
            return await db.WanProfiles.AsNoTracking()
                .Where(w => w.DataPathInterface != null)
                .OrderBy(w => w.WanNetworkgroup)
                .ThenByDescending(w => w.UpdatedAt)
                .Select(w => w.DataPathInterface)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the remembered primary WAN interface");
            return null;
        }
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
            // Tests are attributed to the scored WAN by their recorded WAN group. A scoped WAN
            // takes only tests stamped with its own group, never unstamped ones - an unstamped
            // test ran over the default route, which is the primary's.
            var scopedGroupLower = _scopedWanKey == null
                ? null
                : GatewayWanHelper.WanNetworkGroupFromKey(_scopedWanKey).ToLowerInvariant();
            await using var db = await CreateSiteDbAsync(ct);

            // The primary's own group, when a connected compute has recorded which WAN holds the
            // role. Without it the predicate below falls back to the conventional first group,
            // which is right on the sites that have one WAN or lead with WAN1 and wrong on a site
            // whose primary is WAN2 - there it would miss every test stamped "WAN2" and count the
            // FAILOVER link's tests as the primary's, grading a backup circuit against the fiber
            // plan. Unstamped rows stay in either way: they predate stamping and ran over the
            // default route, which is the primary's by definition.
            var primaryGroupLower = scopedGroupLower != null
                ? null
                : (await db.WanProfiles.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.IsPrimary == true, ct))?.WanNetworkgroup?.ToLowerInvariant();
            var results = await db.Iperf3Results.AsNoTracking()
                .Where(r => r.Success
                    && r.TestTime >= since
                    && r.TestTime <= windowEnd
                    && (r.Direction == SpeedTestDirection.CloudflareWan
                        || r.Direction == SpeedTestDirection.CloudflareWanGateway
                        || r.Direction == SpeedTestDirection.UwnWan
                        || r.Direction == SpeedTestDirection.UwnWanGateway)
                    && (scopedGroupLower == null
                        ? (r.WanNetworkGroup == null
                            || r.WanNetworkGroup.ToLower() == (primaryGroupLower ?? "wan"))
                        : r.WanNetworkGroup != null && r.WanNetworkGroup.ToLower() == scopedGroupLower))
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
            // Warning, not Debug: an empty pool here renders as "No recent WAN speed
            // test" in the report - indistinguishable from genuinely having none - and
            // the report is then cached for CacheTtl. Post-restart DB contention can
            // land exactly here, so the failure must be visible in default logs.
            _logger.LogWarning(ex, "ISP Health could not load WAN speed test results; Speed vs Plan will show no tests until the next recompute");
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
                var asnName = AsnNameCleanup.Clean(t.AsnName);
                if (ispOverrides != null && ispOverrides.TryGetValue(t.TargetId, out var o))
                {
                    resolvedAsn = o.Asn;
                    asnName ??= AsnNameCleanup.Clean(o.Name);
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

    /// <summary>
    /// Address and PTR name per access hop, for the ISP Network breakout.
    ///
    /// Only AccessIsp targets are resolved: they are the only ones broken out per target, and every
    /// other monitored target would be a DNS lookup nobody ever sees. Resolved together rather than in
    /// sequence so a hop with no PTR costs the timeout once instead of adding to a queue, and the
    /// cache means a warm instance does no lookups at all.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> ResolveHopAddressesAsync(
        List<MonitoringTarget> ispTargets, CancellationToken ct)
    {
        var withAddress = ispTargets
            .Where(t => !string.IsNullOrWhiteSpace(t.Address))
            .GroupBy(t => t.TargetId)
            .Select(g => g.First())
            .ToList();

        var resolved = await Task.WhenAll(withAddress.Select(async t =>
        {
            var (ip, hostname) = await ReverseDnsCache.ResolveAsync(t.Address, ct);
            return (t.TargetId, Display: ReverseDnsCache.Format(ip, hostname));
        }));

        return resolved.ToDictionary(r => r.TargetId, r => r.Display);
    }

}
