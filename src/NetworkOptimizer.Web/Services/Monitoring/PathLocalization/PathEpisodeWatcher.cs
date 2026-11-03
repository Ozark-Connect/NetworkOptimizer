using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.Monitoring.PathLocalization;

/// <summary>
/// The vantage and bind that measure a target: the server's own prober (no agent), or one named agent.
/// A trace from anywhere else measures a different path and is worthless for localizing this one.
/// </summary>
/// <param name="AgentId">The agent whose results are recorded for the target; null for the server.</param>
/// <param name="SourceInterface">The source IP or interface the target's probes bind to; null for the default route.</param>
public sealed record PathVantage(int? AgentId, string? SourceInterface)
{
    /// <summary>The vantage as the latency series names it ("server", "agent-3").</summary>
    public string Label => AgentId is int id ? $"agent-{id}" : "server";

    /// <summary>The vantage a stored label names ("server", "agent-3"), with the bind stored beside it.</summary>
    public static PathVantage FromLabel(string label, string? sourceInterface) =>
        new(label.StartsWith("agent-", StringComparison.Ordinal) && int.TryParse(label[6..], out var id) ? id : null, sourceInterface);
}

/// <summary>
/// Path Localization for one site. Every latency result either write path records (the server's
/// collection loop and the agent result sink) is fed here; each target's samples fold into its own
/// <see cref="TargetDegradation"/>. When an Internet or Custom target degrades and the gate says the
/// rest of the WAN is clean, an episode opens: traceroutes to the degraded targets from the vantage
/// that measures them, every minute while they stay degraded, one to a clean control, and one more
/// after recovery as the per-hop baseline. Each round is stored and analyzed as it lands, so the
/// verdict on ISP Health is live while the episode runs.
///
/// One episode per WAN context at a time. Episode capture is the app probing, not a change to any
/// configuration or device, so it writes no audit entry.
/// </summary>
public sealed class PathEpisodeWatcher
{
    /// <summary>How far back the gate reads WAN load: the sustained span that made a target degraded.</summary>
    private static readonly TimeSpan LoadLookback = TimeSpan.FromMinutes(15);

    /// <summary>How far back the gate looks for a target that rose without staying degraded: the span of one degraded run.</summary>
    private TimeSpan RecentSpan => TimeSpan.FromMinutes(_options.PathDegradationWindowMinutes * _options.PathDegradationConsecutiveWindows);

    private const string WaitingOnRecoveryNote = " Waiting on the recovery round for the per-hop baseline.";

    private static readonly TimeSpan PerHopTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TraceDeadline = TimeSpan.FromSeconds(25);

    private readonly string _slug;
    private readonly bool _isDefault;
    private readonly SiteDbContextFactory _siteDb;
    private readonly MonitoringInfluxRegistry _influx;
    private readonly MonitoringAlertRegistry _alerts;
    private readonly IspHealthRegistry _ispHealth;
    private readonly UpstreamTracerRegistry _tracers;
    private readonly LocalProbeExecutor _localProbe;
    private readonly SiteAgentCoverage _coverage;
    private readonly AgentProbeService _agentProbe;
    private readonly AsnResolutionService _asn;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IspHealthOptions _options = new();

    // Everything below is guarded by _lock. Observe runs on every probe result from many threads; the
    // runner's tick and the gate evaluations read and update the same state.
    private readonly object _lock = new();
    private readonly Dictionary<string, TargetDegradation> _trackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Watched> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, EpisodeRun> _runs = new();
    private readonly Dictionary<int, string?> _lastBlocked = new();
    private readonly HashSet<int> _evaluating = new();
    // Targets whose episode hit its time limit, with the DegradedSince of the degradation it traced.
    // The same degradation does not open another episode; a new one (a new DegradedSince) does.
    private readonly Dictionary<string, DateTime?> _exhausted = new(StringComparer.OrdinalIgnoreCase);
    private bool _started;

    // One trace at a time across this site's episodes. Linux routers rate-limit Time Exceeded per
    // source (icmp_ratelimit, burst 6), so two traces' 5 TTL-1 probes at once lose replies at the
    // gateway, which then reads as a lossy hop. One also leaves the local executor's process slots
    // to the latency pings that decide degradation and outages.
    private readonly SemaphoreSlim _traceSlots = new(1, 1);


    public PathEpisodeWatcher(
        string slug,
        SiteDbContextFactory siteDb,
        MonitoringInfluxRegistry influx,
        MonitoringAlertRegistry alerts,
        IspHealthRegistry ispHealth,
        UpstreamTracerRegistry tracers,
        LocalProbeExecutor localProbe,
        AgentProbeService agentProbe,
        AsnResolutionService asn,
        ILoggerFactory loggerFactory,
        SiteAgentCoverage coverage)
    {
        _coverage = coverage;
        _slug = slug;
        _isDefault = slug == SiteManagementService.DefaultSiteSlug;
        _siteDb = siteDb;
        _influx = influx;
        _alerts = alerts;
        _ispHealth = ispHealth;
        _tracers = tracers;
        _localProbe = localProbe;
        _agentProbe = agentProbe;
        _asn = asn;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PathEpisodeWatcher>();
    }

    /// <summary>A target as last observed: what it is, which context it measures, and from where.</summary>
    private sealed record Watched(int Id, string TargetId, string Name, string Address, GateRole Role,
        int ContextKey, int? WanContextId, string? WanInterface, PathVantage Vantage);

    /// <summary>A target an episode traces, with its rounds so far.</summary>
    private sealed class RunTarget
    {
        public required Watched Target { get; set; }
        /// <summary>The vantage came from the context, not a trace: replace it with the target's next live observation.</summary>
        public bool VantageGuessed { get; set; }
        public required PathEpisodeTargetRole Role { get; init; }
        public List<TracerouteResult> Rounds { get; } = new();
        public TracerouteResult? Recovery { get; set; }
        /// <summary>The one address a hostname target is traced at for the whole episode, recovery included.</summary>
        public string? ResolvedAddress { get; set; }
        public bool ResolveTried { get; set; }
        public int RoundsRun { get; set; }
        public DateTime NextRoundAt { get; set; }
    }

    /// <summary>An episode in flight: open, or closed and waiting on its recovery round.</summary>
    private sealed class EpisodeRun
    {
        public required PathEpisode Row { get; init; }
        public required int ContextKey { get; init; }
        /// <summary>When tracing began. The time limit runs from here, not from when the degradation began:
        /// a gate that held it back for hours must not hand over an episode that is already out of time.</summary>
        public required DateTime StartedAt { get; init; }
        /// <summary>Why tracing stopped short of the targets clearing, for the summary; null when they cleared.</summary>
        public string? StopNote { get; set; }
        /// <summary>Why rounds are paused, when the gate stopped holding mid-episode; null while tracing.</summary>
        public string? PausedBy { get; set; }
        public DateTime? PausedSince { get; set; }
        /// <summary>Paused time before the current pause, which the time limit leaves out.</summary>
        public TimeSpan PausedTotal { get; set; }
        public DateTime NextGateCheckAt { get; set; }
        public required bool Metered { get; init; }
        public List<RunTarget> Degraded { get; } = new();
        public RunTarget? Control { get; init; }
        public bool ControlTraced { get; set; }
        public DateTime? RecoveryDue { get; set; }
        public bool Busy { get; set; }
        /// <summary>Changed outside a round (resumed), so the next tick saves it even with nothing to trace.</summary>
        public bool Dirty { get; set; }
        /// <summary>Echo pings to unmonitored hops a trace marked elevated, by address. Touched only by AdvanceAsync.</summary>
        public Dictionary<string, HopPing> HopPings { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// Monitored addresses' series baseline RTT, kept as first read: the tracker's baseline moves on
        /// after the episode, and the table should show what the path was compared against.
        /// </summary>
        public Dictionary<string, double> SeriesBaselines { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Pooled echo pings to one hop. A router limits the Time Exceeded replies a trace reads far more
    /// than echo replies, yet an echo to it crosses the same forward path, so a clean echo series
    /// means the trace's loss there was reply generation, not forwarding.
    /// </summary>
    private sealed class HopPing
    {
        /// <summary>The degraded target whose trace found the hop; the hop is pinged from that target's current vantage.</summary>
        public required string TargetId { get; init; }
        public int Sent { get; set; }
        public int Received { get; set; }
        public double? MinRttMs { get; set; }
        /// <summary>The hop's min RTT outside the episode (recovery, else control), when a round had one.</summary>
        public double? BaselineMinRttMs { get; set; }
    }

    /// <summary>The analysis over the rounds so far, and which hops' own series decided them.</summary>
    private sealed record Analysis(EpisodeVerdict Verdict, Dictionary<string, bool> Anchors, HashSet<string> Pinged, HashSet<string> Monitored,
        Dictionary<string, double> MonitoredLoss, HashSet<string> AccessHops);

    /// <summary>Most hops pinged in one round, across the episode's targets.</summary>
    private const int MaxPingedHopsPerRound = 8;

    /// <summary>Echoes a hop needs before its series decides it.</summary>
    private const int MinHopPings = 10;

    /// <summary>
    /// One latency result for a target, as recorded. Cheap: folds the sample into the target's window
    /// and, when a window closes on a degraded path target, starts a gate evaluation in the background.
    /// </summary>
    public void Observe(MonitoringTarget target, WanContext? context, PathVantage vantage,
        DateTime timestamp, double? rttAvgMs, int sent, int received)
    {
        var role = RoleOf(target);
        if (role == null) return;
        var now = DateTime.UtcNow;
        // Replayed backlog after an agent reconnect describes the past, not a degradation to trace now.
        if (now - timestamp > TimeSpan.FromMinutes(_options.PathTargetStaleMinutes)) return;

        var key = target.WanContextId ?? 0;
        var watched = new Watched(target.Id, target.TargetId, string.IsNullOrEmpty(target.Name) ? target.Address : target.Name,
            target.Address, role.Value, key, target.WanContextId, context?.WanInterface, vantage);

        bool evaluate;
        lock (_lock)
        {
            if (!_started)
            {
                _started = true;
                _ = Task.Run(StartAsync);
            }
            _watched[target.TargetId] = watched;
            if (!_trackers.TryGetValue(target.TargetId, out var tracker))
                _trackers[target.TargetId] = tracker = new TargetDegradation(target.TargetId, _options);
            var closed = tracker.Record(timestamp, rttAvgMs, sent, received,
                () => _alerts.GetFor(_slug).Targets.IsWanOutageOpen(context?.WanInterface) || InOpenEpisode(target.TargetId, now));
            evaluate = closed != null && role == GateRole.Path && tracker.Degraded && _evaluating.Add(key);
        }
        if (evaluate) _ = EvaluateContextAsync(key);
    }

    /// <summary>
    /// Whether an episode with this id is open with path rounds still to run. One that has used them
    /// all stays open until its targets clear, but is no longer tracing.
    /// </summary>
    public bool IsTracing(int episodeId)
    {
        lock (_lock) return _runs.Values.Any(r => r.Row.Id == episodeId && r.Row.ClosedAt == null && RoundsLeft(r));
    }

    /// <summary>The most path rounds any degraded target of this episode has run; null when it is not running here.</summary>
    public int? RoundsTraced(int episodeId)
    {
        lock (_lock) return _runs.Values.FirstOrDefault(r => r.Row.Id == episodeId)?.Degraded.Select(d => d.RoundsRun).DefaultIfEmpty(0).Max();
    }

    private bool RoundsLeft(EpisodeRun run)
    {
        var maxRounds = run.Metered ? 1 : _options.PathMaxRoundsPerTarget;
        return run.Degraded.Any(d => d.RoundsRun < maxRounds);
    }

    /// <summary>
    /// A local Custom target (the Network Performance LAN filter's rule) gets no role: its trouble is a
    /// LAN link, which a forward trace cannot localize, and it says nothing about the Internet path as
    /// a control or in the site-wide count.
    /// </summary>
    internal static GateRole? RoleOf(MonitoringTarget t) => t.TargetType switch
    {
        MonitoringTargetType.Custom when LocalTargetResolver.IsLocal(t) => null,
        MonitoringTargetType.InternetService or MonitoringTargetType.Custom => GateRole.Path,
        MonitoringTargetType.AccessIsp => GateRole.Access,
        _ when t.DiscoveryMethod == DiscoveryMethod.L2Neighbor => GateRole.Access,
        MonitoringTargetType.Transit => GateRole.Other,
        _ => null
    };

    /// <summary>A degraded member of an open episode keeps its windows out of its own baseline - until the
    /// episode outlives its time limit, after which the level it sits at is the new normal.</summary>
    private bool InOpenEpisode(string targetId, DateTime now) => _runs.Values.Any(r =>
        r.Row.ClosedAt == null
        && now - r.StartedAt < TimeSpan.FromHours(_options.PathMaxEpisodeHours)
        && r.Degraded.Any(d => string.Equals(d.Target.TargetId, targetId, StringComparison.OrdinalIgnoreCase)));

    // ---- Start-up: resume what a restart interrupted, seed baselines from history ----

    private async Task StartAsync()
    {
        try
        {
            await ResumeEpisodesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Path Localization could not resume interrupted episodes for site {Slug}", _slug);
        }
        await SeedBaselinesAsync();
    }

    /// <summary>
    /// Picks up every episode a restart interrupted: still tracing, or closed and waiting on its
    /// recovery round. Its rounds, pinned address, vantage, hop pings and metered flag are all stored, so
    /// it carries on where it stopped; only the degraded targets' state has to be put back, since seeded
    /// history never sets it. An episode that cannot be rebuilt (its targets are gone) is closed instead.
    /// </summary>
    private async Task ResumeEpisodesAsync()
    {
        await using var db = _siteDb.CreateForSite(_slug, _isDefault);
        var repo = new PathEpisodeRepository(db);
        var interrupted = (await repo.GetOpenAsync())
            .Concat((await repo.GetClosedWithoutRecoveryAsync())
                .Where(e => e.Summary?.EndsWith(WaitingOnRecoveryNote, StringComparison.Ordinal) == true))
            .ToList();
        if (interrupted.Count == 0) return;

        var targetIds = interrupted.SelectMany(e => e.Targets.Select(t => t.MonitoringTargetId)).Distinct().ToList();
        var targets = await db.MonitoringTargets.AsNoTracking().Where(t => targetIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id);
        var contexts = await db.WanContexts.AsNoTracking().ToDictionaryAsync(c => c.Id);
        var now = DateTime.UtcNow;

        foreach (var episode in interrupted.OrderByDescending(e => e.OpenedAt))
        {
            var full = await repo.GetWithTracesAsync(episode.Id);
            var run = full == null ? null : RebuildRun(full, targets, contexts, now);
            if (run == null)
            {
                if (full is { ClosedAt: null })
                {
                    full.ClosedAt = full.UpdatedAt;
                    full.Summary = $"{full.Summary} Tracing stopped when Network Optimizer restarted.".Trim();
                    await repo.UpdateAsync(full);
                }
                continue;
            }

            lock (_lock)
            {
                // One episode per WAN context: the newest wins if a restart somehow left two.
                if (_runs.ContainsKey(run.ContextKey)) continue;
                _runs[run.ContextKey] = run;
                foreach (var t in run.Degraded.Concat(run.Control is { } c ? new[] { c } : Array.Empty<RunTarget>()))
                    _watched.TryAdd(t.Target.TargetId, t.Target);
                if (run.Row.ClosedAt == null)
                {
                    foreach (var d in run.Degraded)
                    {
                        if (!_trackers.TryGetValue(d.Target.TargetId, out var tracker))
                            _trackers[d.Target.TargetId] = tracker = new TargetDegradation(d.Target.TargetId, _options);
                        var stored = run.Row.Targets.FirstOrDefault(x => x.MonitoringTargetId == d.Target.Id && x.Role == PathEpisodeTargetRole.Degraded);
                        tracker.ResumeDegraded(PathEpisodeTraceMapping.Utc(run.Row.OpenedAt), stored?.PeakLossPct, stored?.PeakRttMs, now);
                    }
                }
            }
            _logger.LogInformation("Path Localization resumed episode {Id} on site {Slug} after a restart", run.Row.Id, _slug);
        }
    }

    /// <summary>
    /// The vantage a resumed target traces from: its last trace's, else the context's. The bind is what
    /// the context's agent is pushed (AgentProbeResultSink.ResolveSpecSourceIp): its interface, else its
    /// source IP. A guessed vantage is replaced by the target's next live observation (AdoptLiveVantage).
    /// </summary>
    internal static PathVantage ResumedVantage(PathEpisodeTrace? last, WanContext? context) => last != null
        ? PathVantage.FromLabel(last.Vantage, last.SourceInterface)
        : new PathVantage(context?.AgentId, context?.InterfaceName ?? context?.ProbeSourceIp);

    /// <summary>An interrupted episode's run, rebuilt from what it stored; null when its targets are gone.</summary>
    private EpisodeRun? RebuildRun(PathEpisode episode, IReadOnlyDictionary<int, MonitoringTarget> targets,
        IReadOnlyDictionary<int, WanContext> contexts, DateTime now)
    {
        var key = episode.WanContextId ?? 0;
        var context = episode.WanContextId is int id ? contexts.GetValueOrDefault(id) : null;
        var traces = episode.Traces.OrderBy(t => t.Timestamp).ToList();

        RunTarget? Rebuild(PathEpisodeTarget stored)
        {
            if (!targets.TryGetValue(stored.MonitoringTargetId, out var mt)) return null;
            var mine = traces.Where(t => t.MonitoringTargetId == mt.Id).ToList();
            var last = mine.LastOrDefault();
            var watched = new Watched(mt.Id, mt.TargetId, string.IsNullOrEmpty(mt.Name) ? mt.Address : mt.Name, mt.Address,
                RoleOf(mt) ?? GateRole.Path, key, mt.WanContextId, context?.WanInterface, ResumedVantage(last, context));

            var run = new RunTarget
            {
                Target = watched,
                VantageGuessed = last == null,
                Role = stored.Role,
                ResolvedAddress = mine.Select(t => t.ResolvedAddress).LastOrDefault(a => !string.IsNullOrEmpty(a)),
                RoundsRun = mine.Count(t => t.Kind == PathEpisodeTraceKind.Path),
                NextRoundAt = now
            };
            run.ResolveTried = run.ResolvedAddress != null;
            foreach (var t in mine)
            {
                if (PathEpisodeTraceMapping.ToResult(t, mt.Address) is not { } result) continue;
                if (t.Kind == PathEpisodeTraceKind.Recovery) run.Recovery = result;
                else run.Rounds.Add(result);
            }
            return run;
        }

        var degraded = episode.Targets.Where(t => t.Role == PathEpisodeTargetRole.Degraded).Select(Rebuild).OfType<RunTarget>().ToList();
        if (degraded.Count == 0) return null;
        var control = episode.Targets.Where(t => t.Role == PathEpisodeTargetRole.Control).Select(Rebuild).FirstOrDefault(c => c != null);
        var snapshot = PathGateSnapshot.Read(episode.GateSnapshot);
        episode.Traces.Clear();

        var run = new EpisodeRun
        {
            Row = episode,
            ContextKey = key,
            StartedAt = traces.Count > 0 ? PathEpisodeTraceMapping.Utc(traces[0].Timestamp) : PathEpisodeTraceMapping.Utc(episode.OpenedAt),
            Metered = snapshot.Metered,
            Control = control,
            ControlTraced = control == null || control.Rounds.Count > 0
        };
        run.Degraded.AddRange(degraded);
        foreach (var (address, ping) in snapshot.HopPings)
            run.HopPings[address] = new HopPing { TargetId = degraded[0].Target.TargetId, Sent = ping.Sent, Received = ping.Received, MinRttMs = ping.MinRttMs };
        foreach (var (address, ms) in snapshot.SeriesBaselines)
            run.SeriesBaselines[address] = ms;
        if (episode.ClosedAt is DateTime closed)
        {
            var due = PathEpisodeTraceMapping.Utc(closed) + TimeSpan.FromMinutes(_options.PathRecoveryDelayMinutes);
            run.RecoveryDue = due > now ? due : now;
        }
        return run;
    }

    /// <summary>
    /// Loads each candidate target's trailing day from InfluxDB as 5-minute means (pushdown-only, one
    /// target at a time), so a restart does not cost a day of baseline. Past episodes stay out of it.
    /// </summary>
    private async Task SeedBaselinesAsync()
    {
        try
        {
            List<MonitoringTarget> targets;
            List<(DateTime Start, DateTime End)> past;
            await using (var db = _siteDb.CreateForSite(_slug, _isDefault))
            {
                targets = await db.MonitoringTargets.AsNoTracking()
                    .Where(t => t.Enabled && t.RetiredAt == null)
                    .ToListAsync();
                var since = DateTime.UtcNow - TimeSpan.FromHours(_options.PathBaselineHours);
                past = (await db.PathEpisodes.AsNoTracking()
                        .Where(e => e.ClosedAt == null || e.ClosedAt >= since)
                        .Select(e => new { e.OpenedAt, e.ClosedAt })
                        .ToListAsync())
                    .Select(e => (e.OpenedAt, e.ClosedAt ?? DateTime.UtcNow))
                    .ToList();
            }

            var influx = _influx.GetFor(_slug);
            var window = TimeSpan.FromMinutes(_options.PathDegradationWindowMinutes);
            foreach (var target in targets.Where(t => RoleOf(t) != null))
            {
                var to = DateTime.UtcNow;
                var points = await influx.QueryLatencyDetailByTargetIdAsync(target.TargetId,
                    to - TimeSpan.FromHours(_options.PathBaselineHours), to, window);
                if (points.Count == 0) continue;
                lock (_lock)
                {
                    if (!_trackers.TryGetValue(target.TargetId, out var tracker))
                        _trackers[target.TargetId] = tracker = new TargetDegradation(target.TargetId, _options);
                    // aggregateWindow stamps each mean at its window's END; the tracker keys by start.
                    tracker.Seed(points.Select(p => (p.Time - window, p.RttAvgMs, p.LossPercent)), past);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Path Localization could not seed baselines for site {Slug}; they build from live samples", _slug);
        }
    }

    // ---- The gate ----

    private async Task EvaluateContextAsync(int key)
    {
        try
        {
            await EvaluateContextCoreAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Path Localization gate evaluation failed for site {Slug}", _slug);
        }
        finally
        {
            lock (_lock) _evaluating.Remove(key);
        }
    }

    private async Task EvaluateContextCoreAsync(int key)
    {
        var now = DateTime.UtcNow;
        List<GateTarget> gateTargets;
        List<Watched> members;
        lock (_lock)
        {
            (gateTargets, members) = GateTargetsFor(key, now);

            if (_runs.TryGetValue(key, out var run))
            {
                // Already tracing this WAN: a target that degrades a few minutes after the rest joins,
                // inside the budget. One waiting on recovery whose targets degraded again resumes.
                // A run mid-round is left alone: its recovery traces may be in flight, and it checks for
                // a renewed degradation itself before taking them.
                if (run.Row.ClosedAt == null)
                    JoinDegraded(run, gateTargets, members, now);
                else if (!run.Busy && AnyDegraded(run, now))
                    Resume(run, now);
                return;
            }
        }
        if (!gateTargets.Any(t => t.Role == GateRole.Path && t.Degraded)) return;

        var wanInterface = members.Select(m => m.WanInterface).FirstOrDefault(w => w != null);
        var utilization = await UtilizationAsync(wanInterface, now);
        var gate = PathEpisodeGate.Evaluate(gateTargets, utilization, _options.CongestionLoadHighFraction,
            _alerts.GetFor(_slug).Targets.IsWanOutageOpen(wanInterface));
        lock (_lock)
        {
            if (!_lastBlocked.TryGetValue(key, out var last) || last != gate.BlockedBy)
            {
                _lastBlocked[key] = gate.BlockedBy;
                if (!gate.Passed)
                    _logger.LogDebug("Path Localization on site {Slug} not tracing: {Reason}", _slug, gate.BlockedBy);
            }
        }
        if (!gate.Passed) return;

        await OpenEpisodeAsync(key, gate, members, utilization, now);
    }

    /// <summary>The context's fresh targets as the gate sees them, and every target it holds. Caller holds the lock.</summary>
    private (List<GateTarget> Targets, List<Watched> Members) GateTargetsFor(int key, DateTime now)
    {
        var members = _watched.Values.Where(w => w.ContextKey == key).ToList();
        var targets = members
            .Where(w => _trackers.TryGetValue(w.TargetId, out var t) && t.IsFresh(now))
            .Select(w =>
            {
                var t = _trackers[w.TargetId];
                return new GateTarget(w.TargetId, w.Role, t.Degraded && !Exhausted(w.TargetId, t), t.IsClean(now), t.PeakLossPct, t.RttRiseMs,
                    t.ElevatedSince(now - RecentSpan) == true);
            })
            .ToList();
        return (targets, members);
    }

    /// <summary>
    /// Runs the gate again for an open episode before a round. What held when it opened can stop
    /// holding - the WAN goes down, the whole site degrades, a download saturates the link - and rounds
    /// taken then measure that, not the path-specific degradation the episode is about. Null when the
    /// round may run, else why not.
    /// </summary>
    private async Task<string?> PausedByAsync(EpisodeRun run, DateTime now)
    {
        List<GateTarget> targets;
        string? wanInterface;
        lock (_lock)
        {
            List<Watched> members;
            (targets, members) = GateTargetsFor(run.ContextKey, now);
            wanInterface = members.Select(m => m.WanInterface).FirstOrDefault(w => w != null);
        }
        var gate = PathEpisodeGate.Evaluate(targets, await UtilizationAsync(wanInterface, now),
            _options.CongestionLoadHighFraction, _alerts.GetFor(_slug).Targets.IsWanOutageOpen(wanInterface),
            requireCleanControl: false);
        // Nothing degraded is the episode clearing, which the round logic closes on; not a pause.
        return gate.Passed || gate.Degraded.Count == 0 ? null : gate.BlockedBy;
    }

    /// <summary>Whether a degraded target is still degraded and still reporting. One that stopped
    /// reporting keeps its last state forever, which says nothing about the path now.</summary>
    private bool StillDegraded(RunTarget d, DateTime now) =>
        _trackers.TryGetValue(d.Target.TargetId, out var t) && t.Degraded && t.IsFresh(now);

    private bool AnyDegraded(EpisodeRun run, DateTime now) => run.Degraded.Any(d => StillDegraded(d, now));

    private bool Exhausted(string targetId, TargetDegradation t) =>
        _exhausted.TryGetValue(targetId, out var since) && since == t.DegradedSince;

    /// <summary>
    /// The same targets degraded again before the recovery round: still one episode, so it reopens. The
    /// renewed degradation gets its own rounds - the ones it opened with went to the first.
    /// </summary>
    private static void Resume(EpisodeRun run, DateTime now)
    {
        run.Row.ClosedAt = null;
        run.RecoveryDue = null;
        run.Dirty = true;
        foreach (var d in run.Degraded)
        {
            d.NextRoundAt = now;
            d.RoundsRun = 0;
        }
    }

    private void JoinDegraded(EpisodeRun run, List<GateTarget> gateTargets, List<Watched> members, DateTime now)
    {
        foreach (var g in gateTargets.Where(g => g.Role == GateRole.Path && g.Degraded)
                     .OrderByDescending(g => g.LossPct).ThenByDescending(g => g.RttRiseMs))
        {
            if (run.Degraded.Count >= _options.PathMaxTracedTargets) break;
            if (run.Degraded.Any(d => d.Target.TargetId.Equals(g.TargetId, StringComparison.OrdinalIgnoreCase))) continue;
            var w = members.First(m => m.TargetId == g.TargetId);
            run.Degraded.Add(new RunTarget { Target = w, Role = PathEpisodeTargetRole.Degraded, NextRoundAt = now });
        }
    }

    private async Task<double?> UtilizationAsync(string? wanInterface, DateTime now)
    {
        try
        {
            return await _ispHealth.GetFor(_slug, wanInterface).GetMedianUtilizationAsync(now - LoadLookback, now);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Path Localization could not read WAN load for site {Slug}", _slug);
            return null;
        }
    }

    private async Task OpenEpisodeAsync(int key, GateResult gate, List<Watched> members, double? utilization, DateTime now)
    {
        var byId = members.ToDictionary(m => m.TargetId, StringComparer.OrdinalIgnoreCase);
        var traced = gate.Degraded.Take(_options.PathMaxTracedTargets).Select(g => byId[g.TargetId]).ToList();
        var candidates = gate.CleanControls.Select(g => byId[g.TargetId]).ToList();

        await using var db = _siteDb.CreateForSite(_slug, _isDefault);
        var ids = traced.Concat(candidates).Select(t => t.Id).ToList();
        var ancestry = (await db.UpstreamDiscoveries.AsNoTracking()
                .Where(d => d.MonitoringTargetId != null && ids.Contains(d.MonitoringTargetId.Value))
                .Select(d => new { d.MonitoringTargetId, d.AncestorHopIps })
                .ToListAsync())
            .GroupBy(d => d.MonitoringTargetId!.Value)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)(g.First().AncestorHopIps ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        IReadOnlyList<string> AncestryOf(Watched w) => ancestry.TryGetValue(w.Id, out var a) ? a : Array.Empty<string>();

        // The control that shares the most forward hops with the worst degraded target gives the
        // closest "clean through here" comparison inside the same minute.
        var worst = AncestryOf(traced[0]);
        var control = candidates
            .OrderByDescending(c => AncestryOf(c).Intersect(worst, StringComparer.OrdinalIgnoreCase).Count())
            .FirstOrDefault();

        var metered = await IsMeteredAsync(db, members.Select(m => m.WanInterface).FirstOrDefault(w => w != null), key == 0);
        var trackers = SnapshotTrackers(traced);
        var opened = traced.Select(t => trackers[t.TargetId].DegradedSince).Where(d => d.HasValue).Min() ?? now;

        var row = new PathEpisode
        {
            WanContextId = key == 0 ? null : key,
            WanInterface = traced[0].WanInterface,
            OpenedAt = opened,
            UpdatedAt = now,
            GateSnapshot = JsonSerializer.Serialize(new PathGateSnapshot
            {
                Degraded = gate.Degraded.Select(d => new PathGateSnapshot.DegradedTarget(d.TargetId, d.LossPct, d.RttRiseMs)).ToList(),
                CleanControls = gate.CleanControls.Select(c => c.TargetId).ToList(),
                Utilization = utilization,
                Metered = metered
            }),
            Summary = "Tracing the degraded paths."
        };
        foreach (var t in traced)
            row.Targets.Add(TargetRow(t, PathEpisodeTargetRole.Degraded, trackers[t.TargetId]));
        if (control != null)
            row.Targets.Add(new PathEpisodeTarget { MonitoringTargetId = control.Id, Role = PathEpisodeTargetRole.Control });

        lock (_lock)
        {
            // Another evaluation may have opened one while this was reading the database.
            if (_runs.ContainsKey(key)) return;
        }
        await new PathEpisodeRepository(db).AddAsync(row);

        var run = new EpisodeRun
        {
            Row = row,
            ContextKey = key,
            StartedAt = now,
            Metered = metered,
            Control = control == null ? null : new RunTarget { Target = control, Role = PathEpisodeTargetRole.Control }
        };
        foreach (var t in traced)
            run.Degraded.Add(new RunTarget { Target = t, Role = PathEpisodeTargetRole.Degraded, NextRoundAt = now });
        lock (_lock) _runs[key] = run;

        _logger.LogInformation("Path Localization opened episode {Id} on site {Slug}: tracing {Targets}", row.Id, _slug,
            string.Join(", ", traced.Select(t => t.Name)));
    }

    private Dictionary<string, TargetDegradation> SnapshotTrackers(IEnumerable<Watched> targets)
    {
        lock (_lock) return targets.ToDictionary(t => t.TargetId, t => _trackers[t.TargetId], StringComparer.OrdinalIgnoreCase);
    }

    private static PathEpisodeTarget TargetRow(Watched t, PathEpisodeTargetRole role, TargetDegradation tracker) => new()
    {
        MonitoringTargetId = t.Id,
        Role = role,
        BaselineRttMs = tracker.BaselineRttMs,
        BaselineLossPct = tracker.BaselineLossPct,
        PeakRttMs = tracker.PeakRttMs,
        PeakLossPct = tracker.PeakLossPct
    };

    /// <summary>
    /// Whether the WAN's traffic costs money (<see cref="MeteredProbePolicy"/>): a Data Usage cap on it,
    /// or a limited access technology for the primary path. A metered WAN gets the control and one path
    /// round, and no recovery round.
    /// </summary>
    private async Task<bool> IsMeteredAsync(NetworkOptimizerDbContext db, string? wanInterface, bool primary)
    {
        try
        {
            var technology = primary ? _tracers.GetFor(_slug).State.AccessTechnology : AccessTechnology.Unknown;
            var key = wanInterface ?? (primary ? _tracers.GetFor(_slug).State.WanInterface : null);
            var capped = false;
            if (key != null)
            {
                var wanKey = GatewayWanHelper.WanInterfaceKeyFromKey(key);
                var caps = await db.WanDataUsageConfigs.AsNoTracking().Where(c => c.DataCapGb > 0).Select(c => c.WanKey).ToListAsync();
                capped = caps.Any(k => string.Equals(GatewayWanHelper.WanInterfaceKeyFromKey(k), wanKey, StringComparison.OrdinalIgnoreCase));
            }
            return MeteredProbePolicy.For(technology, capped).Rung > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Path Localization could not read the metered state for site {Slug}; treating as unmetered", _slug);
            return false;
        }
    }

    // ---- Rounds ----

    /// <summary>Runs whatever rounds are due on every episode in flight. Called by the runner every few seconds.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        List<EpisodeRun> due;
        lock (_lock)
        {
            due = _runs.Values.Where(r => !r.Busy).ToList();
            foreach (var r in due) r.Busy = true;
        }
        // Episodes on different WANs advance side by side; _traceSlots serializes the traces they run.
        await Task.WhenAll(due.Select(run => AdvanceGuardedAsync(run, ct)));
    }

    private async Task AdvanceGuardedAsync(EpisodeRun run, CancellationToken ct)
    {
        try
        {
            await AdvanceAsync(run, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Path Localization episode {Id} on site {Slug} failed a round", run.Row.Id, _slug);
        }
        finally
        {
            lock (_lock) run.Busy = false;
        }
    }

    private async Task AdvanceAsync(EpisodeRun run, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var maxRounds = run.Metered ? 1 : _options.PathMaxRoundsPerTarget;
        var interval = TimeSpan.FromSeconds(_options.PathRoundIntervalSeconds);
        bool roundDue, recoveryDue;
        lock (_lock)
        {
            roundDue = run.Row.ClosedAt == null
                && ((!run.ControlTraced && run.Control != null)
                    || run.Degraded.Any(d => StillDegraded(d, now) && d.RoundsRun < maxRounds && d.NextRoundAt <= now));
            recoveryDue = run.Row.ClosedAt != null && run.RecoveryDue <= now;
        }

        // The gate is asked again before a round, at most once a round interval: it reads WAN load,
        // which is not worth doing every tick, and a pause is re-checked on the same cadence.
        if (roundDue && now >= run.NextGateCheckAt)
        {
            run.NextGateCheckAt = now + interval;
            var pausedBy = await PausedByAsync(run, now);
            if (pausedBy != run.PausedBy)
            {
                if (pausedBy != null && run.PausedSince == null) run.PausedSince = now;
                if (pausedBy == null && run.PausedSince is DateTime since)
                {
                    run.PausedTotal += now - since;
                    run.PausedSince = null;
                }
                run.PausedBy = pausedBy;
                run.Dirty = true;
                if (pausedBy != null)
                    _logger.LogDebug("Path Localization episode {Id} on site {Slug} paused: {Reason}", run.Row.Id, _slug, pausedBy);
            }
        }

        // The recovery round is the per-hop baseline, so it waits for a moment the rest of the
        // connection is clean too - and gives up after an hour rather than wait forever.
        if (recoveryDue && await RecoveryBlockedByAsync(run, now) is string blocked)
        {
            recoveryDue = false;
            if (now - run.Row.ClosedAt!.Value - TimeSpan.FromMinutes(_options.PathRecoveryDelayMinutes) >= TimeSpan.FromHours(1))
            {
                run.RecoveryDue = null;
                run.StopNote = $"The recovery round was skipped: {blocked} for the hour after it was due.";
                await AnalyzeAndSaveAsync(run, ct);
                lock (_lock) _runs.Remove(run.ContextKey);
                return;
            }
            run.RecoveryDue = now + interval;
        }

        List<(RunTarget Target, PathEpisodeTraceKind Kind)> work = new();
        bool closing = false, recovering = false, dirty;
        DateTime? clearedAt = null;
        lock (_lock)
        {
            if (run.Row.ClosedAt == null)
            {
                // A paused episode's rounds wait the pause out; it still closes if its targets clear.
                if (run.PausedBy == null)
                {
                    if (!run.ControlTraced && run.Control != null)
                    {
                        work.Add((run.Control, PathEpisodeTraceKind.Control));
                        run.ControlTraced = true;
                    }
                    foreach (var d in run.Degraded)
                    {
                        if (StillDegraded(d, now) && d.RoundsRun < maxRounds && d.NextRoundAt <= now)
                        {
                            work.Add((d, PathEpisodeTraceKind.Path));
                            d.RoundsRun++;
                            d.NextRoundAt = now + interval;
                        }
                    }
                }
                var trackers = run.Degraded.Select(d => _trackers.GetValueOrDefault(d.Target.TargetId)).ToList();
                // Time spent paused is not time spent tracing, so it does not count toward the limit.
                var paused = run.PausedTotal + (run.PausedSince is DateTime ps ? now - ps : TimeSpan.Zero);
                var timedOut = now - run.StartedAt - paused >= TimeSpan.FromHours(_options.PathMaxEpisodeHours);
                var silent = trackers.Any(t => t is { Degraded: true } && !t.IsFresh(now));
                if (!AnyDegraded(run, now) || timedOut)
                {
                    closing = true;
                    clearedAt = trackers.Select(t => t?.ClearedAt).Where(c => c.HasValue).Max();
                    if (timedOut)
                    {
                        run.StopNote = $"Still degraded after {_options.PathMaxEpisodeHours} hours of tracing, so tracing stopped.";
                        // The level it is stuck at is this degradation's, and it gets no second episode.
                        foreach (var d in run.Degraded)
                            if (_trackers.TryGetValue(d.Target.TargetId, out var t) && t.Degraded)
                                _exhausted[d.Target.TargetId] = t.DegradedSince;
                    }
                    else if (silent)
                    {
                        run.StopNote = "A degraded target stopped reporting, so tracing stopped.";
                    }
                }
            }
            else if (recoveryDue)
            {
                // A recovery round taken while the path is degraded again would be no baseline at all.
                if (AnyDegraded(run, now))
                    Resume(run, now);
                else
                {
                    recovering = true;
                    work.AddRange(run.Degraded.Select(d => (d, PathEpisodeTraceKind.Recovery)));
                }
            }
            dirty = run.Dirty;
            run.Dirty = false;
        }

        if (work.Count > 0)
            await Task.WhenAll(work.Select(w => TraceBoundedAsync(run, w.Target, w.Kind, ct)));
        // After a path round, so the hops it read as elevated get their echoes before the verdict is saved.
        if (work.Any(w => w.Kind == PathEpisodeTraceKind.Path))
            await PingHopsAsync(run, ct);

        if (closing)
        {
            run.Row.ClosedAt = run.StopNote != null ? now : clearedAt ?? now;
            // A pause still open ends with the episode, so a resume starts its time limit honestly.
            if (run.PausedSince is DateTime pausedAt) run.PausedTotal += now - pausedAt;
            run.PausedSince = null;
            run.PausedBy = null;
            // A metered WAN skips the recovery round, and one that stopped short never recovered.
            run.RecoveryDue = run.Metered || run.StopNote != null
                ? null
                : run.Row.ClosedAt + TimeSpan.FromMinutes(_options.PathRecoveryDelayMinutes);
        }
        if (recovering) run.Row.RecoveryTracedAt = now;

        if (work.Count > 0 || closing || recovering || dirty)
            await AnalyzeAndSaveAsync(run, ct);

        if ((closing && run.RecoveryDue == null) || recovering)
        {
            lock (_lock) _runs.Remove(run.ContextKey);
            _logger.LogInformation("Path Localization closed episode {Id} on site {Slug}: {Label}", run.Row.Id, _slug, run.Row.BottleneckLabel);
        }
    }

    /// <summary>
    /// Why the recovery round must wait, or null when it may run: the baseline it records is only one
    /// if the rest of the connection is clean when it is taken - no outage, a clean access layer, and a
    /// WAN that is not loaded.
    /// </summary>
    private async Task<string?> RecoveryBlockedByAsync(EpisodeRun run, DateTime now)
    {
        List<GateTarget> targets;
        string? wanInterface;
        lock (_lock)
        {
            List<Watched> members;
            (targets, members) = GateTargetsFor(run.ContextKey, now);
            wanInterface = members.Select(m => m.WanInterface).FirstOrDefault(w => w != null);
        }
        if (_alerts.GetFor(_slug).Targets.IsWanOutageOpen(wanInterface)) return "an outage was open";
        if (targets.Any(t => t.Role == GateRole.Access && t.Degraded)) return "the access layer was degraded";
        return await UtilizationAsync(wanInterface, now) is double u && u >= _options.CongestionLoadHighFraction
            ? "the WAN was loaded"
            : null;
    }

    private async Task TraceBoundedAsync(EpisodeRun run, RunTarget target, PathEpisodeTraceKind kind, CancellationToken ct)
    {
        await _traceSlots.WaitAsync(ct);
        try
        {
            // Back to back, a trace starts while the gateway is still refilling its Time Exceeded
            // bucket from the last one's TTL-1 burst, and the first hop reads as lossy.
            var wait = _lastTraceEnded + TraceGap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            await TraceAsync(run, target, kind, ct);
        }
        finally
        {
            _lastTraceEnded = DateTime.UtcNow;
            _traceSlots.Release();
        }
    }

    /// <summary>The gap between one trace and the next: a 5-probe TTL-1 burst refills at one token a second.</summary>
    private static readonly TimeSpan TraceGap = TimeSpan.FromSeconds(5);

    // Written and read only while holding _traceSlots.
    private DateTime _lastTraceEnded = DateTime.MinValue;

    private async Task TraceAsync(EpisodeRun run, RunTarget target, PathEpisodeTraceKind kind, CancellationToken ct)
    {
        PathVantage vantage;
        lock (_lock) vantage = AdoptLiveVantage(target);
        var executor = ExecutorFor(vantage);
        // A hostname is pinned to one address for the whole episode: a name that rotates (a CDN, an
        // AWS endpoint) would otherwise send each round, and the recovery round, down a different
        // path, and pooling those per TTL compares different routers. Resolved once from the vantage;
        // if that fails, the first trace's own header names the address it went to.
        var isHostname = !System.Net.IPAddress.TryParse(target.Target.Address, out _);
        if (isHostname && target.ResolvedAddress == null && !target.ResolveTried)
        {
            target.ResolveTried = true;
            try
            {
                var lookup = await executor.LookupAsync(
                    new ProbeTarget(target.Target.Address, ProbeMode.Icmp, null, vantage.SourceInterface), ct: ct);
                target.ResolvedAddress = lookup.Addresses.FirstOrDefault(a =>
                    System.Net.IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Path Localization could not resolve {Target} from its vantage", target.Target.Address);
            }
        }

        // ICMP whatever the target's own probe mode: a TCP-only destination simply never answers the
        // last TTL, and its own series still speaks for it.
        var probe = new ProbeTarget(target.ResolvedAddress ?? target.Target.Address, ProbeMode.Icmp, null, vantage.SourceInterface);
        var result = await executor.TracerouteAsync(
            probe,
            maxHops: _options.PathMaxHops,
            perHopTimeout: PerHopTimeout,
            totalDeadline: TraceDeadline,
            probesPerHop: _options.PathProbesPerHop,
            ct: ct);

        if (isHostname)
        {
            // Traced by name (the lookup failed): pin whatever its header says it went to.
            target.ResolvedAddress ??= result.ResolvedAddress;
            // The analyzer finds the destination hop by this address; a trace sent to the pinned
            // literal reports none of its own.
            if (target.ResolvedAddress != null)
                result = result with { ResolvedAddress = target.ResolvedAddress };
        }

        if (result.Hops.Count > 0)
        {
            lock (_lock)
            {
                if (kind == PathEpisodeTraceKind.Recovery) target.Recovery = result;
                else target.Rounds.Add(result);
            }
        }
        else
        {
            _logger.LogDebug("Path Localization trace to {Target} returned no hops: {Error}", target.Target.Name, result.ErrorMessage);
        }

        await using var db = _siteDb.CreateForSite(_slug, _isDefault);
        await new PathEpisodeRepository(db).AddTraceAsync(new PathEpisodeTrace
        {
            EpisodeId = run.Row.Id,
            MonitoringTargetId = target.Target.Id,
            Kind = kind,
            Timestamp = result.Timestamp,
            Vantage = vantage.Label,
            SourceInterface = vantage.SourceInterface,
            Reached = result.Reached,
            ResolvedAddress = result.ResolvedAddress,
            HopsJson = JsonSerializer.Serialize(result.Hops)
        }, ct);
    }

    /// <summary>
    /// Analyzes every round so far. A monitored hop is decided by its own series; an unmonitored one
    /// the episode has pinged enough, by those pings; anything else by the trace.
    /// </summary>
    private async Task<Analysis> AnalyzeAsync(EpisodeRun run, CancellationToken ct)
    {
        List<TargetTraces> degraded, controls;
        Dictionary<string, string> monitoredByAddress;
        HashSet<string> accessHops;
        lock (_lock)
        {
            degraded = run.Degraded.Select(ToTraces).ToList();
            controls = run.Control is { } c && c.Rounds.Count > 0 ? new List<TargetTraces> { ToTraces(c) } : new();
            monitoredByAddress = _watched.Values
                .Where(w => w.Role != GateRole.Path && w.ContextKey == run.ContextKey)
                .GroupBy(w => w.Address, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First().TargetId, StringComparer.OrdinalIgnoreCase);
            accessHops = _watched.Values
                .Where(w => w.Role == GateRole.Access && w.ContextKey == run.ContextKey)
                .Select(w => w.Address)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            void KeepBaseline(string? address, string targetId)
            {
                if (address != null && !run.SeriesBaselines.ContainsKey(address)
                    && _trackers.GetValueOrDefault(targetId)?.BaselineRttMs is double b)
                    run.SeriesBaselines[address] = b;
            }
            foreach (var (address, id) in monitoredByAddress) KeepBaseline(address, id);
            foreach (var d in run.Degraded)
            {
                KeepBaseline(d.Target.Address, d.Target.TargetId);
                KeepBaseline(d.ResolvedAddress, d.Target.TargetId);
                foreach (var r in d.Rounds) KeepBaseline(r.ResolvedAddress, d.Target.TargetId);
            }
        }
        var seriesBaselines = new Dictionary<string, double>(run.SeriesBaselines, StringComparer.OrdinalIgnoreCase);

        // AsnResolutionService caches every answer for the life of the process, so asking again for the
        // hops a previous round already resolved costs nothing.
        var asns = new Dictionary<string, AsnLookup?>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in PathTraceAnalyzer.AddressesOf(degraded.Concat(controls)))
            asns[address] = await ResolveAsnAsync(address, ct);

        var openedAt = run.Row.OpenedAt;
        var anchors = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var pinged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var monitoredLoss = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool? Anchor(string ip)
        {
            bool? monitored = null;
            lock (_lock)
            {
                if (monitoredByAddress.TryGetValue(ip, out var id) && _trackers.TryGetValue(id, out var t))
                {
                    monitored = t.ElevatedSince(openedAt);
                    // Only while the episode was open: the recovery wait after it would dilute the figure.
                    if (monitored != null && t.LossSince(openedAt, run.Row.ClosedAt) is { } series) monitoredLoss[ip] = series.LossPct;
                }
            }
            if (monitored is bool m) { anchors[ip] = m; return m; }
            if (PingAnchor(run, ip) is bool p) { anchors[ip] = p; pinged.Add(ip); return p; }
            return null;
        }
        string together;
        lock (_lock)
        {
            together = PathEpisodePresentation.Together(run.Degraded
                .Select(d => _trackers.GetValueOrDefault(d.Target.TargetId))
                .Where(t => t != null)
                .Select(t => ((double?)t!.PeakLossPct, t.BaselineRttMs, t.PeakRttMs)));
        }
        var verdict = PathTraceAnalyzer.Analyze(degraded, controls, ip => asns.GetValueOrDefault(ip), Anchor, _options, together, accessHops,
            seriesBaselines);
        return new Analysis(verdict, anchors, pinged, new HashSet<string>(monitoredByAddress.Keys, StringComparer.OrdinalIgnoreCase), monitoredLoss,
            accessHops);
    }

    /// <summary>
    /// A pinged hop's verdict: elevated on echo loss or a min-RTT rise over its baseline, clean
    /// otherwise. Null until it has enough echoes, and for a hop that answers no echo at all: that
    /// is a router ignoring ping, not one losing everything.
    /// </summary>
    private bool? PingAnchor(EpisodeRun run, string ip)
    {
        if (!run.HopPings.TryGetValue(ip, out var p) || p.Sent < MinHopPings || p.Received == 0) return null;
        var loss = 100.0 * (p.Sent - p.Received) / p.Sent;
        if (loss >= _options.PathHopLossElevatedPct) return true;
        return p.MinRttMs is double m && p.BaselineMinRttMs is double b
            && m - b >= Math.Max(_options.PathHopRttMinDeltaMs, b * _options.PathHopRttDeltaFraction);
    }

    /// <summary>
    /// Pings, from the target's own vantage and bind, every unmonitored hop the trace reads as elevated,
    /// plus the hops already being pinged, so the next analysis decides them on echoes.
    /// </summary>
    private async Task PingHopsAsync(EpisodeRun run, CancellationToken ct)
    {
        var analysis = await AnalyzeAsync(run, ct);
        Dictionary<string, PathVantage> vantageOf;
        lock (_lock) vantageOf = run.Degraded.ToDictionary(d => d.Target.TargetId, AdoptLiveVantage, StringComparer.OrdinalIgnoreCase);

        foreach (var t in analysis.Verdict.Targets)
        {
            if (!vantageOf.ContainsKey(t.TargetId)) continue;
            foreach (var h in t.Hops)
            {
                if (h.Address == null || h.Responses == 0 || h.Has(HopTag.Destination) || analysis.Monitored.Contains(h.Address)) continue;
                if (run.HopPings.TryGetValue(h.Address, out var known))
                {
                    known.BaselineMinRttMs ??= h.BaselineMinRttMs;
                    continue;
                }
                if (h.Elevated != true || run.HopPings.Count >= MaxPingedHopsPerRound) continue;
                run.HopPings[h.Address] = new HopPing { TargetId = t.TargetId, BaselineMinRttMs = h.BaselineMinRttMs };
            }
        }

        foreach (var (address, hop) in run.HopPings.ToList())
        {
            if (!vantageOf.TryGetValue(hop.TargetId, out var hopVantage)) continue;
            await _traceSlots.WaitAsync(ct);
            try
            {
                var result = await ExecutorFor(hopVantage).PingAsync(
                    new ProbeTarget(address, ProbeMode.Icmp, null, hopVantage.SourceInterface),
                    count: MinHopPings, perPingTimeout: PerHopTimeout, ct: ct);
                hop.Sent += result.Sent;
                hop.Received += result.Received;
                if (result.RttMinMs is double min) hop.MinRttMs = hop.MinRttMs is double cur ? Math.Min(cur, min) : min;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "Path Localization could not ping hop {Address}", address);
            }
            finally
            {
                _traceSlots.Release();
            }
        }
    }

    /// <summary>
    /// The vantage a target traces from. A resumed target with no trace yet carries a guess from its
    /// context; the first live observation after the restart names the exact agent and bind, and
    /// ResumeEpisodesAsync's TryAdd leaves that one in place, so a different instance there is the live one.
    /// Caller holds _lock.
    /// </summary>
    private PathVantage AdoptLiveVantage(RunTarget target)
    {
        if (target.VantageGuessed && _watched.TryGetValue(target.Target.TargetId, out var live)
            && !ReferenceEquals(live, target.Target))
        {
            target.Target = live;
            target.VantageGuessed = false;
        }
        return target.Target.Vantage;
    }

    // A vantage with no agent is the "server" vantage, which resolves as ProbeExecutorFactory.GetServer
    // does: a resumed target with no trace yet must not run from this server for a site its agent owns.
    internal IProbeExecutor ExecutorFor(PathVantage vantage) => vantage.AgentId is int agentId
        ? new AgentProbeExecutor(_agentProbe, _slug, _loggerFactory.CreateLogger<AgentProbeExecutor>(), agentId)
        : _coverage.AgentOwnsPathMeasurement(_slug)
            ? new AgentProbeExecutor(_agentProbe, _slug, _loggerFactory.CreateLogger<AgentProbeExecutor>())
            : _localProbe;

    /// <summary>Re-runs the analysis over every round so far and writes the verdict onto the episode.</summary>
    private async Task AnalyzeAndSaveAsync(EpisodeRun run, CancellationToken ct)
    {
        var (verdict, anchors, pinged, _, monitoredLoss, accessHops) = await AnalyzeAsync(run, ct);

        var row = run.Row;
        var snapshot = PathGateSnapshot.Read(row.GateSnapshot);
        snapshot.Anchors = anchors;
        snapshot.PingedHops = pinged.ToList();
        snapshot.MonitoredLoss = monitoredLoss;
        snapshot.AccessHops = accessHops.ToList();
        lock (_lock) snapshot.SeriesBaselines = new Dictionary<string, double>(run.SeriesBaselines, StringComparer.OrdinalIgnoreCase);
        snapshot.HopPings = run.HopPings.ToDictionary(kv => kv.Key,
            kv => new PathGateSnapshot.HopPingStats(kv.Value.Sent, kv.Value.Received, kv.Value.MinRttMs), StringComparer.OrdinalIgnoreCase);
        row.GateSnapshot = JsonSerializer.Serialize(snapshot);
        row.Scope = verdict.Scope.ToString();
        row.Disposition = verdict.Disposition.ToString();
        row.BottleneckHopIp = verdict.BottleneckHopIp;
        row.BottleneckHopNumber = verdict.BottleneckHopNumber;
        row.BottleneckLabel = Truncate(verdict.Label, 1000);
        row.ReturnPathFinding = verdict.ReturnFinding;
        var summary = verdict.Summary;
        bool roundsLeft;
        lock (_lock) roundsLeft = RoundsLeft(run);
        // Open with its rounds used up: no prefix, since the event row's Ongoing badge says it.
        if (row.ClosedAt == null)
            summary = run.PausedBy != null ? $"Paused: {run.PausedBy}. {summary}" : roundsLeft ? $"Tracing now. {summary}" : summary;
        else if (run.StopNote != null) summary += $" {run.StopNote}";
        else if (run.Metered) summary += " This WAN is metered, so it was traced once and no recovery round ran.";
        else if (row.RecoveryTracedAt == null) summary += WaitingOnRecoveryNote;
        row.Summary = Truncate(summary, 2000);
        row.UpdatedAt = DateTime.UtcNow;

        lock (_lock)
        {
            foreach (var d in run.Degraded)
            {
                var tracker = _trackers.GetValueOrDefault(d.Target.TargetId);
                var v = verdict.Targets.FirstOrDefault(t => t.TargetId.Equals(d.Target.TargetId, StringComparison.OrdinalIgnoreCase));
                var stored = row.Targets.FirstOrDefault(t => t.MonitoringTargetId == d.Target.Id && t.Role == PathEpisodeTargetRole.Degraded);
                if (stored == null)
                {
                    stored = tracker != null ? TargetRow(d.Target, PathEpisodeTargetRole.Degraded, tracker)
                        : new PathEpisodeTarget { MonitoringTargetId = d.Target.Id, Role = PathEpisodeTargetRole.Degraded };
                    row.Targets.Add(stored);
                }
                if (tracker != null)
                {
                    stored.PeakLossPct = Math.Max(stored.PeakLossPct ?? 0, tracker.PeakLossPct);
                    if (tracker.PeakRttMs is double p) stored.PeakRttMs = Math.Max(stored.PeakRttMs ?? 0, p);
                }
                if (v == null) continue;
                stored.Scope = v.Scope.ToString();
                stored.Disposition = v.Disposition.ToString();
                stored.BottleneckHopIp = v.OnsetIp;
                stored.BottleneckHopNumber = v.OnsetTtl;
                stored.BottleneckLabel = Truncate(v.Label, 1000);
            }
        }

        await using var db = _siteDb.CreateForSite(_slug, _isDefault);
        await new PathEpisodeRepository(db).UpdateAsync(row, ct);
    }

    private static TargetTraces ToTraces(RunTarget t) =>
        new(t.Target.TargetId, t.Target.Name, t.Target.Address, t.Rounds.ToList(), t.Recovery);

    private async Task<AsnLookup?> ResolveAsnAsync(string address, CancellationToken ct)
    {
        try
        {
            return await _asn.ResolveAsync(address, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "ASN lookup for {Address} failed", address);
            return null;
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Deletes episodes past their retention.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        await using var db = _siteDb.CreateForSite(_slug, _isDefault);
        var removed = await new PathEpisodeRepository(db).PruneAsync(DateTime.UtcNow - TimeSpan.FromDays(_options.PathEpisodeRetentionDays), ct);
        if (removed > 0)
            _logger.LogDebug("Path Localization pruned {Count} episode(s) on site {Slug}", removed, _slug);
    }
}
