using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <summary>
/// When the Path Map's routes are re-traced. One loop per site, shared by every viewer:
/// <list type="bullet">
/// <item>While someone views the map on an unmetered WAN, each target in view is re-traced once its
/// route is a minute old, amber targets first. Traces run one at a time in the site's trace slot
/// (<see cref="PathEpisodeWatcher.InTraceSlotAsync"/>), so a pass takes as long as its traces do.</item>
/// <item>On a metered WAN, a view traces each target once, skipping a route younger than 15 minutes.</item>
/// <item>With no one viewing, each target on an unmetered WAN is re-traced about hourly, and at once
/// when it turns degraded.</item>
/// </list>
/// The ISP and Transit targets ride along on the Internet and Custom targets' routes; one no traced
/// route crosses is traced on its own.
/// </summary>
public sealed class PathMapTraceScheduler : BackgroundService
{
    /// <summary>A viewer counts this long after its last request: the loop stops about a minute after the last one leaves.</summary>
    internal static readonly TimeSpan ViewerTimeout = TimeSpan.FromSeconds(75);

    /// <summary>A route this old is re-traced while someone views it.</summary>
    internal static readonly TimeSpan ViewedRouteAge = TimeSpan.FromSeconds(60);

    /// <summary>A metered WAN's route younger than this is not re-traced for a page load.</summary>
    internal static readonly TimeSpan MeteredRouteAge = TimeSpan.FromMinutes(15);

    /// <summary>A route this old is re-traced in the background on an unmetered WAN.</summary>
    internal static readonly TimeSpan BackgroundRouteAge = TimeSpan.FromHours(1);

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdleRescan = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ViewedRescan = TimeSpan.FromSeconds(5);

    private readonly PathEpisodeRegistry _episodes;
    private readonly PathRouteRecorder _recorder;
    private readonly SiteDbContextFactory _siteDb;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly NetworkOptimizer.Core.ISiteWorkGate _siteWorkGate;
    private readonly ILogger<PathMapTraceScheduler> _logger;
    private readonly ConcurrentDictionary<string, SiteState> _sites = new(StringComparer.OrdinalIgnoreCase);

    public PathMapTraceScheduler(PathEpisodeRegistry episodes, PathRouteRecorder recorder, SiteDbContextFactory siteDb,
        IDbContextFactory<NetworkOptimizerDbContext> mainDb, NetworkOptimizer.Core.ISiteWorkGate siteWorkGate,
        ILogger<PathMapTraceScheduler> logger)
    {
        _episodes = episodes;
        _recorder = recorder;
        _siteDb = siteDb;
        _mainDb = mainDb;
        _siteWorkGate = siteWorkGate;
        _logger = logger;
    }

    private sealed class SiteState
    {
        public readonly object Lock = new();
        /// <summary>Viewers' selections, by selection key, with when each was last asked for.</summary>
        public readonly Dictionary<string, (PathMapSelection Selection, DateTime LastSeen)> Viewers = new();
        /// <summary>When the current viewing began: a metered WAN traces each target once per viewing.</summary>
        public DateTime? ViewingSince;
        /// <summary>Targets traced since <see cref="ViewingSince"/> on a metered WAN, by TargetId.</summary>
        public readonly HashSet<int> MeteredTraced = new();
        /// <summary>Targets that turned degraded, to trace ahead of the background order.</summary>
        public readonly HashSet<string> Urgent = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>When each target was last picked, by Id: whether or not its trace stored a route.</summary>
        public readonly Dictionary<int, DateTime> Tried = new();
        public bool Subscribed;
        public Task? Running;
        public DateTime NextIdleScan;
    }

    /// <summary>
    /// Records that someone is viewing the map for a selection: the site's loop traces the targets in
    /// view until <see cref="ViewerTimeout"/> after the last call.
    /// </summary>
    public void NoteViewer(string slug, PathMapSelection selection)
    {
        var state = StateFor(slug);
        var now = DateTime.UtcNow;
        lock (state.Lock)
        {
            Expire(state, now);
            if (state.Viewers.Count == 0)
            {
                state.ViewingSince = now;
                state.MeteredTraced.Clear();
            }
            // A new selection is looked at now; a repeat poll keeps the current wait.
            if (!state.Viewers.ContainsKey(selection.Key)) state.NextIdleScan = DateTime.MinValue;
            state.Viewers[selection.Key] = (selection, now);
        }
    }

    /// <summary>Whether anyone is viewing the map on the site.</summary>
    public bool IsViewed(string slug)
    {
        if (!_sites.TryGetValue(slug, out var state)) return false;
        lock (state.Lock)
        {
            Expire(state, DateTime.UtcNow);
            return state.Viewers.Count > 0;
        }
    }

    private SiteState StateFor(string slug)
    {
        var state = _sites.GetOrAdd(slug, _ => new SiteState());
        lock (state.Lock)
        {
            if (state.Subscribed) return state;
            state.Subscribed = true;
        }
        _episodes.GetFor(slug).TargetDegraded += targetId =>
        {
            lock (state.Lock)
            {
                state.Urgent.Add(targetId);
                state.NextIdleScan = DateTime.MinValue;
            }
        };
        return state;
    }

    private static void Expire(SiteState state, DateTime now)
    {
        foreach (var key in state.Viewers.Where(v => now - v.Value.LastSeen > ViewerTimeout).Select(v => v.Key).ToList())
            state.Viewers.Remove(key);
        if (state.Viewers.Count == 0) state.ViewingSince = null;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Long enough for the first latency results, which name each target's vantage; a viewer
        // opening the map right after a restart waits no longer than this for its first trace.
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }

        var sitesReadAt = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTime.UtcNow - sitesReadAt > TimeSpan.FromMinutes(5))
                {
                    foreach (var slug in await EnabledSitesAsync(stoppingToken)) StateFor(slug);
                    sitesReadAt = DateTime.UtcNow;
                }
                foreach (var (slug, state) in _sites)
                {
                    lock (state.Lock)
                    {
                        if (state.Running is { IsCompleted: false }) continue;
                        Expire(state, DateTime.UtcNow);
                        if (DateTime.UtcNow < state.NextIdleScan) continue;
                        state.Running = Task.Run(() => StepAsync(slug, state, stoppingToken), stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Path Map trace scheduling failed");
            }

            try { await Task.Delay(Tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<List<string>> EnabledSitesAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _mainDb.CreateDbContextAsync(ct);
            var slugs = await db.Sites.AsNoTracking().Where(s => s.Enabled).Select(s => s.Slug).ToListAsync(ct);
            return slugs.Count > 0 ? slugs : new List<string> { SiteManagementService.DefaultSiteSlug };
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Path Map could not read the site list");
            return new List<string> { SiteManagementService.DefaultSiteSlug };
        }
    }

    /// <summary>Traces the site's most overdue target, if any is due.</summary>
    private async Task StepAsync(string slug, SiteState state, CancellationToken ct)
    {
        try
        {
            if (!_siteWorkGate.IsSiteOperational(slug)) { Idle(state, IdleRescan); return; }
            var next = await NextAsync(slug, state, ct);
            if (next == null) { Idle(state, null); return; }
            await _recorder.TraceAsync(slug, next.Value, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Path Map trace step failed for site {Slug}", slug);
            Idle(state, IdleRescan);
        }
    }

    /// <summary>Nothing due: look again soon while someone views the map, else in a minute.</summary>
    private static void Idle(SiteState state, TimeSpan? wait)
    {
        lock (state.Lock)
            state.NextIdleScan = DateTime.UtcNow + (wait ?? (state.Viewers.Count > 0 ? ViewedRescan : IdleRescan));
    }

    /// <summary>A target the loop could trace, with what decides its turn.</summary>
    internal sealed record Candidate(int Id, string TargetId, bool Viewed, bool Metered, bool Urgent, bool Amber, DateTime? RouteAt);

    private async Task<int?> NextAsync(string slug, SiteState state, CancellationToken ct)
    {
        List<PathMapSelection> selections;
        HashSet<string> urgent;
        HashSet<int> meteredTraced;
        Dictionary<int, DateTime> tried;
        lock (state.Lock)
        {
            tried = new Dictionary<int, DateTime>(state.Tried);
            selections = state.Viewers.Values.Select(v => v.Selection).ToList();
            urgent = new HashSet<string>(state.Urgent, StringComparer.OrdinalIgnoreCase);
            meteredTraced = new HashSet<int>(state.MeteredTraced);
        }

        var isDefault = slug == SiteManagementService.DefaultSiteSlug;
        await using var db = _siteDb.CreateForSite(slug, isDefault);
        var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        if (settings == null || !settings.Enabled) return null;

        var targets = (await db.MonitoringTargets.AsNoTracking().ToListAsync(ct)).Where(PathMapScope.IsMapped).ToList();
        if (targets.Count == 0) return null;
        var routes = await RoutesAsync(db, ct);
        var watcher = _episodes.GetFor(slug);
        var contexts = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var primaryKey = selections.FirstOrDefault()?.PrimaryKey ?? "wan";

        // Metered state per WAN, read once per step.
        var metered = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        async Task<bool> MeteredAsync(MonitoringTarget t)
        {
            var wan = t.WanContextId is int c && contexts.TryGetValue(c, out var ctx) ? ctx.WanInterface : null;
            var key = wan ?? ShiftRediscovery.PrimaryKey;
            if (!metered.TryGetValue(key, out var m)) metered[key] = m = await watcher.IsWanMeteredAsync(wan, wan == null);
            return m;
        }

        // ISP and Transit targets ride on the Internet and Custom routes that cross them.
        var destinationIds = targets.Where(PathMapScope.IsDestination).Select(t => t.Id).ToHashSet();
        var crossed = routes.Where(r => destinationIds.Contains(r.Key))
            .SelectMany(r => r.Value.Latest.Hops.Select(h => h.Address))
            .Where(a => a != null).Select(a => a!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tracedRouterIds = await db.UpstreamDiscoveries.AsNoTracking()
            .Where(d => d.MonitoringTargetId != null).Select(d => d.MonitoringTargetId!.Value).ToListAsync(ct);
        var routerRows = tracedRouterIds.ToHashSet();

        var now = DateTime.UtcNow;
        var candidates = new List<Candidate>();
        foreach (var t in targets)
        {
            // A router no route crosses is traced on its own, and only one discovery gave a row to.
            if (!PathMapScope.IsDestination(t) && (crossed.Contains(t.Address) || !routerRows.Contains(t.Id))) continue;
            var viewed = selections.Any(s => PathMapScope.InView(t, s));
            var isUrgent = urgent.Contains(t.TargetId);
            // The later of the stored route and the last attempt: a trace that stores nothing must not keep the
            // target first in line (a site re-traced its own upstream gateway every step while two routers waited).
            var routeAt = routes.TryGetValue(t.Id, out var r) ? r.Latest.CheckedAt : (DateTime?)null;
            if (tried.TryGetValue(t.Id, out var at) && (routeAt == null || at > routeAt)) routeAt = at;
            var amber = watcher.StateOf(t.TargetId, now)?.Health is PathTargetHealth.Elevated or PathTargetHealth.Degraded
                or PathTargetHealth.Unreachable;
            candidates.Add(new Candidate(t.Id, t.TargetId, viewed, await MeteredAsync(t), isUrgent, amber, routeAt));
        }

        var pick = Pick(candidates, meteredTraced, now);
        lock (state.Lock)
        {
            // A degraded target this loop never traces (a LAN target, a metered WAN's) is dropped.
            state.Urgent.RemoveWhere(id => !candidates.Any(c => c.Urgent && !c.Metered
                && string.Equals(c.TargetId, id, StringComparison.OrdinalIgnoreCase)));
            if (pick == null) return null;
            state.Urgent.Remove(pick.TargetId);
            state.Tried[pick.Id] = now;
            if (pick.Metered && pick.Viewed) state.MeteredTraced.Add(pick.Id);
        }
        return pick.Id;
    }

    /// <summary>
    /// The next target to trace, or null when none is due: viewed targets first (amber ones ahead,
    /// then the oldest route), then degraded ones, then the background order. Pure, for tests.
    /// </summary>
    internal static Candidate? Pick(IReadOnlyList<Candidate> candidates, IReadOnlySet<int> meteredTraced, DateTime now)
    {
        static TimeSpan Age(Candidate c, DateTime now) => c.RouteAt is DateTime at ? now - at : TimeSpan.MaxValue;

        var viewed = candidates.Where(c => c.Viewed && (c.Metered
                ? !meteredTraced.Contains(c.Id) && Age(c, now) >= MeteredRouteAge
                : Age(c, now) >= ViewedRouteAge))
            .OrderByDescending(c => c.Amber).ThenByDescending(c => Age(c, now))
            .FirstOrDefault();
        if (viewed != null) return viewed;

        // A metered WAN gets no background tracing; its episodes already trace it once.
        var urgent = candidates.Where(c => c.Urgent && !c.Metered).OrderByDescending(c => Age(c, now)).FirstOrDefault();
        if (urgent != null) return urgent;

        return candidates.Where(c => !c.Metered && Age(c, now) >= BackgroundRouteAge)
            .OrderByDescending(c => Age(c, now)).FirstOrDefault();
    }

    private static async Task<Dictionary<int, StoredRouteState>> RoutesAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        var rows = await db.UpstreamDiscoveries.AsNoTracking()
            .Where(d => d.MonitoringTargetId != null && d.RouteJson != null)
            .Select(d => new { Id = d.MonitoringTargetId!.Value, d.RouteJson })
            .ToListAsync(ct);
        var result = new Dictionary<int, StoredRouteState>();
        foreach (var row in rows)
        {
            if (StoredRoutes.ReadState(row.RouteJson) is { } state) result.TryAdd(row.Id, state);
        }
        return result;
    }
}
