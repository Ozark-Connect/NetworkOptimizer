using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;
using NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

namespace NetworkOptimizer.Web.Services.Monitoring.PathMap;

/// <inheritdoc />
public sealed class PathMapService : IPathMapService
{
    /// <summary>Most points in a hover card's sparkline.</summary>
    internal const int SparkPoints = 36;

    /// <summary>A window ending this close to now is live.</summary>
    private static readonly TimeSpan LiveSlack = TimeSpan.FromMinutes(1);

    private readonly SiteDbContextFactory _siteDb;
    private readonly SiteContextService _site;
    private readonly PathEpisodeRegistry _episodes;
    private readonly PathMapTraceScheduler _scheduler;
    private readonly RoutePlacer _placer;
    private readonly MonitoringPathView _pathView;
    private readonly GeoEnrichmentService _geo;
    private readonly ILogger<PathMapService> _logger;
    private readonly IspHealthOptions _options = new();

    public PathMapService(SiteDbContextFactory siteDb, SiteContextService site, PathEpisodeRegistry episodes,
        PathMapTraceScheduler scheduler, RoutePlacer placer, MonitoringPathView pathView, GeoEnrichmentService geo,
        ILogger<PathMapService> logger)
    {
        _siteDb = siteDb;
        _site = site;
        _episodes = episodes;
        _scheduler = scheduler;
        _placer = placer;
        _pathView = pathView;
        _geo = geo;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PathMapView> GetAsync(PathMapQuery query, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var selection = PathMapSelection.Parse(query.Wans, query.All, query.Primary);
        var (from, to) = Window(query, now);
        var live = now - to <= LiveSlack;
        if (query.Trace && live) _scheduler.NoteViewer(_site.Slug, selection);

        await using var db = _siteDb.CreateForSite(_site.Slug, _site.IsDefault);
        var targets = (await db.MonitoringTargets.AsNoTracking().ToListAsync(ct))
            .Where(t => PathMapScope.IsMapped(t) && PathMapScope.InView(t, selection))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0) return PathMapView.Empty(null) with { Live = live, AsOf = live ? null : to };

        var ids = targets.Select(t => (int?)t.Id).ToList();
        var rows = await db.UpstreamDiscoveries.AsNoTracking()
            .Where(d => ids.Contains(d.MonitoringTargetId))
            .Select(d => new { Id = d.MonitoringTargetId!.Value, d.RouteJson, d.RouteShiftsJson })
            .ToListAsync(ct);
        // A router discovery gave no row to is never traced on its own (PathMapTraceScheduler).
        var withRow = rows.Select(r => r.Id).ToHashSet();
        var stored = rows.Where(r => r.RouteJson != null).GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
        // Every trace the site keeps: a hop silent on one route may have answered on another.
        var history = stored.Values.Select(r => StoredRoutes.ReadState(r.RouteJson)).Where(s => s != null)
            .SelectMany(s => new[] { s!.Latest, s.Previous, s.Stable }).Where(r => r != null).Select(r => r!).ToList();

        var watcher = _episodes.GetFor(_site.Slug);
        var overrides = await _placer.OverridesAsync(_site.Slug, ct);
        var learned = await _placer.LearnedAsync(_site.Slug, ct);
        var wans = await WansAsync(ct);
        var starts = Starts(wans, overrides);

        var inputs = new List<PathMapTargetInput>();
        foreach (var t in targets)
        {
            var wanKey = PathMapScope.WanKey(t, selection.PrimaryKey);
            PlacedRoute? route = null, shiftFrom = null, shiftTo = null;
            DateTime? shiftAt = null, checkedAt = null;
            if (stored.TryGetValue(t.Id, out var row) && StoredRoutes.ReadState(row.RouteJson) is { } state)
            {
                var shifts = StoredRoutes.ReadShifts(row.RouteShiftsJson);
                var drawn = HopHistory.Fill(live ? state.Stable : StoredRoutes.RouteAt(state, shifts, to), history);
                route = await _placer.PlaceAsync(drawn, overrides, ct, learned);
                checkedAt = live ? state.Latest.CheckedAt : drawn.CheckedAt;
                // The latest shift in the window that is still a route change once both routes are placed alike:
                // one stored before a hop was placed better is not.
                foreach (var shift in shifts.Where(s => s.At >= from && s.At <= to).OrderByDescending(s => s.At))
                {
                    var before = await _placer.PlaceAsync(shift.From, overrides, ct, learned);
                    var after = await _placer.PlaceAsync(shift.To, overrides, ct, learned);
                    if (!RouteChanges.IsReal(before, after)) continue;
                    (shiftFrom, shiftTo, shiftAt) = (before, after, shift.At);
                    break;
                }
            }

            var current = watcher.StateOf(t.TargetId, now);
            var health = live
                ? current ?? new PathTargetState(PathTargetHealth.Unknown, null, null, null, null, null)
                : PathTargetState.FromWindows(watcher.WindowsOf(t.TargetId, from, to),
                    _options.PathDegradationConsecutiveWindows, current?.BaselineRttMs);
            var wan = wans.FirstOrDefault(w => string.Equals(w.WanInterface, wanKey, StringComparison.OrdinalIgnoreCase));
            var vantage = route?.Route.Vantage ?? watcher.VantageFor(t, null).Label;

            inputs.Add(new PathMapTargetInput(
                t.TargetId, string.IsNullOrEmpty(t.Name) ? t.Address : t.Name, MonitoringLinks.CategoryFor(t.TargetType),
                wanKey, wan?.FriendlyName, PathMapScope.IsUnpinned(t), t.Address, route, shiftFrom, shiftAt, checkedAt, health,
                Spark(watcher.WindowsOf(t.TargetId, from, to)),
                PathEpisodePresentation.RerunUrl(t.Address, vantage, t.WanContextId),
                Traced: PathMapScope.IsDestination(t) || withRow.Contains(t.Id), ShiftTo: shiftTo));
        }

        var located = await LocatedAsync(db, inputs, targets, from, to, ct);
        return PathMapBuilder.Build(inputs, starts, located, _options, live, live ? null : to);
    }

    /// <summary>The window the charts show: explicit bounds, else trailing the range from now.</summary>
    internal static (DateTime From, DateTime To) Window(PathMapQuery q, DateTime now)
    {
        if (q.From is DateTime f && q.To is DateTime t && f < t) return (f.ToUniversalTime(), t.ToUniversalTime());
        var hours = q.RangeHours ?? 1;
        return (hours <= 0 ? now.AddMinutes(-15) : now.AddHours(-hours), now);
    }

    /// <summary>Each window's median RTT, averaged down to at most <see cref="SparkPoints"/> points.</summary>
    internal static IReadOnlyList<double?> Spark(IReadOnlyList<DegradationWindow> windows)
    {
        if (windows.Count == 0) return Array.Empty<double?>();
        var size = (int)Math.Ceiling(windows.Count / (double)SparkPoints);
        return windows.Chunk(size).Select(c =>
        {
            var values = c.Where(w => w.MedianRttMs.HasValue).Select(w => w.MedianRttMs!.Value).ToList();
            return values.Count == 0 ? (double?)null : Math.Round(values.Average(), 1);
        }).ToList();
    }

    private async Task<IReadOnlyList<WanSummary>> WansAsync(CancellationToken ct)
    {
        try { return await _pathView.GetWansAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Path Map could not read the WANs for site {Slug}", _site.Slug);
            return Array.Empty<WanSummary>();
        }
    }

    /// <summary>
    /// Where each WAN's traces start, as the Route Map draws it (<see cref="HopLocations.Start"/>): the
    /// city set by hand for the WAN's public IPv4 address, else GeoLite2's. A WAN without one starts at
    /// its routes' first placed hop.
    /// </summary>
    private Dictionary<string, PathMapStart> Starts(IReadOnlyList<WanSummary> wans, IReadOnlyDictionary<string, HopLocationSetting> overrides)
    {
        var starts = new Dictionary<string, PathMapStart>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in wans)
        {
            if (w.IpAddress is not { Length: > 0 } ip || w.IpClass != PublicAddressClass.PublicIPv4) continue;
            if (HopLocations.Start(ip, _geo.Enrich, overrides) is { } at)
                starts[w.WanInterface] = new PathMapStart(at.City, at.CountryCode, at.Latitude, at.Longitude, w.FriendlyName ?? w.WanInterface);
        }
        return starts;
    }

    /// <summary>
    /// Path Localization's located segments for the episodes in the window: the onset hop it found,
    /// and the responding hop before it on the degraded target's route as the last clean one.
    /// </summary>
    private static async Task<List<PathMapLocated>> LocatedAsync(NetworkOptimizerDbContext db, IReadOnlyList<PathMapTargetInput> inputs,
        IReadOnlyList<MonitoringTarget> targets, DateTime from, DateTime to, CancellationToken ct)
    {
        var episodes = await db.PathEpisodes.AsNoTracking()
            .Where(e => e.BottleneckHopIp != null && e.OpenedAt <= to && (e.ClosedAt == null || e.ClosedAt >= from))
            .Include(e => e.Targets)
            .ToListAsync(ct);
        var located = new List<PathMapLocated>();
        var byDbId = targets.ToDictionary(t => t.Id);
        var inputById = inputs.ToDictionary(i => i.TargetId, StringComparer.OrdinalIgnoreCase);
        foreach (var e in episodes)
        {
            string? lastClean = null;
            foreach (var et in e.Targets.Where(t => t.Role == PathEpisodeTargetRole.Degraded))
            {
                if (!byDbId.TryGetValue(et.MonitoringTargetId, out var mt) || !inputById.TryGetValue(mt.TargetId, out var input)
                    || input.Route == null) continue;
                var hops = input.Route.Route.Hops.Where(h => h.Address != null).ToList();
                var at = hops.FindIndex(h => string.Equals(h.Address, e.BottleneckHopIp, StringComparison.OrdinalIgnoreCase));
                if (at > 0) { lastClean = hops[at - 1].Address; break; }
            }
            located.Add(new PathMapLocated(lastClean, e.BottleneckHopIp!));
        }
        return located;
    }
}
