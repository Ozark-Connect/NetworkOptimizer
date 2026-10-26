using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>The site service behind every Change Control page for the site in context.</summary>
public sealed partial class ChangeControlService : IChangeControlService
{
    private readonly ChangeControlRegistry _registry;
    private readonly ISiteAccessFilter _access;
    private readonly Gates.IAuditContext _audit;
    private readonly SiteContextService _site;
    private readonly Identity.ICallerContext _caller;
    private readonly IEffectiveSiteRoleResolver _roles;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly MonitoringInfluxClient _influx;
    private readonly IspHealthService _isp;
    private readonly ConfigTemplateStore _templates;
    private readonly NetworkOptimizer.Alerts.Interfaces.IAlertRepository _alerts;
    private readonly ILogger<ChangeControlService> _logger;

    public ChangeControlService(ChangeControlRegistry registry, SiteContextService site, Identity.ICallerContext caller,
        IEffectiveSiteRoleResolver roles, IDbContextFactory<NetworkOptimizerDbContext> mainDb, MonitoringInfluxClient influx,
        IspHealthService isp, ConfigTemplateStore templates, NetworkOptimizer.Alerts.Interfaces.IAlertRepository alerts,
        ISiteAccessFilter access, Gates.IAuditContext audit, ILogger<ChangeControlService> logger)
    {
        _access = access;
        _audit = audit;
        _alerts = alerts;
        _registry = registry;
        _site = site;
        _caller = caller;
        _roles = roles;
        _mainDb = mainDb;
        _influx = influx;
        _isp = isp;
        _templates = templates;
        _logger = logger;
    }

    private ChangeControlSiteEngine Engine => _registry.GetFor(_site.Slug);
    private PlanCoordinator Plans => _registry.PlansFor(_site.Slug);
    private IChangeControlRepositoryAccessor Repo => Engine.Repositories;
    private string Actor => _caller.Current?.ActorName is { Length: > 0 } n ? n : "unknown";

    private async Task<bool> CallerIsSiteAdminAsync()
    {
        var caller = _caller.Current;
        if (caller == null || caller.IsSystem || caller.AuthenticationDisabled) return true;
        if (caller.Principal == null) return false;
        if (caller.Principal.IsInRole(Roles.Admin)) return true;
        return await _roles.GetEffectiveRoleAsync(caller.Principal, _site.Slug) == SiteRole.SiteAdmin;
    }

    public Task<bool> IsSiteAdminAsync(CancellationToken cancellationToken = default) => CallerIsSiteAdminAsync();

    private async Task<string> SiteNameAsync(CancellationToken ct)
    {
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        return await db.Sites.AsNoTracking().Where(s => s.Slug == _site.Slug).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? _site.Slug;
    }

    // ---------- Changes ----------

    internal static string Who(ConfigChange c) => c.ActorKind switch
    {
        ConfigChangeActorKind.Person => c.ActorName ?? "Admin",
        ConfigChangeActorKind.NetworkOptimizer => c.ActorName is { Length: > 0 } n && n != "Network Optimizer" ? $"Network Optimizer ({n})" : "Network Optimizer",
        ConfigChangeActorKind.LikelyUniFi => c.Kind == ConfigChangeKind.Config ? "No admin recorded, likely UniFi" : "UniFi",
        ConfigChangeActorKind.Firmware => c.ActorName ?? "Firmware Rollout",
        _ => "No admin recorded",
    };

    /// <summary>The newest applied Roll Back of each change, by change ID.</summary>
    internal static Dictionary<int, (int PlanId, DateTime? At)> RollbacksByChange(List<(int ChangeId, int PlanId, DateTime? CompletedAt)> rows) =>
        rows.GroupBy(r => r.ChangeId).ToDictionary(g => g.Key, g => (g.First().PlanId, g.First().CompletedAt));

    internal static ChangeRowView Row(ConfigChange c, string slug, string siteName, int incidentsAfter,
        IReadOnlyDictionary<int, (int PlanId, DateTime? At)>? rollbacks = null) => new()
        {
            RolledBackByPlanId = rollbacks != null && rollbacks.TryGetValue(c.Id, out var rb) ? rb.PlanId : null,
            RolledBackAt = rollbacks != null && rollbacks.TryGetValue(c.Id, out var rba) ? rba.At : null,
            Id = c.Id,
            SiteSlug = slug,
            SiteName = siteName,
            OccurredAt = c.OccurredAt,
            Kind = c.Kind,
            Summary = c.Summary,
            ActorKind = c.ActorKind,
            Who = Who(c),
            RegressionCount = c.RegressionCount,
            FixCount = c.FixCount,
            Tags = string.IsNullOrEmpty(c.ServiceTagsJson) ? [] : JsonSerializer.Deserialize<List<string>>(c.ServiceTagsJson) ?? [],
            IncidentsAfter = incidentsAfter,
            ManagementPath = c.ManagementPath,
            SnapshotId = c.SnapshotId,
            PreviousSnapshotId = c.PreviousSnapshotId,
            PlanId = c.PlanId,
            HealthDrop = c.HealthBefore is >= 0 and var before && c.HealthAfter is >= 0 and var after ? Math.Round(before - after, 1) : null,
        };

    public async Task<List<ChangeRowView>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, bool? includeOwn = null, CancellationToken cancellationToken = default)
    {
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var own = includeOwn ?? settings.ShowOwnChanges;
        var name = await SiteNameAsync(cancellationToken);
        return await Repo.UseAsync(async (r, c) =>
        {
            var changes = (await r.GetChangesAsync(fromUtc, toUtc, 2000, c))
                .Where(x => own || x.ActorKind != ConfigChangeActorKind.NetworkOptimizer || x.PlanId != null).ToList();
            var incidents = await ChangeIncidents.StartedAsync(r, fromUtc, toUtc + ChangeControlSiteEngine.IncidentWindow, c);
            var rollbacks = RollbacksByChange(await r.GetChangeRollbacksAsync(c));
            return changes.Select(x => Row(x, _site.Slug, name, LinkedAfter(x, incidents), rollbacks)).ToList();
        }, cancellationToken);
    }

    /// <summary>Linked incidents that started within the window after a config change. WAN/ISP ones are listed, never counted (spec).</summary>
    internal static int LinkedAfter(ConfigChange x, List<ScopedIncident> incidents) => x.Kind != ConfigChangeKind.Config ? 0
        : incidents.Count(i => i.Relevance == IncidentRelevance.Linked && i.Incident.FirstTriggeredAt >= x.OccurredAt
            && i.Incident.FirstTriggeredAt - x.OccurredAt <= ChangeControlSiteEngine.IncidentWindow);

    internal static IncidentView Incident(ScopedIncident s) => new()
    {
        // The condition's end, not when someone resolved the alert; null while it is still going on.
        Id = s.Incident.Id,
        Title = s.Incident.Title,
        StartedAt = s.Incident.FirstTriggeredAt,
        ResolvedAt = s.End < DateTime.UtcNow.AddMinutes(-1) ? s.End : null,
        Severity = s.Incident.Severity.ToString(),
        IsWan = s.IsWan,
    };

    public async Task<ChangeDetailView?> GetChangeDetailAsync(int changeId, CancellationToken cancellationToken = default)
    {
        var name = await SiteNameAsync(cancellationToken);
        var loaded = await Repo.UseAsync<(ConfigChange change, List<ConfigChangeObject> objects, List<ScopedIncident> incidents, List<SiteHealthHour> health,
            Dictionary<int, (int PlanId, DateTime? At)> rollbacks)?>(async (r, c) =>
        {
            var change = await r.GetChangeAsync(changeId, c);
            if (change == null) return null;
            var objects = await r.GetChangeObjectsAsync([change.Id], c);
            var incidents = await ChangeIncidents.StartedAsync(r, change.OccurredAt, change.OccurredAt + ChangeControlSiteEngine.IncidentWindow, c);
            var health = await r.GetHealthHoursAsync(change.OccurredAt.AddHours(-4), change.OccurredAt.AddHours(5), c);
            return (change, objects, incidents, health, RollbacksByChange(await r.GetChangeRollbacksAsync(c)));
        }, cancellationToken);
        if (loaded is not { } l) return null;

        var metrics = HealthDeltas(l.health, l.change.OccurredAt)
            .Concat(await new SiteMetricsReader(_influx, _isp, _logger).BeforeAfterAsync(l.change.OccurredAt, cancellationToken)).ToList();
        var impact = string.IsNullOrEmpty(l.change.ImpactJson) ? [] : JsonSerializer.Deserialize<List<ImpactLine>>(l.change.ImpactJson) ?? [];
        var source = l.change.ActorKind == ConfigChangeActorKind.Person
            ? string.Join(", ", new[] { l.change.ActorName, l.change.ActorIp, l.change.ActorPlatform }.Where(s => !string.IsNullOrEmpty(s)))
            : null;
        return new ChangeDetailView
        {
            Row = Row(l.change, _site.Slug, name, LinkedAfter(l.change, l.incidents), l.rollbacks),
            Objects = l.objects.Select(ObjectView).ToList(),
            Impact = impact,
            IncidentsAfter = l.incidents.Select(Incident).ToList(),
            BeforeAfter = metrics,
            Source = source,
            CanRollBack = l.change.Kind == ConfigChangeKind.Config && l.change.PreviousSnapshotId != null && !l.rollbacks.ContainsKey(l.change.Id),
            Facts = Facts(l.change),
        };
    }

    /// <summary>
    /// Site Health and each area's points lost, 3 hours before vs. the 3 full hours after, ISP impairment
    /// removed: a change cannot cause upstream loss. Areas that moved less than a point are left out.
    /// </summary>
    internal static List<MetricDelta> HealthDeltas(IReadOnlyList<SiteHealthHour> hours, DateTime at)
    {
        var floor = SiteHealth.SiteHealthJob.FloorHour(at);
        var before = hours.Where(h => h.HourStart >= floor.AddHours(-3) && h.HourStart < floor).ToList();
        var after = hours.Where(h => h.HourStart >= floor.AddHours(1) && h.HourStart < floor.AddHours(4)).ToList();
        if (before.Count == 0 || after.Count == 0) return [];
        static double? Mean(IEnumerable<double?> values)
        {
            var known = values.OfType<double>().ToList();
            return known.Count > 0 ? known.Average() : null;
        }
        var rows = new List<MetricDelta>();
        if (Mean(before.Select(SiteHealth.SiteHealthScorer.LocalHealth)) is { } hb && Mean(after.Select(SiteHealth.SiteHealthScorer.LocalHealth)) is { } ha)
            rows.Add(new MetricDelta("Site Health (3 h)", hb.ToString("0.#", CultureInfo.InvariantCulture), ha.ToString("0.#", CultureInfo.InvariantCulture), ha < hb - 3));
        foreach (var (label, value) in new (string, Func<SiteHealthHour, double>)[]
                 {
                     ("Internet, local", SiteHealth.SiteHealthSeriesBuilder.Local), ("Wi-Fi", SiteHealth.SiteHealthSeriesBuilder.WifiWeighted),
                     ("Availability", h => h.Availability ?? 0), ("LAN", h => h.Lan ?? 0),
                 })
        {
            var b = before.Average(value) * 100;
            var a = after.Average(value) * 100;
            if (Math.Abs(a - b) < 1) continue;
            rows.Add(new MetricDelta($"{label}, points lost", b.ToString("0.#", CultureInfo.InvariantCulture), a.ToString("0.#", CultureInfo.InvariantCulture), a > b));
        }
        return rows;
    }

    // Recorded this long after it happened means it came from history, not from a live device read.
    private static readonly TimeSpan ImportedAfter = TimeSpan.FromHours(1);

    internal static List<(string Label, string Value)> Facts(ConfigChange c)
    {
        if (c.Kind is not (ConfigChangeKind.DeviceFirmware or ConfigChangeKind.NetworkAppUpgrade)) return [];
        var facts = new List<(string, string)>();
        if (c.Kind == ConfigChangeKind.DeviceFirmware && c.Summary.IndexOf(": firmware", StringComparison.Ordinal) is > 0 and var end)
            facts.Add(("Device", c.Summary[..end]));
        facts.Add(("From", FirmwareVersionFormat.ShortOrNull(c.VersionFrom) ?? "Not recorded"));
        facts.Add(("To", FirmwareVersionFormat.ShortOrNull(c.VersionTo) ?? "Not recorded"));
        facts.Add(("Upgraded by", c.ActorKind switch
        {
            ConfigChangeActorKind.Firmware => c.ActorName ?? "Firmware Rollout",
            ConfigChangeActorKind.LikelyUniFi => "Outside Network Optimizer (UniFi Console or auto-update)",
            _ => Who(c),
        }));
        if (c.DetectedAt - c.OccurredAt > ImportedAfter)
            facts.Add(("Recorded", c.FirmwareRolloutPlanId != null ? "From Firmware Rollout history" : "From reboot history"));
        return facts;
    }

    internal static ChangeObjectView ObjectView(ConfigChangeObject o)
    {
        var fields = JsonSerializer.Deserialize<List<StoredField>>(o.FieldsJson) ?? [];
        // Labels are saved at detection; relabel from the path so rows recorded earlier get today's names.
        // A label carrying a port's given name, or a synthetic row (_security), is kept as saved.
        foreach (var f in fields.Where(f => !f.Path.StartsWith('_') && !f.Label.Contains('(')))
            f.Label = ChangeDescriber.LabelFor(f.Path);
        return new ChangeObjectView
        {
            Collection = o.Collection,
            CollectionName = ConfigCollections.Get(o.Collection)?.DisplayName ?? o.Collection,
            ObjectId = o.ObjectId,
            Name = o.ObjectName,
            Action = o.Action,
            Fields = fields,
            OwnedBy = o.OwnedBy,
            ChangeText = o.Action switch
            {
                ConfigChangeAction.Created => "Created",
                ConfigChangeAction.Deleted => "Deleted",
                _ => string.Join(", ", fields.Select(f => f.Label).Distinct().Take(4)),
            },
        };
    }

    public async Task<List<ChangeRowView>> GetChangesBeforeAsync(int incidentId, CancellationToken cancellationToken = default)
    {
        var name = await SiteNameAsync(cancellationToken);
        var changes = await Repo.UseAsync(async (r, c) =>
        {
            if (await r.GetIncidentAsync(incidentId, ChangeIncidents.EventTypes, c) is not { } found
                || ChangeIncidents.Classify(found.Alerts) != IncidentRelevance.Linked)
                return new List<ConfigChange>();
            var start = found.Incident.FirstTriggeredAt;
            return await r.GetChangesAsync(start - ChangeControlSiteEngine.IncidentWindow, start, 100, c);
        }, cancellationToken);
        return changes.Where(c => c.Kind == ConfigChangeKind.Config).Select(c => Row(c, _site.Slug, name, 1)).ToList();
    }

    // ---------- Snapshots ----------

    internal static (string Text, string Dot) TriggerLabel(ConfigSnapshot s) => s.Trigger switch
    {
        ConfigSnapshotTrigger.Daily => ("Daily", ""),
        ConfigSnapshotTrigger.Initial => ("First snapshot", ""),
        ConfigSnapshotTrigger.Manual => ($"Taken by {s.TriggerDetail}", "person"),
        ConfigSnapshotTrigger.BeforeApply => ($"Before {s.TriggerDetail}", "no"),
        ConfigSnapshotTrigger.AfterApply => ($"After {s.TriggerDetail}", "no"),
        ConfigSnapshotTrigger.BeforeFirmwareRollout => ("Before Firmware Rollout", "firmware"),
        ConfigSnapshotTrigger.NetworkAppUpgrade => ("After Network app upgrade", "firmware"),
        _ when s.TriggerDetail == "Network Optimizer" => ("Network Optimizer", "no"),
        _ when string.IsNullOrEmpty(s.TriggerDetail) => ("No admin recorded", "unattributed"),
        _ => (s.TriggerDetail!, "person"),
    };

    public async Task<SnapshotListView> GetSnapshotsAsync(CancellationToken cancellationToken = default)
    {
        return await Repo.UseAsync(async (r, c) =>
        {
            var snaps = await r.GetSnapshotsAsync(limit: 1000, cancellationToken: c);
            var (count, _) = await r.GetSnapshotTotalsAsync(c);
            var settings = await r.GetSettingsAsync(c);
            var stored = _registry.BlobBytes(_site.Slug);
            var rows = snaps.Select(s =>
            {
                var (text, dot) = TriggerLabel(s);
                // A pinned snapshot that sits right before a Network app upgrade reads as such.
                if (s.Pinned && s.PinReason == "Before Network app upgrade") (text, dot) = ("Before Network app upgrade", "firmware");
                return new SnapshotRowView
                {
                    Id = s.Id,
                    TakenAt = s.TakenAt,
                    Trigger = text,
                    TriggerDot = dot,
                    ChangedObjects = s.ChangedObjectCount,
                    IssueCount = s.AuditIssueCount,
                    RegressionCount = s.AuditRegressionCount,
                    Pinned = s.Pinned,
                    IsBaseline = s.IsBaseline,
                    PinReason = s.PinReason,
                };
            }).ToList();
            return new SnapshotListView { Rows = rows, Count = count, StoredBytes = stored, Settings = settings, LiveIssueCount = rows.FirstOrDefault()?.IssueCount };
        }, cancellationToken);
    }

    private async Task<(ConfigState State, string Label, string Detail)> LoadSideAsync(int? snapshotId, CancellationToken ct)
    {
        if (snapshotId == null) return (await Engine.ReadLiveAsync(ct), "Now", "Live config");
        var snap = await Repo.UseAsync((r, c) => r.GetSnapshotAsync(snapshotId.Value, c), ct) ?? throw new InvalidOperationException("Snapshot not found.");
        var state = await Engine.LoadSnapshotStateAsync(snap.Id, ct) ?? throw new InvalidOperationException("Snapshot content not found.");
        var tags = new List<string>();
        if (snap.IsBaseline) tags.Add("Baseline");
        var (text, dot) = TriggerLabel(snap);
        // A change snapshot is labeled with who made the change; a name keeps its own casing.
        tags.Add(snap.Trigger == ConfigSnapshotTrigger.Change
            ? dot == "unattributed" ? "after a change, no admin recorded" : $"after a change by {text}"
            : text.ToLowerInvariant() + " snapshot");
        var detail = string.Join(", ", tags);
        return (state, Local(snap.TakenAt).ToString("MMM d, h:mm tt"), char.ToUpperInvariant(detail[0]) + detail[1..]);
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);

    public async Task<CompareView> CompareAsync(int? fromSnapshotId, int? toSnapshotId, CancellationToken cancellationToken = default)
    {
        try
        {
            var from = await LoadSideAsync(fromSnapshotId, cancellationToken);
            var to = await LoadSideAsync(toSnapshotId, cancellationToken);
            var diff = ConfigDiffEngine.Compare(from.State, to.State);
            var beforeNames = ConfigIdentityContext.ForBefore(from.State, to.State);
            var afterNames = new ConfigIdentityContext(to.State.NamesById());
            var fromTime = from.State.ReadAt;
            var toTime = toSnapshotId == null ? DateTime.UtcNow : to.State.ReadAt;

            var objects = new List<CompareObjectView>();
            foreach (var o in diff.Objects.Where(x => !x.Generated))
            {
                var record = ChangeControlSiteEngine.ToRecord(o, beforeNames, afterNames);
                var latest = await Repo.UseAsync((r, c) => r.GetLatestObjectChangeAsync(o.Collection, o.ObjectId, fromTime, toTime, c), cancellationToken);
                string? whenWho = null, note = null;
                if (latest is { } l)
                {
                    whenWho = $"{Local(l.Change.OccurredAt):MMM d}, {Who(l.Change)}";
                    if (l.Change.ActorKind is ConfigChangeActorKind.LikelyUniFi or ConfigChangeActorKind.NoAdminRecorded && l.Change.Summary.Contains("Network app upgrade", StringComparison.Ordinal))
                        note = "During a Network app upgrade. No AUDIT event matches.";
                }
                objects.Add(new CompareObjectView { Object = ObjectView(record), WhenWho = whenWho, Note = note });
            }
            foreach (var order in diff.PolicyOrder)
                objects.Add(new CompareObjectView { Object = ObjectView(ChangeControlSiteEngine.ToRecord(ChangeControlSiteEngine.PolicyOrderObject(order, afterNames), beforeNames, afterNames)) });

            var impact = new List<ImpactLine>();
            if (ImpactAnalyzer.Triggers(objects.Select(o => o.Object.Collection)) && Engine.Evaluator != null)
            {
                var result = await Engine.Evaluator.RunImpactAsync(_site.Slug, from.State, to.State, cancellationToken);
                if (result != null) impact = [.. result.Regressions, .. result.Fixes];
            }

            return new CompareView
            {
                FromLabel = from.Label,
                FromDetail = from.Detail,
                ToLabel = to.Label,
                ToDetail = to.Detail,
                FromSnapshotId = fromSnapshotId,
                ToSnapshotId = toSnapshotId,
                Impact = impact,
                Objects = objects,
            };
        }
        catch (InvalidOperationException ex)
        {
            return new CompareView { Error = ex.Message };
        }
    }

    public async Task<List<TimelineLane>> GetLanesAsync(DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default) =>
        await new SiteMetricsReader(_influx, _isp, _logger).LanesAsync(fromUtc, toUtc, cancellationToken);

    /// <returns>The first rebuilt change's ID, or 0 when the current engine finds nothing changed.</returns>
    public async Task<int> RecomputeChangeAsync(int changeId, CancellationToken cancellationToken = default) =>
        (await Engine.RecomputeAsync(changeId, cancellationToken)).FirstOrDefault()?.Id ?? 0;

    public async Task<int?> TakeSnapshotAsync(CancellationToken cancellationToken = default) =>
        (await Engine.TakeSnapshotAsync(ConfigSnapshotTrigger.Manual, Actor, null, cancellationToken))?.Id;

    public Task PinSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default) => SetPinAsync(snapshotId, true, cancellationToken);

    public Task UnpinSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default) => SetPinAsync(snapshotId, false, cancellationToken);

    private Task SetPinAsync(int snapshotId, bool pinned, CancellationToken ct) =>
        Repo.UseAsync(async (r, c) =>
        {
            var s = await r.GetSnapshotAsync(snapshotId, c) ?? throw new InvalidOperationException("Snapshot not found.");
            s.Pinned = pinned;
            s.PinReason = pinned ? $"Pinned by {Actor}" : null;
            await r.UpdateSnapshotAsync(s, c);
        }, ct);

    public async Task<string> DownloadYamlAsync(int? snapshotId, IReadOnlyCollection<string>? collections = null, CancellationToken cancellationToken = default) =>
        YamlConverter.Export((await LoadSideAsync(snapshotId, cancellationToken)).State, collections, includeSecrets: false);

    public async Task<string> DownloadYamlWithSecretsAsync(int? snapshotId, CancellationToken cancellationToken = default) =>
        "# Contains secrets in clear text. Store it like a password.\n"
        + YamlConverter.Export((await LoadSideAsync(snapshotId, cancellationToken)).State, null, includeSecrets: true);

    public async Task<string> DownloadDiffAsync(int? fromSnapshotId, int? toSnapshotId, CancellationToken cancellationToken = default)
    {
        var view = await CompareAsync(fromSnapshotId, toSnapshotId, cancellationToken);
        var sb = new StringBuilder();
        sb.AppendLine($"Compare: {view.FromLabel} ({view.FromDetail}) to {view.ToLabel} ({view.ToDetail})");
        foreach (var i in view.Impact) sb.AppendLine($"{i.Kind}: {i.Title}. {i.Detail}");
        foreach (var o in view.Objects)
        {
            sb.AppendLine($"{o.Object.Action} {o.Object.CollectionName}: {o.Object.Name}{(o.WhenWho == null ? "" : $" ({o.WhenWho})")}");
            foreach (var f in o.Object.Fields)
                sb.AppendLine(f.Secret ? $"  {f.Label}: changed" : $"  {f.Label}: {f.Old} -> {f.New}");
        }
        return sb.ToString();
    }

    // ---------- Edit ----------

    public async Task<(string Yaml, Dictionary<string, int> Counts, string Schema)> GetEditBaseAsync(int? snapshotId, string collection, CancellationToken cancellationToken = default)
    {
        var state = (await LoadSideAsync(snapshotId, cancellationToken)).State;
        var counts = state.Collections.ToDictionary(k => k.Key, k => k.Key == "user" ? k.Value.Count(ConfigDiffEngine.HasClientConfig) : k.Value.Count);
        var schema = ConfigCollections.Get(collection) is { } def ? YamlConverter.EditSchema(state, [def]).ToJsonString() : "{}";
        return (YamlConverter.Export(state, [collection], SecretMode.Mask, ids: true), counts, schema);
    }

    public async Task<string?> RevealSecretAsync(int? snapshotId, string token, CancellationToken cancellationToken = default)
    {
        if (YamlConverter.SecretTokenPath(token) is not { } path)
        {
            _audit.SetTarget(null, "Not a masked secret");
            return null;
        }
        var state = (await LoadSideAsync(snapshotId, cancellationToken)).State;
        if (YamlConverter.FindSecret(state, path) is not { } found)
        {
            _audit.SetTarget(path, "Not found; nothing shown");
            return null;
        }
        // The audit names which secret was shown, never its value.
        _audit.SetTarget(path, $"{found.Def.Noun} {ConfigState.DisplayName(found.Def, found.Object)}: {ChangeDescriber.LabelFor(found.Field)}");
        return YamlConverter.ScalarText(found.Value);
    }

    public Task<string> GenerateSecretAsync(Secrets.SecretKind kind, CancellationToken cancellationToken = default) =>
        Task.FromResult(Secrets.SecretGenerator.Generate(kind));

    private async Task<(ConfigState Desired, List<YamlProblem> Problems, HashSet<string> Collections)> ParseEditsAsync(
        int? snapshotId, IReadOnlyDictionary<string, string> files, CancellationToken ct) =>
        ParseEdits((await LoadSideAsync(snapshotId, ct)).State, files);

    /// <summary>
    /// Edit tab files (one per collection group, keyed by its first collection) parsed onto a base state.
    /// Two passes: the first collects every object the edit creates, so a file can reference an object
    /// another file creates (a WLAN on a new network), whichever order the files come in.
    /// </summary>
    internal static (ConfigState Desired, List<YamlProblem> Problems, HashSet<string> Collections) ParseEdits(
        ConfigState baseState, IReadOnlyDictionary<string, string> files)
    {
        var declared = new ConfigState();
        foreach (var (key, list) in ParseFiles(null).Desired.Collections)
        {
            var created = list.Where(o => ConfigState.IdOf(o).StartsWith(YamlConverter.TempIdPrefix, StringComparison.Ordinal)).ToList();
            if (created.Count > 0) declared.Collections[key] = created;
        }
        var (desired, problems, touched) = ParseFiles(declared);

        (ConfigState Desired, List<YamlProblem> Problems, HashSet<string> Touched) ParseFiles(ConfigState? others)
        {
            var state = baseState.Clone();
            var found = new List<YamlProblem>();
            var collections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (collection, text) in files)
            {
                var def = ConfigCollections.Get(collection);
                if (def == null) continue;
                var parsed = YamlConverter.Parse(text, state, YamlConverter.FileName(def), others);
                found.AddRange(parsed.Problems);
                foreach (var (c, list) in parsed.Collections)
                {
                    state.Collections[c] = list;
                    collections.Add(c);
                }
            }
            return (state, found, collections);
        }
        // A delete takes list references with it, so the plan carries those updates too.
        touched.UnionWith(DeleteCascade.Apply(baseState, desired, touched));
        return (desired, problems, touched);
    }

    public async Task<YamlValidation> ValidateYamlAsync(int? snapshotId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default)
    {
        var baseState = (await LoadSideAsync(snapshotId, cancellationToken)).State;
        var (desired, problems, touched) = await ParseEditsAsync(snapshotId, filesByCollection, cancellationToken);
        var plan = touched.Count == 0 ? null : PlanBuilder.Build(desired, baseState, new PlanOptions { Collections = touched });
        var ops = plan?.Operations ?? [];
        // Plan errors (a kept secret with nothing to keep, a dangling reference) block Review Plan too.
        // Only from live: from a snapshot this plans against the snapshot, while Review Plan plans against live.
        if (snapshotId == null)
        {
            var edited = filesByCollection.Keys.Select(ConfigCollections.Get).OfType<ConfigCollectionDef>().ToList();
            foreach (var error in plan?.Errors ?? [])
            {
                // Errors start with the object's noun ("WLAN Home: ..."); file them under that collection.
                var def = edited.FirstOrDefault(d => error.StartsWith(d.Noun + " ", StringComparison.Ordinal)) ?? edited.FirstOrDefault();
                problems.Add(new YamlProblem("Error", def == null ? "" : YamlConverter.FileName(def), 1, error));
            }
        }
        return new YamlValidation
        {
            Problems = problems,
            EditedObjects = ops.Count,
            EditedCollections = ops.Select(o => o.Collection).Distinct().Count(),
            EditedByCollection = ops.GroupBy(o => o.Collection).ToDictionary(g => g.Key, g => g.Count()),
        };
    }

    public Task<(long SnapshotBytes, long ConsoleBackupBytes)> GetStorageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(_registry.StorageBytes(_site.Slug));

    public async Task<List<(string Name, string EventType, bool Enabled)>> GetAlertRulesAsync(CancellationToken cancellationToken = default) =>
        (await _alerts.GetRulesAsync(cancellationToken))
            .Where(r => r.EventTypePattern.StartsWith("change.", StringComparison.Ordinal))
            .Select(r => (r.Name, r.EventTypePattern, r.IsEnabled)).ToList();

    // ---------- Plans ----------

    public async Task<int> BuildRollbackToSnapshotAsync(int snapshotId, CancellationToken cancellationToken = default)
    {
        var snap = await Repo.UseAsync((r, c) => r.GetSnapshotAsync(snapshotId, c), cancellationToken) ?? throw new InvalidOperationException("Snapshot not found.");
        var state = await Engine.LoadSnapshotStateAsync(snapshotId, cancellationToken) ?? throw new InvalidOperationException("Snapshot content not found.");
        var label = Local(snap.TakenAt).ToString("MMM d, h:mm tt");
        var plan = await Plans.CreateAsync(ConfigPlanKind.RollbackSnapshot, $"Roll Back {await SiteNameAsync(cancellationToken)} to {label}",
            $"Snapshot, {label}", snapshotId, null, state, new PlanOptions(), Actor, cancellationToken,
            doc =>
            {
                doc.Context["source_detail"] = snap.Pinned ? snap.PinReason ?? "Pinned" : TriggerLabel(snap).Text;
                doc.Context["source_taken_at"] = snap.TakenAt.ToString("o");
            });
        return plan.Id;
    }

    public async Task<int> BuildRollbackOfChangeAsync(int changeId, CancellationToken cancellationToken = default)
    {
        var (change, objects) = await Repo.UseAsync(async (r, c) =>
        {
            var ch = await r.GetChangeAsync(changeId, c) ?? throw new InvalidOperationException("Change not found.");
            return (ch, await r.GetChangeObjectsAsync([ch.Id], c));
        }, cancellationToken);
        if (change.PreviousSnapshotId is not { } prev) throw new InvalidOperationException("No snapshot before this change.");
        if ((await Repo.UseAsync((r, c) => r.GetChangeRollbacksAsync(c), cancellationToken)).Any(x => x.ChangeId == change.Id))
            throw new InvalidOperationException("This change is already rolled back.");
        var state = await Engine.LoadSnapshotStateAsync(prev, cancellationToken) ?? throw new InvalidOperationException("Snapshot content not found.");
        var only = objects.Where(o => !o.ObjectId.StartsWith("order:", StringComparison.Ordinal)).Select(o => (o.Collection, o.ObjectId)).ToHashSet();
        var plan = await Plans.CreateAsync(ConfigPlanKind.RollbackChange, $"Roll Back: {change.Summary}",
            $"Change, {Local(change.OccurredAt):MMM d, h:mm tt}", prev, null, state,
            new PlanOptions { OnlyObjects = only, Collections = objects.Select(o => o.Collection).ToHashSet() }, Actor, cancellationToken,
            rollsBackChangeId: change.Id);
        return plan.Id;
    }

    public async Task<int> BuildEditPlanAsync(int? snapshotId, IReadOnlyDictionary<string, string> filesByCollection, CancellationToken cancellationToken = default)
    {
        var baseState = (await LoadSideAsync(snapshotId, cancellationToken)).State;
        var (desired, problems, touched) = ParseEdits(baseState, filesByCollection);
        if (problems.Any(p => p.Level == "Error"))
            throw new InvalidOperationException("Fix the errors before Review Plan.");
        // Name the collections the edit changes, not every collection the uploaded file carries.
        var changed = PlanBuilder.Build(desired, baseState, new PlanOptions { Collections = touched }).Operations
            .Select(o => o.Collection).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = ConfigCollections.All.Where(d => changed.Contains(d.Key)).Select(d => d.DisplayName).ToList();
        var plan = await Plans.CreateAsync(ConfigPlanKind.Edit, $"Edit {await SiteNameAsync(cancellationToken)}: {(named.Count > 0 ? string.Join(", ", named) : "no changes")}",
            snapshotId == null ? "YAML edit of the live config" : "YAML edit of a snapshot", snapshotId, null, desired,
            new PlanOptions { Collections = touched }, Actor, cancellationToken);
        return plan.Id;
    }

    public async Task<int> BuildReturnToBaselineAsync(CancellationToken cancellationToken = default)
    {
        var (state, label, options, snapshotId) = await BaselineStateAsync(cancellationToken) ?? throw new InvalidOperationException("No Baseline is set.");
        var plan = await Plans.CreateAsync(ConfigPlanKind.Baseline, $"Return {await SiteNameAsync(cancellationToken)} to Baseline",
            label, snapshotId, null, state, options, Actor, cancellationToken);
        return plan.Id;
    }

    public async Task<int> BuildApplyTemplateAsync(int templateId, CancellationToken cancellationToken = default)
    {
        var (view, state) = await _templates.LoadAsync(templateId, cancellationToken) ?? throw new InvalidOperationException("Template not found.");
        var options = new PlanOptions { SameSite = false, DeleteMissing = false, Collections = state.Collections.Keys.ToHashSet() };
        var plan = await Plans.CreateAsync(ConfigPlanKind.Template, $"Apply template {view.Name} to {await SiteNameAsync(cancellationToken)}",
            $"Template {view.Name}", null, null, state, options, Actor, cancellationToken);
        return plan.Id;
    }

    private async Task<(ConfigState State, string Label, PlanOptions Options, int? SnapshotId)?> BaselineStateAsync(CancellationToken ct)
    {
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), ct);
        if (settings.BaselineTemplateId is { } tid && await _templates.LoadAsync(tid, ct) is { } t)
            return (t.State, $"Template {t.View.Name}", new PlanOptions { SameSite = false, DeleteMissing = false, Collections = t.State.Collections.Keys.ToHashSet() }, null);
        var baseline = await Repo.UseAsync((r, c) => r.GetBaselineSnapshotAsync(c), ct);
        if (baseline == null) return null;
        var state = await Engine.LoadSnapshotStateAsync(baseline.Id, ct);
        return state == null ? null : (state, $"Baseline, {Local(baseline.TakenAt):MMM d, h:mm tt}", new PlanOptions(), baseline.Id);
    }

    public async Task<PlanView?> GetPlanAsync(int planId, CancellationToken cancellationToken = default)
    {
        var loaded = await Plans.LoadAsync(planId, cancellationToken);
        if (loaded == null) return null;
        var (plan, doc, acks) = loaded.Value;
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var isAdmin = await CallerIsSiteAdminAsync();
        Redact(doc);
        var connection = _registry.Connection(_site.Slug);
        return new PlanView
        {
            Plan = plan,
            Doc = doc,
            Acks = acks,
            Result = string.IsNullOrEmpty(plan.ResultJson) ? null : JsonSerializer.Deserialize<PlanResult>(plan.ResultJson),
            SiteName = await SiteNameAsync(cancellationToken),
            SiteDetail = connection.IsViaAgent ? "On-Site Agent" : "direct",
            ApprovalsOn = settings.ApprovalsEnabled,
            ApprovalRequired = ApprovalRequired(settings, plan, doc, isAdmin),
            CallerIsSiteAdmin = isAdmin,
            Remaining = PlanReadiness.Remaining(doc, acks, isAdmin),
            ConfirmationText = PlanReadiness.ConfirmationText(doc),
            WatchMinutes = connection.IsViaAgent ? settings.AgentCommitWindowMinutes : settings.CommitWindowMinutes,
            DeviceCount = doc.Context.TryGetValue("watch_online", out var w) ? w.Split(',', StringSplitOptions.RemoveEmptyEntries).Length : 0,
            QuietestHourSuggestion = settings.ScheduleQuietestHour && !ConfigPlanStatuses.Terminal.Contains(plan.Status)
                ? await QuietestHourAsync(cancellationToken) : null,
        };
    }

    /// <summary>The next start of the hour with the least WAN traffic over the last 14 days.</summary>
    private async Task<DateTime?> QuietestHourAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var throughput = await new SiteMetricsReader(_influx, _isp, _logger).WanThroughputAsync(now.AddDays(-14), now, ct);
        if (throughput.Count < 24) return null;
        var byHour = throughput
            .Where(p => p.Down != null)
            .GroupBy(p => Local(p.Time).Hour)
            .Select(g => (Hour: g.Key, Mean: g.Average(p => (p.Down ?? 0) + (p.Up ?? 0))))
            .OrderBy(x => x.Mean).FirstOrDefault();
        var localNow = Local(now);
        var next = localNow.Date.AddHours(byHour.Hour);
        if (next <= localNow) next = next.AddDays(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), TimeZoneInfo.Local);
    }

    /// <summary>Removes write bodies before a plan leaves the service: they can hold secrets.</summary>
    private static void Redact(PlanDocument doc)
    {
        foreach (var op in doc.Operations) op.Body = null;
    }

    /// <summary>
    /// Approvals on: Operators submit, Admins apply their own plans directly, except a plan with a
    /// security regression, which needs a Site Admin's approval even for an Admin. The emergency
    /// exception lets an Operator roll back to a snapshot from the last 24 hours without approval.
    /// </summary>
    internal static bool ApprovalRequired(ChangeControlSettings settings, ConfigPlan plan, PlanDocument doc, bool isAdmin)
    {
        if (!settings.ApprovalsEnabled) return false;
        if (doc.Regressions.Count > 0) return true;
        if (isAdmin) return false;
        if (settings.EmergencyRollbackWithoutApproval && plan.Kind == ConfigPlanKind.RollbackSnapshot
            && doc.Context.TryGetValue("source_taken_at", out var taken)
            && DateTime.TryParse(taken, null, System.Globalization.DateTimeStyles.RoundtripKind, out var takenAt)
            && DateTime.UtcNow - takenAt <= TimeSpan.FromHours(24))
            return false;
        return true;
    }

    public async Task<List<PlanSummaryView>> GetPlansAsync(bool inProgressOnly, CancellationToken cancellationToken = default)
    {
        var name = await SiteNameAsync(cancellationToken);
        var plans = await Repo.UseAsync((r, c) => r.GetPlansAsync(inProgressOnly ? ConfigPlanStatuses.InProgress : null, 100, c), cancellationToken);
        return plans.Select(p => Summary(p, _site.Slug, name)).ToList();
    }

    internal static PlanSummaryView Summary(ConfigPlan p, string slug, string siteName) => new()
    {
        Id = p.Id,
        SiteSlug = slug,
        SiteName = siteName,
        Name = p.Name,
        Status = p.Status,
        WatchUntil = p.WatchUntil,
        StartedAt = p.CompletedAt,
        Detail = p.Status switch
        {
            ConfigPlanStatus.PendingApproval => $"{p.ObjectCount} objects{(p.ScheduledFor is { } s ? $", scheduled {Local(s):MMM d, h:mm tt}" : "")}",
            ConfigPlanStatus.Scheduled or ConfigPlanStatus.Approved => p.ScheduledFor is { } s2 ? $"Scheduled {Local(s2):MMM d, h:mm tt}" : "Approved, ready to apply",
            ConfigPlanStatus.Watching => $"Commit-confirmed until {Local(p.WatchUntil ?? DateTime.UtcNow):h:mm tt}",
            ConfigPlanStatus.Applied => $"Applied {Local(p.CompletedAt ?? p.UpdatedAt):MMM d, h:mm tt}",
            ConfigPlanStatus.Reverted => p.Message ?? "Reverted",
            ConfigPlanStatus.Failed => p.Message ?? "Failed",
            _ => $"{p.ObjectCount} objects, {p.DeviceCount} devices",
        },
    };

    public async Task SavePlanAsync(int planId, PlanAcknowledgements acks, DateTime? scheduledForUtc, bool applyOnApproval, CancellationToken cancellationToken = default)
    {
        await Plans.SaveAcknowledgementsAsync(planId, acks, cancellationToken);
        await Plans.ScheduleAsync(planId, scheduledForUtc, applyOnApproval, cancellationToken);
    }

    public Task SubmitPlanAsync(int planId, CancellationToken cancellationToken = default) => Plans.SubmitAsync(planId, Actor, cancellationToken);

    public Task ApprovePlanAsync(int planId, CancellationToken cancellationToken = default) => Plans.ApproveAsync(planId, Actor, cancellationToken);

    public async Task<string?> ApplyPlanAsync(int planId, CancellationToken cancellationToken = default)
    {
        var loaded = await Plans.LoadAsync(planId, cancellationToken) ?? throw new InvalidOperationException("Plan not found.");
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        var isAdmin = await CallerIsSiteAdminAsync();
        var needsApproval = ApprovalRequired(settings, loaded.Plan, loaded.Doc, isAdmin);
        if (loaded.Plan.ScheduledFor is { } at && at > DateTime.UtcNow && !needsApproval)
        {
            // A schedule set on an approval-free plan books it rather than applying now. Readiness is
            // checked here with the booker's role: the scheduled tick applies without a person present.
            var remaining = PlanReadiness.Remaining(loaded.Doc, loaded.Acks, isAdmin);
            if (remaining.Count > 0) return "Still open: " + string.Join(", ", remaining) + ".";
            var plan = loaded.Plan;
            plan.Status = ConfigPlanStatus.Scheduled;
            plan.AppliedBy = Actor;
            await Repo.UseAsync((r, c) => r.UpdatePlanAsync(plan, c), cancellationToken);
            return null;
        }
        return await Plans.ApplyAsync(planId, new PlanActor(Actor, isAdmin), needsApproval, automatic: false, cancellationToken);
    }

    public Task CancelPlanAsync(int planId, CancellationToken cancellationToken = default) => Plans.CancelAsync(planId, cancellationToken);

    public async Task<string?> RevertPlanAsync(int planId, CancellationToken cancellationToken = default)
    {
        var loaded = await Plans.LoadAsync(planId, cancellationToken) ?? throw new InvalidOperationException("Plan not found.");
        var isAdmin = await CallerIsSiteAdminAsync();
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        // An Operator reverts without approval only inside the emergency window (spec: 24 hours).
        var recent = loaded.Plan.CompletedAt is { } done && DateTime.UtcNow - done <= TimeSpan.FromHours(24);
        if (!isAdmin && settings.ApprovalsEnabled && !(settings.EmergencyRollbackWithoutApproval && recent))
            return "Approvals are on for this site. A Site Admin reverts this plan, or roll back to the snapshot taken before it from Snapshots and submit that plan.";
        return await Plans.RevertAsync(planId, $"Reverted by {Actor}", [], automatic: false, cancellationToken, new PlanActor(Actor, isAdmin));
    }

    public async Task<byte[]?> GetPlanReportPdfAsync(int planId, CancellationToken cancellationToken = default)
    {
        var plan = await Repo.UseAsync((r, c) => r.GetPlanAsync(planId, c), cancellationToken);
        var report = RolloutReport.Parse(plan?.ReportJson);
        return report == null ? null : new RolloutReportPdfGenerator().GenerateReportBytes(report, await SiteNameAsync(cancellationToken));
    }

    // ---------- Settings, Baseline, drift ----------

    public Task<ChangeControlSettings> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);

    public async Task SaveSettingsAsync(ChangeControlSettings settings, CancellationToken cancellationToken = default)
    {
        settings.KeepEveryChangeDays = Math.Clamp(settings.KeepEveryChangeDays, 1, 3650);
        settings.KeepDailyDays = Math.Clamp(settings.KeepDailyDays, settings.KeepEveryChangeDays, 3650);
        settings.PollIntervalSeconds = Math.Clamp(settings.PollIntervalSeconds, 30, 600);
        settings.CommitWindowMinutes = Math.Clamp(settings.CommitWindowMinutes, 1, 60);
        settings.AgentCommitWindowMinutes = Math.Clamp(settings.AgentCommitWindowMinutes, 1, 120);
        settings.ConsoleBackupsToKeep = Math.Clamp(settings.ConsoleBackupsToKeep, 0, 100);
        await Repo.UseAsync((r, c) => r.SaveSettingsAsync(settings, c), cancellationToken);
        Engine.PollSoon();
    }

    public async Task SetBaselineAsync(int? snapshotId, CancellationToken cancellationToken = default)
    {
        await Repo.UseAsync(async (r, c) =>
        {
            await r.SetBaselineSnapshotAsync(snapshotId, c);
            if (snapshotId != null)
            {
                var settings = await r.GetSettingsAsync(c);
                settings.BaselineTemplateId = null;
                await r.SaveSettingsAsync(settings, c);
            }
        }, cancellationToken);
    }

    public async Task AcceptLiveAsBaselineAsync(CancellationToken cancellationToken = default)
    {
        var snap = await Engine.TakeSnapshotAsync(ConfigSnapshotTrigger.Manual, Actor, $"Baseline, accepted by {Actor}", cancellationToken)
                   ?? throw new InvalidOperationException("Could not read the live config.");
        await SetBaselineAsync(snap.Id, cancellationToken);
    }

    public async Task SetBaselineTemplateAsync(int? templateId, CancellationToken cancellationToken = default)
    {
        await Repo.UseAsync(async (r, c) =>
        {
            var settings = await r.GetSettingsAsync(c);
            settings.BaselineTemplateId = templateId;
            await r.SaveSettingsAsync(settings, c);
            if (templateId != null) await r.SetBaselineSnapshotAsync(null, c);
        }, cancellationToken);
    }

    public async Task<DriftView> GetDriftAsync(CancellationToken cancellationToken = default)
    {
        var baseline = await BaselineStateAsync(cancellationToken);
        if (baseline is not { } b) return new DriftView();
        var latest = await Repo.UseAsync((r, c) => r.GetLatestSnapshotAsync(c), cancellationToken);
        if (latest == null || await Engine.LoadSnapshotStateAsync(latest.Id, cancellationToken) is not { } current) return new DriftView { HasBaseline = true, BaselineLabel = b.Label };
        var objects = DriftCalculator.Drift(b.State, current, b.Options);
        var settings = await Repo.UseAsync((r, c) => r.GetSettingsAsync(c), cancellationToken);
        return new DriftView
        {
            HasBaseline = true,
            BaselineLabel = b.Label,
            BaselineSnapshotId = b.SnapshotId,
            BaselineTemplateId = settings.BaselineTemplateId,
            Objects = objects.Select(o => new CompareObjectView { Object = ObjectView(o) }).ToList(),
        };
    }
}
