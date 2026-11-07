using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// Reads the site in context's episodes. The hop tables are rebuilt from the stored rounds with the
/// same analyzer the live verdict came from, and the monitored hops' anchors recorded with it, so an
/// episode reads the same after InfluxDB has downsampled the series it ran against.
///
/// Every call opens its own context on the site's database rather than sharing the circuit's: the
/// panel and each open hop table read on their own timers, and one context cannot serve overlapping
/// queries.
/// </summary>
public sealed class PathEpisodeService : IPathEpisodeService
{
    private const int MaxEpisodes = 200;

    private readonly SiteDbContextFactory _siteDb;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly PathEpisodeRegistry _registry;
    private readonly SiteContextService _site;
    private readonly AsnResolutionService _asn;
    private readonly IspHealthOptions _options = new();

    public PathEpisodeService(SiteDbContextFactory siteDb, IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        PathEpisodeRegistry registry, SiteContextService site, AsnResolutionService asn)
    {
        _siteDb = siteDb;
        _mainDb = mainDb;
        _registry = registry;
        _site = site;
        _asn = asn;
    }

    public async Task<IReadOnlyList<PathEpisodeView>> GetEpisodesAsync(DateTime from, DateTime to, string? wanKey, bool isPrimary,
        CancellationToken ct = default)
    {
        await using var db = _siteDb.CreateForSite(_site.Slug, _site.IsDefault);
        var rows = await new PathEpisodeRepository(db).GetOverlappingAsync(from, to, MaxEpisodes, ct);
        var key = string.IsNullOrEmpty(wanKey) ? null : GatewayWanHelper.WanInterfaceKeyFromKey(wanKey);
        rows = rows.Where(e =>
                (isPrimary && e.WanContextId == null)
                || (key != null && e.WanInterface != null
                    && string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(e.WanInterface), key, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (rows.Count == 0) return Array.Empty<PathEpisodeView>();

        var targets = await TargetsAsync(db, rows.SelectMany(e => e.Targets.Select(t => t.MonitoringTargetId)), ct);
        return rows.Select(e => View(e, targets)).ToList();
    }

    public async Task<PathEpisodeDetail?> GetEpisodeAsync(int id, CancellationToken ct = default)
    {
        PathEpisode? row;
        Dictionary<int, MonitoringTarget> targets;
        await using (var db = _siteDb.CreateForSite(_site.Slug, _site.IsDefault))
        {
            row = await new PathEpisodeRepository(db).GetWithTracesAsync(id, ct);
            if (row == null) return null;
            targets = await TargetsAsync(db, row.Targets.Select(t => t.MonitoringTargetId), ct);
        }
        var view = View(row, targets);

        var rounds = row.Traces
            .OrderBy(t => t.Timestamp)
            .Select(t => (Trace: t, Result: ToResult(t, targets)))
            .Where(x => x.Result != null)
            .ToList();
        TargetTraces Traces(PathEpisodeTargetView t, params PathEpisodeTraceKind[] kinds)
        {
            var mine = rounds.Where(r => r.Trace.MonitoringTargetId == t.MonitoringTargetId).ToList();
            var address = targets.TryGetValue(t.MonitoringTargetId, out var mt) ? mt.Address : t.Name;
            return new TargetTraces(t.TargetId, t.Name, address,
                mine.Where(r => kinds.Contains(r.Trace.Kind)).Select(r => r.Result!).ToList(),
                mine.LastOrDefault(r => r.Trace.Kind == PathEpisodeTraceKind.Recovery).Result);
        }

        var degraded = view.Degraded.Select(t => Traces(t, PathEpisodeTraceKind.Path)).ToList();
        var controls = view.Controls.Select(t => Traces(t, PathEpisodeTraceKind.Control)).Where(c => c.PathRounds.Count > 0).ToList();

        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in PathTraceAnalyzer.AddressesOf(degraded.Concat(controls)))
            asns[address] = await _asn.ResolveAsync(address, ct);

        var snapshot = PathGateSnapshot.Read(row.GateSnapshot);
        var anchors = snapshot.Anchors;
        // A degraded target's row holds its series baseline from when the episode opened, so even an
        // episode stored before SeriesBaselines existed shows one on its destination row.
        var seriesBaselines = snapshot.SeriesBaselines;
        foreach (var (t, traces) in view.Degraded.Zip(degraded))
        {
            if (t.BaselineRttMs is not double b) continue;
            foreach (var address in traces.PathRounds.Select(r => r.ResolvedAddress).Append(traces.Address))
                if (!string.IsNullOrEmpty(address)) seriesBaselines.TryAdd(address, b);
        }
        var verdict = PathTraceAnalyzer.Analyze(degraded, controls, ip => asns.GetValueOrDefault(ip),
            ip => anchors.TryGetValue(ip, out var a) ? a : null, _options,
            PathEpisodePresentation.Together(view.Degraded.Select(d => (d.PeakLossPct, d.BaselineRttMs, d.PeakRttMs))),
            new HashSet<string>(snapshot.AccessHops, StringComparer.OrdinalIgnoreCase), seriesBaselines);

        var agentNames = await AgentNamesAsync(ct);
        var tables = view.Degraded.Select(t =>
        {
            var v = verdict.Targets.FirstOrDefault(x => x.TargetId == t.TargetId);
            var trace = rounds.Where(r => r.Trace.MonitoringTargetId == t.MonitoringTargetId).Select(r => r.Trace).LastOrDefault();
            var tt = degraded.First(d => d.TargetId == t.TargetId);
            var vantage = trace?.Vantage ?? "server";
            return new PathEpisodeHopTable(t, tt.Address, vantage, trace?.SourceInterface,
                tt.PathRounds.Count, tt.Recovery != null, v?.Hops ?? Array.Empty<HopReading>(), VantageName(vantage, agentNames),
                trace?.ResolvedAddress);
        }).ToList();

        return new PathEpisodeDetail(view, tables, new HashSet<string>(snapshot.PingedHops, StringComparer.OrdinalIgnoreCase), snapshot.HopPings,
            snapshot.MonitoredLoss);
    }

    /// <summary>The site's agents by id, for naming a trace's vantage.</summary>
    private async Task<Dictionary<int, string>> AgentNamesAsync(CancellationToken ct)
    {
        await using var mainDb = await _mainDb.CreateDbContextAsync(ct);
        var site = await mainDb.Sites.AsNoTracking().FirstOrDefaultAsync(s => s.Slug == _site.Slug, ct);
        return site == null
            ? new()
            : await mainDb.SiteAgents.AsNoTracking().Where(a => a.SiteId == site.Id).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
    }

    /// <summary>"this server", or the agent's name; an agent since removed reads as "an agent".</summary>
    private static string VantageName(string vantage, IReadOnlyDictionary<int, string> agentNames) =>
        !vantage.StartsWith("agent-", StringComparison.Ordinal) ? "this server"
        : int.TryParse(vantage[6..], out var id) && agentNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? name
        : "an agent";

    private static async Task<Dictionary<int, MonitoringTarget>> TargetsAsync(NetworkOptimizerDbContext db, IEnumerable<int> ids,
        CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.MonitoringTargets.AsNoTracking().Where(t => list.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
    }

    private PathEpisodeView View(PathEpisode e, IReadOnlyDictionary<int, MonitoringTarget> targets)
    {
        PathEpisodeTargetView Target(PathEpisodeTarget t)
        {
            targets.TryGetValue(t.MonitoringTargetId, out var mt);
            var name = mt == null ? "Removed target" : string.IsNullOrEmpty(mt.Name) ? mt.Address : mt.Name;
            return new PathEpisodeTargetView(t.MonitoringTargetId, mt?.TargetId ?? "", name, t.Role,
                t.BaselineRttMs, t.PeakRttMs, t.BaselineLossPct, t.PeakLossPct, t.BottleneckLabel,
                mt == null ? MonitoringLinks.CustomCategory : MonitoringLinks.CategoryFor(mt.TargetType),
                t.BaselineJitterMs, t.PeakJitterMs);
        }

        return new PathEpisodeView
        {
            Id = e.Id,
            OpenedAt = Utc(e.OpenedAt),
            ClosedAt = e.ClosedAt is DateTime closed ? Utc(closed) : null,
            Tracing = e.ClosedAt == null && _registry.GetFor(_site.Slug).IsTracing(e.Id),
            RoundsTraced = e.ClosedAt == null ? _registry.GetFor(_site.Slug).RoundsTraced(e.Id) : null,
            Metered = PathGateSnapshot.Read(e.GateSnapshot).Metered,
            UpdatedAt = Utc(e.UpdatedAt),
            WanContextId = e.WanContextId,
            WanInterface = e.WanInterface,
            Scope = Enum.TryParse<CongestionScope>(e.Scope, out var scope) ? scope : CongestionScope.Unlocalized,
            Disposition = Enum.TryParse<CongestionDisposition>(e.Disposition, out var disp) ? disp : CongestionDisposition.Unverifiable,
            Label = e.BottleneckLabel,
            Summary = e.Summary,
            ReturnFinding = e.ReturnPathFinding,
            Degraded = e.Targets.Where(t => t.Role == PathEpisodeTargetRole.Degraded).Select(Target).ToList(),
            Controls = e.Targets.Where(t => t.Role == PathEpisodeTargetRole.Control).Select(Target).ToList()
        };
    }

    private static DateTime Utc(DateTime t) => PathEpisodeTraceMapping.Utc(t);

    private static TracerouteResult? ToResult(PathEpisodeTrace t, IReadOnlyDictionary<int, MonitoringTarget> targets) =>
        PathEpisodeTraceMapping.ToResult(t, targets.TryGetValue(t.MonitoringTargetId, out var mt) ? mt.Address : "");
}

/// <summary>Turns stored rounds back into the traces the analyzer reads, for the service and for resuming an episode.</summary>
internal static class PathEpisodeTraceMapping
{
    /// <summary>
    /// Episode times are written in UTC, but SQLite hands them back with no kind. Marked here, so a
    /// serialized time carries its Z: a browser reads one without it as local time.
    /// </summary>
    public static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : DateTime.SpecifyKind(t, DateTimeKind.Utc);

    /// <summary>One stored round as a trace, or null when it recorded no hops or cannot be read.</summary>
    /// <param name="address">The address the target's probes go to.</param>
    public static TracerouteResult? ToResult(PathEpisodeTrace t, string address)
    {
        try
        {
            var hops = JsonSerializer.Deserialize<List<TraceHop>>(t.HopsJson);
            if (hops == null || hops.Count == 0) return null;
            return new TracerouteResult
            {
                Target = new ProbeTarget(address, Core.Enums.ProbeMode.Icmp, null, t.SourceInterface),
                Vantage = new ProbeVantage(t.Vantage, VantageKind.Server),
                ModeUsed = Core.Enums.ProbeMode.Icmp,
                Timestamp = Utc(t.Timestamp),
                Hops = hops,
                ResolvedAddress = t.ResolvedAddress,
                Reached = t.Reached
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
