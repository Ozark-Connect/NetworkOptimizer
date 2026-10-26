using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>Runs after changes are recorded: the impact diff, then alerts (steps 5 and 6).</summary>
public interface IChangeEvaluator
{
    /// <summary>Evaluates newly recorded changes against the states before and after them.</summary>
    Task EvaluateAsync(string siteSlug, IReadOnlyList<ConfigChange> changes, ConfigState before, ConfigState after, CancellationToken cancellationToken);

    /// <summary>One summary after a Network app upgrade.</summary>
    Task NetworkAppUpgradeSummaryAsync(string slug, string? from, string? to, IReadOnlyList<ConfigChange> changes, CancellationToken ct);

    /// <summary>The post-change device check for management-path changes.</summary>
    Task CheckManagementPathAsync(ChangeControlSiteEngine engine, CancellationToken ct);

    /// <summary>The daily digest.</summary>
    Task DigestAsync(ChangeControlSiteEngine engine, int incidentsAfter, CancellationToken ct);

    /// <summary>Security Audit on two configs for a site (a plan's impact: live against planned).</summary>
    Task<ImpactResult?> RunImpactAsync(string siteSlug, ConfigState before, ConfigState after, CancellationToken ct);

    /// <summary>The Security Audit's network purposes for a config (detected, or the user's overrides).</summary>
    Task<List<NetworkOptimizer.Audit.Models.NetworkInfo>?> GetNetworksAsync(string siteSlug, ConfigState state, CancellationToken ct);
}

/// <summary>
/// One site's change detection (spec "Change detection"): polls the AUDIT log and device
/// provisioning every minute or two, takes a full snapshot only when either shows a change, plus
/// one daily backstop, and records each change attributed to its actor. Also keeps the write
/// journal, detection gaps for agent sites, firmware rows, and retention.
/// </summary>
public sealed class ChangeControlSiteEngine : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnexplainedCfgQuiet = TimeSpan.FromMinutes(5);

    private readonly string _slug;
    private readonly IUniFiConfigApi _api;
    private readonly IChangeControlRepositoryAccessor _repos;
    private readonly ConfigSnapshotStore _store;
    private readonly ConfigSnapshotReader _reader;
    private readonly IAuditLogger? _audit;
    private readonly Func<CancellationToken, Task<bool>>? _isRolloutActive;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly SemaphoreSlim _pollLock = new(1, 1);
    private readonly SemaphoreSlim _evaluateLock = new(1, 1);
    private readonly ConcurrentQueue<UniFiWriteRecord> _journalQueue = new();
    private readonly Func<bool>? _isAgentSite;

    private ConfigState? _latestState;
    private int? _latestSnapshotId;
    private DateTime _nextPoll = DateTime.MinValue;
    private volatile bool _snapshotRequested;

    /// <summary>Evaluates recorded changes (impact diff and alerts). Set by the registry once built.</summary>
    public IChangeEvaluator? Evaluator { get; set; }

    /// <summary>Runs every loop tick after detection (the plan coordinator: due plans, watches, reports).</summary>
    public Func<CancellationToken, Task>? OnTick { get; set; }

    /// <summary>Raised after a snapshot or change is recorded, so open pages can refresh.</summary>
    public event Action? Changed;

    /// <summary>Runs once per start, after the first device read, with device names by MAC (the firmware history import).</summary>
    public Func<IReadOnlyDictionary<string, string>, CancellationToken, Task>? ImportHistory { get; set; }

    private bool _historyImported;

    public ChangeControlSiteEngine(
        string slug, IUniFiConfigApi api, IChangeControlRepositoryAccessor repos, IConfigBlobStore blobs,
        ILogger logger, IAuditLogger? audit = null, Func<CancellationToken, Task<bool>>? isRolloutActive = null,
        Func<bool>? isAgentSite = null)
    {
        _slug = slug;
        _api = api;
        _repos = repos;
        _store = new ConfigSnapshotStore(blobs, slug);
        _reader = new ConfigSnapshotReader(logger);
        _logger = logger;
        _audit = audit;
        _isRolloutActive = isRolloutActive;
        _isAgentSite = isAgentSite;
    }

    /// <summary>Site this engine watches.</summary>
    public string SiteSlug => _slug;

    /// <summary>The console API this engine reads through.</summary>
    public IUniFiConfigApi Api => _api;

    /// <summary>The snapshot store (blob read/write) for this site.</summary>
    public ConfigSnapshotStore Store => _store;

    /// <summary>The repository accessor for this site.</summary>
    public IChangeControlRepositoryAccessor Repositories => _repos;

    /// <summary>Records one of NO's writes (called from the console client's write observer).</summary>
    public void Journal(UniFiWriteRecord record) => _journalQueue.Enqueue(record);

    /// <summary>Asks the loop to check now (after a plan applies, or a page wants fresh data).</summary>
    public void PollSoon() => _nextPoll = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await FlushJournalAsync(stoppingToken);
                if (DateTime.UtcNow >= _nextPoll || _snapshotRequested)
                    await PollLockedAsync(stoppingToken);
                // Runs while the console is unreachable too: a watched plan must notice that.
                if (OnTick != null)
                    await OnTick(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Change detection pass failed for site {Site}", _slug);
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        await FlushJournalAsync(CancellationToken.None);
    }

    private async Task FlushJournalAsync(CancellationToken ct)
    {
        if (_journalQueue.IsEmpty) return;
        var batch = new List<UniFiWriteRecord>();
        while (_journalQueue.TryDequeue(out var r)) batch.Add(r);
        await _repos.UseAsync(async (repo, c) =>
        {
            foreach (var r in batch)
            {
                var (collection, id) = MapJournalPath(r.Scope, r.Path);
                await repo.AddJournalEntryAsync(new ConfigWriteJournalEntry
                {
                    At = r.At, Method = r.Method, Path = Truncate(r.Path, 300), Collection = collection,
                    ObjectId = id ?? r.ResponseId, FieldsJson = JsonSerializer.Serialize(r.Fields), Source = r.Source, Status = r.Status,
                }, c);
            }
        }, ct);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Maps a write path to a collection key and object ID ("rest/networkconf/abc" to networkconf, abc).</summary>
    internal static (string? Collection, string? ObjectId) MapJournalPath(UniFiApiScope scope, string path)
    {
        var clean = path.Split('?')[0].Trim('/');
        foreach (var def in ConfigCollections.All.OrderByDescending(d => d.ReadPath.Length))
        {
            if (def.Scope != scope && !(def.Key == "device" && clean.StartsWith("rest/device", StringComparison.Ordinal))) continue;
            var paths = new[] { def.ReadPath, def.Write?.CreatePath, def.Key == "device" ? "rest/device" : null }
                .Where(p => p != null).Select(p => p!).Distinct();
            foreach (var p in paths)
            {
                if (clean.Equals(p, StringComparison.OrdinalIgnoreCase)) return (def.Key, null);
                if (clean.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                {
                    var rest = clean[(p.Length + 1)..].Split('/');
                    // rest/setting/{key}/{id}: the id is last.
                    var id = rest[^1];
                    return (def.Key, id is "batch" or "batch-delete" or "batch-reorder" ? null : id);
                }
            }
        }
        return (null, null);
    }

    /// <summary>Runs a detection pass now, for callers that need the result (Firmware Rollout's post-upgrade check).</summary>
    public Task PollNowAsync(CancellationToken ct) => PollLockedAsync(ct);

    private async Task PollLockedAsync(CancellationToken ct)
    {
        await _pollLock.WaitAsync(ct);
        try { await PollAsync(ct); }
        finally { _pollLock.Release(); }
    }

    /// <summary>One detection pass.</summary>
    private async Task PollAsync(CancellationToken ct)
    {
        var settings = await _repos.UseAsync((r, c) => r.GetSettingsAsync(c), ct);
        _nextPoll = DateTime.UtcNow.AddSeconds(Math.Clamp(settings.PollIntervalSeconds, 30, 600));
        var now = DateTime.UtcNow;

        if (!_api.IsConnected)
        {
            var reason = _isAgentSite?.Invoke() == true ? "No data: agent offline" : "No data: UniFi Console unreachable";
            await _repos.UseAsync((r, c) => r.OpenGapAsync(now, reason, c), ct);
            Changed?.Invoke();
            return;
        }
        var closed = await _repos.UseAsync((r, c) => r.CloseGapAsync(now, caughtUp: true, c), ct);
        if (closed != null)
        {
            _logger.LogInformation("Change detection for site {Site} is back after a gap; catching up from the AUDIT log", _slug);
            Changed?.Invoke();
        }

        var state = await _repos.UseAsync((r, c) => r.GetDetectionStateAsync(c), ct);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var fromMs = state.LastAuditEventTimestamp > 0 ? state.LastAuditEventTimestamp : nowMs - (long)TimeSpan.FromHours(1).TotalMilliseconds;

        var (events, auditOk) = await AuditLogReader.ReadAsync(_api, fromMs, nowMs, ct);
        var seen = state.LastAuditEventIdsJson is { Length: > 0 } j ? JsonSerializer.Deserialize<HashSet<string>>(j) ?? [] : [];
        events = events.Where(e => !seen.Contains(e.Id)).ToList();

        var devices = await ReadDeviceVersionsAsync(ct);
        var appVersion = await ConfigSnapshotReader.ReadNetworkAppVersionAsync(_api, ct);
        var journal = await _repos.UseAsync((r, c) => r.GetJournalAsync((state.LastPollAt ?? now.AddHours(-1)).AddMinutes(-3), now, c), ct);

        var oldCfg = Deserialize(state.CfgVersionsJson);
        var oldVersions = Deserialize(state.DeviceVersionsJson);
        var cfgChanged = devices != null && devices.Any(d => oldCfg.TryGetValue(d.Mac, out var v) && v != d.CfgVersion);
        var firmwareChanged = devices?.Where(d => oldVersions.TryGetValue(d.Mac, out var v) && !string.IsNullOrEmpty(v) && v != d.Version).ToList() ?? [];
        var appChanged = !string.IsNullOrEmpty(appVersion) && !string.IsNullOrEmpty(state.NetworkAppVersion) && appVersion != state.NetworkAppVersion;

        var changeEvents = events.Where(e => e.IsChange).ToList();
        var foreign = changeEvents.Where(e => !ChangeAttributor.IsOwnEvent(e, journal)).ToList();
        var ownOnUserObjects = changeEvents.Where(e => ChangeAttributor.IsOwnEvent(e, journal)
            && !(e.ObjectName ?? "").StartsWith(ConfigCollections.NoOwnedPrefix, StringComparison.Ordinal)).ToList();
        var recentJournal = journal.Any(x => now - x.At < UnexplainedCfgQuiet);
        var unexplainedCfg = cfgChanged && changeEvents.Count == 0 && !recentJournal;

        if (!_historyImported && devices != null && ImportHistory != null)
        {
            _historyImported = true;
            try
            {
                await ImportHistory(devices.ToDictionary(d => d.Mac, d => d.Name, StringComparer.OrdinalIgnoreCase), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Firmware history import failed for site {Site}", _slug);
            }
        }

        // Firmware rows: one per device that moved version, and one for a Network app version change.
        var rolloutActive = _isRolloutActive != null && await _isRolloutActive(ct);
        foreach (var d in firmwareChanged)
            await RecordFirmwareRowAsync(ConfigChangeKind.DeviceFirmware, $"{d.Name}: firmware {FirmwareVersionFormat.Short(oldVersions[d.Mac])} to {FirmwareVersionFormat.Short(d.Version)}",
                oldVersions[d.Mac], d.Version, rolloutActive, ct);
        if (appChanged)
        {
            await RecordFirmwareRowAsync(ConfigChangeKind.NetworkAppUpgrade, $"Network app {state.NetworkAppVersion} to {appVersion}",
                state.NetworkAppVersion, appVersion, rolloutActive, ct);
            await PinLatestAsync("Before Network app upgrade", ct);
            state.NetworkAppVersionChangedAt = now;
        }

        var hasAny = await _repos.UseAsync(async (r, c) => await r.GetLatestSnapshotAsync(c) != null, ct);
        var dailyDue = DailyDue(settings, state, now);
        ConfigSnapshotTrigger? trigger =
            !hasAny ? ConfigSnapshotTrigger.Initial
            : appChanged ? ConfigSnapshotTrigger.NetworkAppUpgrade
            : foreign.Count > 0 || ownOnUserObjects.Count > 0 || unexplainedCfg || firmwareChanged.Count > 0 || _snapshotRequested
                ? ConfigSnapshotTrigger.Change
            : dailyDue ? ConfigSnapshotTrigger.Daily
            : null;
        _snapshotRequested = false;

        var snapshotOk = true;
        if (trigger != null)
        {
            var inUpgradeWindow = state.NetworkAppVersionChangedAt is { } at && now - at < ChangeAttributor.UpgradeWindow;
            var taken = await SnapshotAndRecordAsync(trigger.Value, null, pinReason: trigger == ConfigSnapshotTrigger.NetworkAppUpgrade ? "Network app upgrade" : null,
                events, journal, inUpgradeWindow, ct);
            snapshotOk = taken.Ok;
            if (trigger is ConfigSnapshotTrigger.Daily or ConfigSnapshotTrigger.Initial && taken.Ok)
                state.LastDailySnapshotAt = now;
            if (appChanged && taken.Ok && Evaluator != null)
                await Evaluator.NetworkAppUpgradeSummaryAsync(_slug, state.NetworkAppVersion, appVersion, taken.Changes, ct);
        }
        if (dailyDue && Evaluator != null && hasAny)
            await Evaluator.DigestAsync(this, await CountIncidentsAfterChangesAsync(now, ct), ct);
        if (Evaluator != null)
            await Evaluator.CheckManagementPathAsync(this, ct);

        // Advance the cursor only past what was processed; a failed snapshot leaves the events for next time.
        if (auditOk && snapshotOk && events.Count > 0)
        {
            var maxTs = events.Max(e => e.Timestamp);
            state.LastAuditEventTimestamp = maxTs;
            state.LastAuditEventIdsJson = JsonSerializer.Serialize(events.Where(e => e.Timestamp == maxTs).Select(e => e.Id)
                .Concat(maxTs == fromMs ? seen : []).ToHashSet());
        }
        else if (auditOk && state.LastAuditEventTimestamp == 0)
        {
            state.LastAuditEventTimestamp = nowMs;
        }

        if (devices != null && snapshotOk)
        {
            state.CfgVersionsJson = JsonSerializer.Serialize(devices.ToDictionary(d => d.Mac, d => d.CfgVersion));
            state.DeviceVersionsJson = JsonSerializer.Serialize(devices.ToDictionary(d => d.Mac, d => d.Version));
        }
        if (!string.IsNullOrEmpty(appVersion)) state.NetworkAppVersion = appVersion;
        state.LastPollAt = now;
        state.LastPollOk = auditOk && devices != null;
        await _repos.UseAsync((r, c) => r.SaveDetectionStateAsync(state, c), ct);

        if (state.LastRetentionAt == null || now - state.LastRetentionAt > TimeSpan.FromHours(6))
            await RunRetentionAsync(settings, state, now, ct);
    }

    /// <summary>Linked incidents in the last day that started within two hours after a config change.</summary>
    private async Task<int> CountIncidentsAfterChangesAsync(DateTime now, CancellationToken ct) =>
        await _repos.UseAsync(async (r, c) =>
        {
            var changes = (await r.GetChangesAsync(now.AddDays(-1).AddHours(-2), now, 1000, c)).Where(x => x.Kind == ConfigChangeKind.Config).ToList();
            var incidents = (await ChangeIncidents.StartedAsync(r, now.AddDays(-1), now, c))
                .Where(s => s.Relevance == IncidentRelevance.Linked).Select(s => s.Incident);
            return incidents.Count(i => changes.Any(x => i.FirstTriggeredAt >= x.OccurredAt && i.FirstTriggeredAt - x.OccurredAt <= IncidentWindow));
        }, ct);

    /// <summary>Changes and incidents are related within this window, either side (spec "Changes and incidents").</summary>
    public static readonly TimeSpan IncidentWindow = TimeSpan.FromHours(2);

    private static bool DailyDue(ChangeControlSettings settings, ChangeDetectionState state, DateTime nowUtc)
    {
        var tz = TimeZoneInfo.Local;
        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var slotLocal = nowLocal.Date.AddHours(settings.DailySnapshotHour).AddMinutes(settings.DailySnapshotMinute);
        if (nowLocal < slotLocal) slotLocal = slotLocal.AddDays(-1);
        var slotUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slotLocal, DateTimeKind.Unspecified), tz);
        return state.LastDailySnapshotAt == null || state.LastDailySnapshotAt < slotUtc;
    }

    private static Dictionary<string, string> Deserialize(string? json) =>
        string.IsNullOrEmpty(json) ? new(StringComparer.OrdinalIgnoreCase)
        : new Dictionary<string, string>(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);

    private sealed record DeviceVersion(string Mac, string Name, string CfgVersion, string Version);

    private async Task<List<DeviceVersion>?> ReadDeviceVersionsAsync(CancellationToken ct)
    {
        var response = await _api.SendAsync(UniFiApiScope.SiteV1, HttpMethod.Get, "stat/device", null, ct);
        if (!response.IsSuccess) return null;
        var list = ConfigSnapshotReader.ExtractList(response.Body);
        return list?.Select(d => new DeviceVersion(
                d["mac"]?.ToString()?.ToLowerInvariant() ?? "",
                d["name"]?.ToString() is { Length: > 0 } n ? n : d["model"]?.ToString() ?? "",
                d["cfgversion"]?.ToString() ?? "",
                d["version"]?.ToString() ?? ""))
            .Where(d => d.Mac.Length > 0)
            .GroupBy(d => d.Mac).Select(g => g.First())
            .ToList();
    }

    private async Task RecordFirmwareRowAsync(ConfigChangeKind kind, string summary, string? from, string? to, bool rolloutActive, CancellationToken ct)
    {
        var change = new ConfigChange
        {
            Kind = kind,
            OccurredAt = DateTime.UtcNow,
            DetectedAt = DateTime.UtcNow,
            ActorKind = rolloutActive ? ConfigChangeActorKind.Firmware : ConfigChangeActorKind.LikelyUniFi,
            ActorName = rolloutActive ? "Firmware Rollout" : null,
            Summary = summary,
            VersionFrom = from,
            VersionTo = to,
            Evaluated = true,
        };
        await _repos.UseAsync((r, c) => r.AddChangeAsync(change, [], c), ct);
    }

    private async Task PinLatestAsync(string reason, CancellationToken ct)
    {
        await _repos.UseAsync(async (r, c) =>
        {
            var latest = await r.GetLatestSnapshotAsync(c);
            if (latest == null || latest.Pinned) return;
            latest.Pinned = true;
            latest.PinReason = reason;
            await r.UpdateSnapshotAsync(latest, c);
        }, ct);
        _audit?.Log(AuditEventBuilder.FromSystem(AuditCategories.Action, AuditActions.ChangeControlSnapshotPinned,
            targetType: "config_snapshot", targetName: reason, siteSlug: _slug));
    }

    /// <summary>
    /// Takes a snapshot now (Take Snapshot, before an apply or a Firmware Rollout). Always stored,
    /// even when nothing changed, and pinned when a reason is given.
    /// </summary>
    public async Task<ConfigSnapshot?> TakeSnapshotAsync(ConfigSnapshotTrigger trigger, string? detail, string? pinReason, CancellationToken ct)
    {
        // A forced snapshot reads no AUDIT log, so a detection pass records what changed since the last
        // poll with its admin first. Both passes need NO's writes flushed: the queue empties on the loop's tick.
        await FlushJournalAsync(ct);
        if (_api.IsConnected) await PollLockedAsync(ct);
        await FlushJournalAsync(ct);
        var journal = await _repos.UseAsync((r, c) => r.GetJournalAsync(DateTime.UtcNow.AddMinutes(-15), DateTime.UtcNow, c), ct);
        var result = await SnapshotAndRecordAsync(trigger, detail, pinReason, [], journal, inUpgradeWindow: false, ct, force: true);
        return result.Snapshot;
    }

    /// <summary>Reads the live config without storing it (plan building, Compare against Now).</summary>
    public Task<ConfigState> ReadLiveAsync(CancellationToken ct) => _reader.ReadAsync(_api, ct);

    /// <summary>Loads a stored snapshot's state, using the in-memory copy for the newest.</summary>
    public async Task<ConfigState?> LoadSnapshotStateAsync(int snapshotId, CancellationToken ct)
    {
        if (_latestSnapshotId == snapshotId && _latestState != null) return _latestState.Clone();
        return await _repos.UseAsync(async (r, c) =>
        {
            var snapshot = await r.GetSnapshotAsync(snapshotId, c);
            return snapshot == null ? null : await _store.LoadAsync(r, snapshot, c);
        }, ct);
    }

    private async Task<(bool Ok, ConfigSnapshot? Snapshot, List<ConfigChange> Changes)> SnapshotAndRecordAsync(
        ConfigSnapshotTrigger trigger, string? detail, string? pinReason, IReadOnlyList<AuditLogEvent> events,
        IReadOnlyList<ConfigWriteJournalEntry> journal, bool inUpgradeWindow, CancellationToken ct, bool force = false)
    {
        await _snapshotLock.WaitAsync(ct);
        try
        {
            var live = await _reader.ReadAsync(_api, ct);
            if (live.Collections.Count == 0 || live.ReadErrors.ContainsKey("networkconf"))
            {
                _logger.LogWarning("Change Control could not read the config for site {Site}: {Errors}", _slug,
                    string.Join("; ", live.ReadErrors.Select(e => $"{e.Key}: {e.Value}")));
                return (false, null, []);
            }

            var (previous, previousId) = await LatestStateAsync(ct);
            var diff = previous == null ? null : ConfigDiffEngine.Compare(previous, live);
            var recordable = diff?.Objects.Where(o => !o.Generated).ToList() ?? [];
            if (!force && trigger == ConfigSnapshotTrigger.Change && recordable.Count == 0 && (diff?.PolicyOrder.Count ?? 0) == 0)
                return (true, null, []);

            var snapshot = new ConfigSnapshot
            {
                Trigger = trigger,
                TriggerDetail = detail ?? DescribeTrigger(recordable, events, journal),
                PreviousSnapshotId = previousId,
                ChangedObjectCount = recordable.Count + (diff?.PolicyOrder.Count ?? 0),
                Pinned = pinReason != null,
                PinReason = pinReason,
            };
            snapshot = await _repos.UseAsync((r, c) => _store.SaveAsync(r, live, snapshot, c), ct);
            _latestState = live;
            _latestSnapshotId = snapshot.Id;

            if (pinReason != null)
                _audit?.Log(AuditEventBuilder.FromSystem(AuditCategories.Action, AuditActions.ChangeControlSnapshotPinned,
                    targetType: "config_snapshot", targetId: snapshot.Id.ToString(), targetName: pinReason, siteSlug: _slug));

            var changes = new List<ConfigChange>();
            if (previous != null && diff != null && (recordable.Count > 0 || diff.PolicyOrder.Count > 0))
            {
                changes = await RecordChangesAsync(snapshot, previousId, previous, live, recordable, diff, events, journal, inUpgradeWindow, ct);
                if (Evaluator != null && changes.Count > 0) EvaluateInBackground(Evaluator, changes, previous, live);
            }
            Changed?.Invoke();
            return (true, snapshot, changes);
        }
        finally
        {
            _snapshotLock.Release();
        }
    }

    /// <summary>
    /// Runs the Security Audit impact of recorded changes off the snapshot path: it takes two full audits,
    /// and a plan apply waits on its snapshots. Evaluations run one at a time, in order.
    /// </summary>
    private void EvaluateInBackground(IChangeEvaluator evaluator, List<ConfigChange> changes, ConfigState previous, ConfigState live)
    {
        _ = Task.Run(async () =>
        {
            await _evaluateLock.WaitAsync();
            try
            {
                await evaluator.EvaluateAsync(_slug, changes, previous, live, CancellationToken.None);
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Change evaluation failed for site {Site}", _slug);
            }
            finally
            {
                _evaluateLock.Release();
            }
        });
    }

    private static string? DescribeTrigger(IReadOnlyList<ObjectDiff> objects, IReadOnlyList<AuditLogEvent> events, IReadOnlyList<ConfigWriteJournalEntry> journal)
    {
        var admin = events.Where(e => e.IsChange && !ChangeAttributor.IsOwnEvent(e, journal)).Select(e => e.AdminName).FirstOrDefault(n => n != null);
        if (admin != null) return admin;
        return objects.Count > 0 && objects.All(o => ChangeAttributor.WrittenByNo(o, journal)) ? "Network Optimizer" : null;
    }

    private async Task<(ConfigState? State, int? Id)> LatestStateAsync(CancellationToken ct)
    {
        if (_latestState != null) return (_latestState, _latestSnapshotId);
        return await _repos.UseAsync<(ConfigState?, int?)>(async (r, c) =>
        {
            var latest = await r.GetLatestSnapshotAsync(c);
            if (latest == null) return (null, null);
            var state = await _store.LoadAsync(r, latest, c);
            _latestState = state;
            _latestSnapshotId = latest.Id;
            return (state, latest.Id);
        }, ct);
    }

    private async Task<List<ConfigChange>> RecordChangesAsync(
        ConfigSnapshot snapshot, int? previousId, ConfigState before, ConfigState after, List<ObjectDiff> objects, ConfigDiff diff,
        IReadOnlyList<AuditLogEvent> events, IReadOnlyList<ConfigWriteJournalEntry> journal, bool inUpgradeWindow, CancellationToken ct)
    {
        var beforeNames = new ConfigIdentityContext(before.NamesById());
        var afterNames = new ConfigIdentityContext(after.NamesById());
        var all = objects.ToList();
        foreach (var order in diff.PolicyOrder)
            all.Add(PolicyOrderObject(order, afterNames));

        var groups = ChangeAttributor.Attribute(all, events, journal, DateTime.UtcNow, inUpgradeWindow);
        var recorded = new List<ConfigChange>();
        foreach (var g in groups)
        {
            var context = g.ActorKind == ConfigChangeActorKind.LikelyUniFi && inUpgradeWindow ? "during Network app upgrade" : null;
            var change = new ConfigChange
            {
                Kind = ConfigChangeKind.Config,
                OccurredAt = g.OccurredAt,
                DetectedAt = DateTime.UtcNow,
                SnapshotId = snapshot.Id,
                PreviousSnapshotId = previousId,
                ActorKind = g.ActorKind,
                ActorName = g.ActorName,
                ActorIp = g.ActorIp,
                ActorPlatform = g.ActorPlatform,
                PlanId = g.PlanId,
                Summary = Truncate(ChangeDescriber.GroupSummary(g.Objects, context, beforeNames, afterNames), 500),
                ObjectCount = g.Objects.Count,
                ServiceTagsJson = JsonSerializer.Serialize(g.Objects.SelectMany(o => o.Tags).Distinct().OrderBy(t => t)),
                ManagementPath = g.Objects.Any(o => o.ManagementPath),
                WanPath = g.Objects.Any(o => o.WanPath),
                AuditEventsJson = g.Events.Count == 0 ? null : JsonSerializer.Serialize(g.Events),
            };
            var rows = g.Objects.Select(o => ToRecord(o, beforeNames, afterNames)).ToList();
            change = await _repos.UseAsync((r, c) => r.AddChangeAsync(change, rows, c), ct);
            recorded.Add(change);
        }
        return recorded;
    }

    /// <summary>A policy order change as a pseudo object on the firewall policy collection.</summary>
    internal static ObjectDiff PolicyOrderObject(PolicyOrderDiff order, ConfigIdentityContext names) => new()
    {
        Collection = "firewall_policy",
        ObjectId = $"order:{order.SourceZoneId}>{order.DestinationZoneId}",
        Name = $"order, {order.ZonePairName}",
        Action = ConfigChangeAction.Updated,
        Fields =
        [
            new FieldChange("order",
                new JsonArray(order.Before.Select(id => (JsonNode?)names.NameOf(id)).ToArray()),
                new JsonArray(order.After.Select(id => (JsonNode?)names.NameOf(id)).ToArray())),
        ],
    };

    /// <summary>Stores an object diff: fields with references as names, secrets without values.</summary>
    internal static ConfigChangeObject ToRecord(ObjectDiff o, ConfigIdentityContext beforeNames, ConfigIdentityContext afterNames)
    {
        var fields = o.Fields.Select(f => new StoredField
        {
            Path = f.Path,
            Label = ChangeDescriber.LabelFor(f.Path, o.After, o.Before),
            Old = f.Secret ? null : ChangeDescriber.Value(f.Old, beforeNames),
            New = f.Secret ? null : ChangeDescriber.Value(f.New, afterNames),
            Secret = f.Secret,
        }).ToList();
        if (o.Collection == "wlanconf")
        {
            var rows = ChangeDescriber.Rows(o, beforeNames, afterNames).Where(r => r.Label == "Security protocol").ToList();
            if (rows.Count > 0) fields.RemoveAll(f => f.Label == "Security");
            foreach (var r in rows)
                fields.Insert(0, new StoredField { Path = "_security", Label = r.Label, Old = r.Old, New = r.New });
        }
        return new ConfigChangeObject
        {
            Collection = o.Collection,
            ObjectId = Truncate(o.ObjectId, 128),
            ObjectName = Truncate(o.Name, 300),
            Action = o.Action,
            FieldsJson = JsonSerializer.Serialize(fields),
            ServiceTagsJson = o.Tags.Count == 0 ? null : JsonSerializer.Serialize(o.Tags.OrderBy(t => t)),
            OwnedBy = o.OwnedBy,
        };
    }

    private async Task RunRetentionAsync(ChangeControlSettings settings, ChangeDetectionState state, DateTime now, CancellationToken ct)
    {
        try
        {
            await _repos.UseAsync(async (r, c) =>
            {
                var snapshots = await r.GetSnapshotsAsync(limit: int.MaxValue, cancellationToken: c);
                var plans = await r.GetPlansAsync(ConfigPlanStatuses.InProgress.Append(ConfigPlanStatus.Draft).ToArray(), 500, c);
                var protectedIds = plans.SelectMany(p => new[] { p.SourceSnapshotId, p.PreApplySnapshotId }).OfType<int>().ToHashSet();
                var delete = SnapshotRetention.ToDelete(snapshots, settings, now, protectedIds);
                if (delete.Count > 0)
                {
                    await r.DeleteSnapshotsAsync(delete, c);
                    var removed = await _store.CollectGarbageAsync(r, c);
                    _logger.LogInformation("Change Control retention for site {Site}: {Snapshots} snapshots thinned, {Blobs} blobs removed",
                        _slug, delete.Count, removed);
                }
                await r.PruneJournalAsync(now.AddDays(-30), c);
                state.LastRetentionAt = now;
                await r.SaveDetectionStateAsync(state, c);
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Change Control retention failed for site {Site}", _slug);
        }
    }

    /// <summary>Forces a full re-read from storage on the next diff (after retention or a restore).</summary>
    public void InvalidateCache()
    {
        _latestState = null;
        _latestSnapshotId = null;
    }
}

/// <summary>A field change as stored on a change record. References are already names; secrets carry no values.</summary>
public sealed class StoredField
{
    public string Path { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Old { get; set; }
    public string? New { get; set; }
    public bool Secret { get; set; }
}
