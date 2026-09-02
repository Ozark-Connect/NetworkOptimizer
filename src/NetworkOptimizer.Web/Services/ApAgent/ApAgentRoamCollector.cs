using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Persists roam records from one site's AP Agent event rings.
///
/// The UniFi Console can say a roam happened and almost nothing else. The agents give the losing
/// and gaining side, the BSSID and band a client actually landed on, and UBNT_ROAM gossip that
/// covers access points with no agent of their own, so this is net-new capability rather than a
/// faster version of something we already had.
///
/// Replay is by sequence, not by time: the cursor survives a restart of this server, and a ring
/// that overwrote the window is recorded as a gap rather than interpolated over.
/// </summary>
public sealed class ApAgentRoamCollector
{
    /// <summary>An access point is a small target; a slow one must not hold up the pass.</summary>
    private static readonly TimeSpan EventsTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan VapsTimeout = TimeSpan.FromSeconds(5);

    /// <summary>VAP tables change on a provision, not between passes.</summary>
    private static readonly TimeSpan VapRefresh = TimeSpan.FromMinutes(5);

    /// <summary>How long roam rows are kept. Long enough to see a client's habits over a season.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(6);

    private readonly IServiceProvider _serviceProvider;
    private readonly ApAgentTargetDirectory _directory;
    private readonly ApAgentEventsClient _events;
    private readonly ILogger<ApAgentRoamCollector> _logger;
    private readonly string _siteSlug;
    private readonly ApAgentChannelMoveCollector? _channelMoves;

    private readonly ApAgentRoamAssembler _assembler = new();
    private readonly Dictionary<string, DateTime> _vapsLoadedAt = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPrunedAt = DateTime.MinValue;

    /// <summary>Creates the collector for one site.</summary>
    /// <param name="channelMoves">Where channel_change events on the same ring are routed; null drops them.</param>
    public ApAgentRoamCollector(
        IServiceProvider serviceProvider,
        ApAgentTargetDirectory directory,
        ApAgentEventsClient events,
        ILogger<ApAgentRoamCollector> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug,
        ApAgentChannelMoveCollector? channelMoves = null)
    {
        _serviceProvider = serviceProvider;
        _directory = directory;
        _events = events;
        _logger = logger;
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _channelMoves = channelMoves;
    }

    /// <summary>
    /// Teaches the assembler that a link MAC belongs to a client. The telemetry collector already
    /// holds every access point's client table, and an MLO client associates under a different MAC
    /// per link, so without this a Wi-Fi 7 client roams as several separate clients.
    /// </summary>
    public void NoteClients(ApAgentClientsPayload payload)
    {
        foreach (var client in payload.Clients)
        {
            var key = client.Key.Length > 0 ? client.Key : client.Mac;
            foreach (var link in client.Links)
                _assembler.NoteClientKey(link.Mac, key);
        }
    }

    /// <summary>One collection pass over every access point on the site that has an agent.</summary>
    public async Task CollectAsync(CancellationToken ct = default)
    {
        if (!await _directory.IsSiteEnabledAsync(_siteSlug, ct)) return;

        var targets = await _directory.GetTargetsAsync(_siteSlug, ct);
        if (targets.Count == 0) return;

        using var scope = CreateSiteScope();
        var db = scope.ServiceProvider.GetRequiredService<NetworkOptimizerDbContext>();
        var cursors = await db.ApAgentEventCursors.ToDictionaryAsync(c => c.DeviceMac, StringComparer.OrdinalIgnoreCase, ct);

        var batch = new List<ApRoamObservedEvent>();
        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) return;
            await RefreshVapsAsync(target, ct);
            await ReadEventsAsync(db, cursors, target, batch, ct);
        }

        var touched = _assembler.Process(batch);
        await PersistAsync(db, touched, ct);
        await PruneAsync(db, ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task RefreshVapsAsync(ApAgentTarget target, CancellationToken ct)
    {
        if (_vapsLoadedAt.TryGetValue(target.Mac, out var at) && DateTime.UtcNow - at < VapRefresh) return;

        var payload = await _events.GetVapsAsync(_siteSlug, target.Host, target.Token, VapsTimeout, ct);
        if (payload == null) return;

        _assembler.SetVaps(target.Mac, payload.Vaps);
        _vapsLoadedAt[target.Mac] = DateTime.UtcNow;
    }

    private async Task ReadEventsAsync(
        NetworkOptimizerDbContext db,
        Dictionary<string, ApAgentEventCursor> cursors,
        ApAgentTarget target,
        List<ApRoamObservedEvent> batch,
        CancellationToken ct)
    {
        var cursor = cursors.GetValueOrDefault(target.Mac);
        var since = ApAgentEventCursorReader.SinceFor(cursor);

        var payload = await _events.GetEventsAsync(_siteSlug, target.Host, target.Token, since, EventsTimeout, ct);
        if (payload == null) return;

        var window = ApAgentEventCursorReader.Read(since, cursor?.AgentStartedAt, payload);
        if (window.RefetchFromStart)
        {
            _logger.LogInformation(
                "AP Agent {Ap} restarted its event ring, replaying it whole (site {Site})", target.Mac, _siteSlug);

            payload = await _events.GetEventsAsync(_siteSlug, target.Host, target.Token, 0, EventsTimeout, ct);
            if (payload == null) return;
            window = ApAgentEventCursorReader.Read(0, null, payload) with { Gap = true };
        }

        if (window.Gap)
        {
            _logger.LogWarning(
                "AP Agent {Ap} event window was overwritten before it was read, so roam history has a gap (site {Site})",
                target.Mac, _siteSlug);
        }

        if (cursor == null)
        {
            cursor = new ApAgentEventCursor { DeviceMac = target.Mac };
            db.ApAgentEventCursors.Add(cursor);
            cursors[target.Mac] = cursor;
        }

        cursor.LastSeq = window.NextSeq;
        cursor.AgentStartedAt = payload.AgentStartedAt == default ? null : payload.AgentStartedAt.ToUniversalTime();
        cursor.LastPolledAt = DateTime.UtcNow;
        cursor.DroppedEvents = window.DroppedEvents;
        cursor.UpdatedAt = DateTime.UtcNow;
        if (window.Gap)
        {
            cursor.TruncationCount++;
            cursor.LastTruncatedAt = DateTime.UtcNow;
        }

        foreach (var e in window.Events)
        {
            // A radio move rides the same ring as the client events but is about no client.
            if (e.Type == ApAgentEventTypes.ChannelChange)
            {
                if (_channelMoves != null)
                    await _channelMoves.RecordAsync(target.Mac, target.Name, e, ct);
                continue;
            }
            batch.Add(new ApRoamObservedEvent(target.Mac, e, window.Gap));
        }
    }

    private static async Task PersistAsync(
        NetworkOptimizerDbContext db, IReadOnlyList<ApRoamCandidate> touched, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var inserted = new List<(ApRoamCandidate Candidate, ApRoamRecord Row)>();

        foreach (var candidate in touched)
        {
            if (candidate.RecordId == null)
            {
                var row = new ApRoamRecord { ObservedAt = now, CreatedAt = now };
                Apply(candidate, row);
                db.ApRoamRecords.Add(row);
                inserted.Add((candidate, row));
                continue;
            }

            if (!candidate.Dirty) continue;
            var existing = await db.ApRoamRecords.FindAsync([candidate.RecordId.Value], ct);
            if (existing == null) continue;
            Apply(candidate, existing);
            candidate.Dirty = false;
        }

        if (inserted.Count == 0) return;

        await db.SaveChangesAsync(ct);
        foreach (var (candidate, row) in inserted)
        {
            candidate.RecordId = row.Id;
            candidate.Dirty = false;
        }
    }

    private static void Apply(ApRoamCandidate candidate, ApRoamRecord row)
    {
        row.RoamedAt = candidate.RoamedAt;
        row.ClientMac = candidate.ClientMac;
        row.LinkMac = candidate.LinkMac;
        row.FromApMac = candidate.FromApMac;
        row.FromBssid = candidate.FromBssid;
        row.FromBand = candidate.FromBand;
        row.FromChannel = candidate.FromChannel;
        row.ToApMac = candidate.ToApMac;
        row.ToBssid = candidate.ToBssid;
        row.Band = candidate.Band;
        row.Channel = candidate.Channel;
        row.DwellSeconds = candidate.DwellSeconds;
        row.AfterEventGap = candidate.AfterEventGap;
        row.Source = candidate.Source;
        row.ObservedByApMacs = string.Join(",", candidate.Observers);
        row.ObservationCount = candidate.Observers.Count;
    }

    private async Task PruneAsync(NetworkOptimizerDbContext db, CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastPrunedAt < PruneInterval) return;
        _lastPrunedAt = DateTime.UtcNow;

        var cutoff = DateTime.UtcNow - Retention;
        await db.ApRoamRecords.Where(r => r.RoamedAt < cutoff).ExecuteDeleteAsync(ct);
    }

    private IServiceScope CreateSiteScope()
    {
        var scope = _serviceProvider.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteSlug);
        return scope;
    }
}
