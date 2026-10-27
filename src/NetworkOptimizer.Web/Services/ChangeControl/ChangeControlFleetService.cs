using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Authorization;
using NetworkOptimizer.Web.Services.Gates;
using NetworkOptimizer.Web.Services.Monitoring.IspHealth;

namespace NetworkOptimizer.Web.Services.ChangeControl;

/// <summary>One site on Overview and on the timeline.</summary>
public sealed class FleetSiteView
{
    public string Slug { get; init; } = "";
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public bool Online { get; init; }
    public bool AgentOffline { get; init; }
    public DateTime? LastSnapshotAt { get; init; }
    public int Changes7d { get; init; }
    public int Changes24h { get; init; }
    public int Regressions { get; init; }
    public int Unattributed { get; init; }
    public int? DriftObjects { get; init; }
    public string? NetworkAppVersion { get; init; }
    /// <summary>Average Site Health over the last 24 hours; null with no data.</summary>
    public double? Health24h { get; init; }
    public string? HealthIssue { get; init; }
}

/// <summary>A Needs Attention item.</summary>
public sealed class AttentionItem
{
    /// <summary>"Regression", "Approval", "Gap", "Reverted", "Drift".</summary>
    public string Kind { get; init; } = "";
    public string SiteSlug { get; init; } = "";
    public string SiteName { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public int? ChangeId { get; init; }
    public int? PlanId { get; init; }
    public int? FromSnapshotId { get; init; }
    public int? ToSnapshotId { get; init; }
    public DateTime At { get; init; }

    /// <summary>Stable key for Dismiss: "regression:{audit issue key}" or "plan:{id}". Null when it cannot be dismissed.</summary>
    public string? Key { get; init; }

    /// <summary>Who dismissed it and when; null while it shows.</summary>
    public string? DismissedBy { get; init; }
    public DateTime? DismissedAt { get; init; }
}

/// <summary>A Last Network App Upgrade row.</summary>
public sealed record UpgradeRow(string SiteSlug, string SiteName, DateTime At, string From, string To, int ObjectsChanged, int Regressions, int? ChangeId);

/// <summary>The Overview tab.</summary>
public sealed class OverviewView
{
    public List<FleetSiteView> Sites { get; init; } = [];
    public List<AttentionItem> Attention { get; init; } = [];
    public List<PlanSummaryView> Plans { get; init; } = [];
    public List<UpgradeRow> Upgrades { get; init; } = [];
}

/// <summary>A gap or incident band on a site's row.</summary>
public sealed record TimelineBand(DateTime Start, DateTime End, string Kind, string Label);

/// <summary>A site row on the timeline.</summary>
public sealed class TimelineSiteRow
{
    public FleetSiteView Site { get; init; } = new();
    public List<(DateTime Time, double? Value)> Sparkline { get; init; } = [];
    public List<ChangeRowView> Markers { get; init; } = [];
    public List<TimelineBand> Bands { get; init; } = [];
    public int ChangeCount { get; init; }
    public int RegressionCount { get; init; }
    /// <summary>Site Health over the range and the period before it, from the stored hours.</summary>
    public SiteHealth.SiteHealthSeries Health { get; init; } = new();
}

/// <summary>
/// Change Control across every site the caller can see (spec: the multi-site timeline shows only
/// sites the user is a member of). Not site-scoped: it reads each authorized site through its own
/// engine, and narrows to the caller's sites with the site-access filter.
/// </summary>
[MutatingService]
public interface IChangeControlFleetService
{
    /// <summary>The Overview tab.</summary>
    [RequireRole(Roles.Viewer)]
    Task<OverviewView> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Timeline rows for the chosen sites (all visible when null).</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<TimelineSiteRow>> GetTimelineAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default);

    /// <summary>Changes across the chosen sites, newest first.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<ChangeRowView>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default);

    /// <summary>The stat lanes of one site (the expanded timeline row).</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<TimelineLane>> GetLanesAsync(string siteSlug, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default);

    /// <summary>The sites the caller can see, for the site pickers.</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<FleetSiteView>> GetSitesAsync(CancellationToken cancellationToken = default);

    /// <summary>A change's detail on any site the caller can see.</summary>
    [RequireRole(Roles.Viewer)]
    Task<ChangeDetailView?> GetChangeDetailAsync(string siteSlug, int changeId, CancellationToken cancellationToken = default);

    /// <summary>Each visible site's last 24 hours of Site Health and what needs attention, worst first (Dashboard, Sites page).</summary>
    [RequireRole(Roles.Viewer)]
    Task<List<SiteHealth.SiteHealthSummary>> GetSiteHealthAsync(CancellationToken cancellationToken = default);

    /// <summary>Hides a Needs Attention item on a site until it is restored.</summary>
    [RequireSiteRole(SiteRole.SiteOperator)]
    [AuditAction(AuditActions.ChangeControlAttentionDismissed, TargetType = "attention_item")]
    Task DismissAttentionAsync([SiteSlug] string siteSlug, string itemKey, string title, CancellationToken cancellationToken = default);

    /// <summary>Shows a dismissed Needs Attention item again.</summary>
    [RequireSiteRole(SiteRole.SiteOperator)]
    [AuditAction(AuditActions.ChangeControlAttentionRestored, TargetType = "attention_item")]
    Task RestoreAttentionAsync([SiteSlug] string siteSlug, string itemKey, CancellationToken cancellationToken = default);
}

/// <summary>The fleet service.</summary>
public sealed class ChangeControlFleetService : IChangeControlFleetService
{
    private readonly ChangeControlRegistry _registry;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly ISiteAccessFilter _access;
    private readonly MonitoringInfluxRegistry _influx;
    private readonly IspHealthRegistry _isp;
    private readonly ILogger<ChangeControlFleetService> _logger;
    private readonly IServiceScopeFactory _scopes;
    private readonly Identity.ICallerContext _caller;

    public ChangeControlFleetService(ChangeControlRegistry registry, IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        ISiteAccessFilter access, MonitoringInfluxRegistry influx, IspHealthRegistry isp, IServiceScopeFactory scopes,
        Identity.ICallerContext caller, ILogger<ChangeControlFleetService> logger)
    {
        _scopes = scopes;
        _caller = caller;
        _registry = registry;
        _mainDb = mainDb;
        _access = access;
        _influx = influx;
        _isp = isp;
        _logger = logger;
    }

    private async Task<List<(string Slug, string Name, bool IsDefault)>> SitesAsync(IReadOnlyCollection<string>? only, CancellationToken ct)
    {
        var running = _registry.RunningSites().ToHashSet(StringComparer.OrdinalIgnoreCase);
        await using var db = await _mainDb.CreateDbContextAsync(ct);
        var rows = await db.Sites.AsNoTracking().Where(s => s.Enabled).Select(s => new { s.Slug, s.Name, s.IsDefault }).ToListAsync(ct);
        var sites = rows.Where(r => running.Contains(r.Slug) || r.IsDefault).Select(r => (r.Slug, r.Name, r.IsDefault)).ToList();
        if (sites.Count == 0) sites.Add((SiteManagementService.DefaultSiteSlug, "Default", true));
        sites = await _access.FilterAsync(sites, s => s.Slug);
        return only == null ? sites : sites.Where(s => only.Contains(s.Slug, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private async Task<FleetSiteView> SiteViewAsync((string Slug, string Name, bool IsDefault) site, bool withDrift, CancellationToken ct)
    {
        var engine = _registry.GetFor(site.Slug);
        var connection = _registry.Connection(site.Slug);
        var now = DateTime.UtcNow;
        var (latest, changes, state, last, hours) = await engine.Repositories.UseAsync(async (r, c) =>
            (await r.GetLatestSnapshotAsync(c), await r.GetChangesAsync(now.AddDays(-7), now, 2000, c), await r.GetDetectionStateAsync(c),
             await r.GetLastConfigChangeAsync(c), await r.GetHealthHoursAsync(now.AddHours(-24), now, c)), ct);
        var health = SiteHealth.SiteHealthSeriesBuilder.Build(_registry.WithProvisionalHealth(site.Slug, hours), now.AddHours(-24), now);
        var config = changes.Where(x => x.Kind == ConfigChangeKind.Config && x.ActorKind != ConfigChangeActorKind.NetworkOptimizer).ToList();
        var counted = changes.Where(x => x.ActorKind != ConfigChangeActorKind.NetworkOptimizer || x.PlanId != null).ToList();
        int? drift = null;
        if (withDrift)
        {
            try
            {
                drift = await DriftCountAsync(engine, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Drift count failed for {Site}", site.Slug);
            }
        }
        return new FleetSiteView
        {
            Slug = site.Slug,
            Name = site.Name,
            // The connection state first: an empty row then reads as unseen, not unchanged.
            Detail = connection.IsAwaitingAgent ? "Agent offline"
                : !connection.IsConnected ? "Console unreachable"
                : latest == null ? "No snapshot yet"
                : last == null ? "No changes recorded"
                : $"Last change {LastChangeText(last.OccurredAt, now)}",
            Online = connection.IsConnected,
            AgentOffline = connection.IsAwaitingAgent,
            LastSnapshotAt = latest?.TakenAt,
            // Counted as the timeline counts: config and firmware alike, NO's own only when a plan applied them.
            Changes7d = counted.Count,
            Changes24h = counted.Count(c => c.OccurredAt > now.AddDays(-1)),
            Regressions = config.Sum(c => c.RegressionCount),
            Unattributed = config.Count(c => c.ActorKind is ConfigChangeActorKind.NoAdminRecorded or ConfigChangeActorKind.LikelyUniFi),
            DriftObjects = drift,
            NetworkAppVersion = state.NetworkAppVersion,
            Health24h = health.Average,
            HealthIssue = health.TopIssue,
        };
    }

    private static async Task<int?> DriftCountAsync(ChangeControlSiteEngine engine, CancellationToken ct)
    {
        var (baseline, latest) = await engine.Repositories.UseAsync(async (r, c) => (await r.GetBaselineSnapshotAsync(c), await r.GetLatestSnapshotAsync(c)), ct);
        if (baseline == null || latest == null) return null;
        if (baseline.Id == latest.Id) return 0;
        var b = await engine.LoadSnapshotStateAsync(baseline.Id, ct);
        var l = await engine.LoadSnapshotStateAsync(latest.Id, ct);
        return b == null || l == null ? null : DriftCalculator.Drift(b, l, new PlanOptions()).Count;
    }

    public async Task<List<FleetSiteView>> GetSitesAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<FleetSiteView>();
        foreach (var s in await SitesAsync(null, cancellationToken))
            result.Add(await SiteViewAsync(s, withDrift: false, cancellationToken));
        return result;
    }

    public async Task<OverviewView> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var view = new OverviewView();
        var now = DateTime.UtcNow;
        foreach (var site in await SitesAsync(null, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            var siteView = await SiteViewAsync(site, withDrift: true, cancellationToken);
            view.Sites.Add(siteView);

            var (changes, plans, gaps, snapshots, dismissedList) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetChangesAsync(now.AddDays(-30), now, 2000, c),
                 await r.GetPlansAsync(null, 50, c),
                 await r.GetGapsAsync(now.AddDays(-7), now, c),
                 await r.GetSnapshotsAsync(null, null, 200, c),
                 await r.GetDismissalsAsync(c)), cancellationToken);
            var dismissed = dismissedList.ToDictionary(d => d.ItemKey, StringComparer.Ordinal);
            void Add(AttentionItem item) => view.Attention.Add(item.Key != null && dismissed.TryGetValue(item.Key, out var d)
                ? new AttentionItem
                {
                    Kind = item.Kind,
                    SiteSlug = item.SiteSlug,
                    SiteName = item.SiteName,
                    Title = item.Title,
                    Detail = item.Detail,
                    ChangeId = item.ChangeId,
                    PlanId = item.PlanId,
                    FromSnapshotId = item.FromSnapshotId,
                    ToSnapshotId = item.ToSnapshotId,
                    At = item.At,
                    Key = item.Key,
                    DismissedBy = d.DismissedBy,
                    DismissedAt = d.DismissedAt,
                }
                : item);

            // A regression shows once, from its newest change, and only while the newest audited snapshot still has it.
            var current = CurrentIssueKeys(snapshots);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var ch in changes.Where(c => c.Kind == ConfigChangeKind.Config && c.RegressionCount > 0 && c.OccurredAt > now.AddDays(-7)))
            {
                var upgrade = ch.ActorKind == ConfigChangeActorKind.LikelyUniFi && ch.Summary.Contains("Network app upgrade", StringComparison.Ordinal);
                var lines = Regressions(ch).Where(l => (current == null || current.Contains(l.Key)) && !seen.Contains(l.Key)).ToList();
                if (lines.Count == 0) continue;
                seen.UnionWith(lines.Select(l => l.Key));
                Add(new AttentionItem
                {
                    Kind = "Regression",
                    SiteSlug = site.Slug,
                    SiteName = site.Name,
                    At = ch.OccurredAt,
                    ChangeId = ch.Id,
                    FromSnapshotId = upgrade ? ch.PreviousSnapshotId : null,
                    ToSnapshotId = upgrade ? ch.SnapshotId : null,
                    Title = upgrade ? $"{lines.Count} regression{(lines.Count == 1 ? "" : "s")} from the Network app upgrade" : string.Join("; ", lines.Select(l => l.Title)),
                    Detail = upgrade ? ch.Summary : $"{ch.Summary}. {ChangeControlService.Who(ch)}.",
                    Key = $"regression:{string.Join("|", lines.Select(l => l.Key).Order(StringComparer.Ordinal))}",
                });
            }
            // What a Network app upgrade deleted: recorded in a change of hundreds of rewrites, where nobody would find it.
            var upgradeChanges = changes.Where(c => c.Kind == ConfigChangeKind.Config && c.OccurredAt > now.AddDays(-7)
                && c.ActorKind == ConfigChangeActorKind.LikelyUniFi && c.Summary.Contains("Network app upgrade", StringComparison.Ordinal)).ToList();
            if (upgradeChanges.Count > 0)
            {
                var objects = await engine.Repositories.UseAsync((r, c) => r.GetChangeObjectsAsync(upgradeChanges.Select(u => u.Id).ToList(), c), cancellationToken);
                foreach (var ch in upgradeChanges)
                {
                    var deleted = objects.Where(o => o.ChangeId == ch.Id && o.Action == ConfigChangeAction.Deleted && !UpgradeRetired.Contains(o.Collection)).ToList();
                    if (deleted.Count == 0) continue;
                    Add(new AttentionItem
                    {
                        Kind = "Deleted",
                        SiteSlug = site.Slug,
                        SiteName = site.Name,
                        At = ch.OccurredAt,
                        ChangeId = ch.Id,
                        Title = $"The Network app upgrade deleted {string.Join(", ", deleted.Select(o => o.ObjectName))}",
                        Detail = $"Restore {(deleted.Count == 1 ? "it" : "them")} from the snapshot taken before the upgrade, in Migrate / Restore.",
                        Key = $"upgrade-deleted:{ch.Id}",
                    });
                }
            }
            foreach (var p in plans.Where(p => p.Status == ConfigPlanStatus.PendingApproval))
                Add(new AttentionItem
                {
                    Kind = "Approval",
                    SiteSlug = site.Slug,
                    SiteName = site.Name,
                    At = p.SubmittedAt ?? p.CreatedAt,
                    PlanId = p.Id,
                    Title = p.Name,
                    Detail = $"Submitted by {p.SubmittedBy}." + (p.ScheduledFor is { } s ? $" Scheduled {Local(s):MMM d, h:mm tt} once approved." : "")
                             + (p.HighestTier >= PlanTiers.Lockout ? " Possible lockout." : ""),
                });
            // Only automatic reverts: one someone pressed is in the plan history already. A Roll Back's revert plan says "Roll Back".
            var manual = plans.Where(p => p.RevertOfPlanId != null && p.CreatedBy == "Roll Back").Select(p => p.RevertOfPlanId!.Value).ToHashSet();
            foreach (var p in plans.Where(p => p.Status == ConfigPlanStatus.Reverted && p.CompletedAt > now.AddDays(-2) && !manual.Contains(p.Id)))
                Add(new AttentionItem
                {
                    Kind = "Reverted",
                    SiteSlug = site.Slug,
                    SiteName = site.Name,
                    At = p.CompletedAt ?? now,
                    PlanId = p.Id,
                    Title = $"Plan reverted: {p.Name}",
                    Detail = p.Message ?? "",
                    Key = $"plan:{p.Id}",
                });
            // A gap shorter than this is a restart or a reconnect, not something to look at.
            foreach (var g in gaps.Where(g => g.EndedAt == null && now - g.StartedAt >= TimeSpan.FromMinutes(5)))
                Add(new AttentionItem
                {
                    Kind = "Gap",
                    SiteSlug = site.Slug,
                    SiteName = site.Name,
                    At = g.StartedAt,
                    Title = g.Reason.Contains("agent", StringComparison.OrdinalIgnoreCase) ? $"Agent offline for {Span(now - g.StartedAt)}" : $"UniFi Console unreachable for {Span(now - g.StartedAt)}",
                    Detail = "Change detection is paused. Network Optimizer catches up from the UniFi audit log when it reconnects.",
                });
            if (siteView.DriftObjects is > 0)
                Add(new AttentionItem
                {
                    Kind = "Drift",
                    SiteSlug = site.Slug,
                    SiteName = site.Name,
                    At = now,
                    Title = $"{siteView.DriftObjects} objects drifted from the Baseline",
                    Detail = "Return to Baseline opens a plan; Accept as Baseline keeps them.",
                });

            // Drafts too: Save Draft is how a plan is kept for later, and this is where it is found again.
            view.Plans.AddRange(plans.Where(p => p.Status == ConfigPlanStatus.Draft || ConfigPlanStatuses.InProgress.Contains(p.Status))
                .Select(p => ChangeControlService.Summary(p, site.Slug, site.Name)));

            var upgradeRow = changes.Where(c => c.Kind == ConfigChangeKind.NetworkAppUpgrade).OrderByDescending(c => c.OccurredAt).FirstOrDefault();
            if (upgradeRow != null)
            {
                var during = changes.Where(c => c.Kind == ConfigChangeKind.Config && c.OccurredAt >= upgradeRow.OccurredAt
                    && c.OccurredAt <= upgradeRow.OccurredAt + ChangeAttributor.UpgradeWindow && c.ActorKind == ConfigChangeActorKind.LikelyUniFi).ToList();
                view.Upgrades.Add(new UpgradeRow(site.Slug, site.Name, upgradeRow.OccurredAt, upgradeRow.VersionFrom ?? "", upgradeRow.VersionTo ?? "",
                    during.Sum(c => c.ObjectCount), during.Sum(c => c.RegressionCount), during.FirstOrDefault()?.Id));
            }
        }
        var order = new Dictionary<string, int> { ["Reverted"] = 0, ["Deleted"] = 1, ["Regression"] = 1, ["Approval"] = 2, ["Gap"] = 3, ["Drift"] = 4 };
        view.Attention.Sort((a, b) => order[a.Kind] != order[b.Kind] ? order[a.Kind].CompareTo(order[b.Kind]) : b.At.CompareTo(a.At));
        view.Upgrades.Sort((a, b) => b.At.CompareTo(a.At));
        return view;
    }

    /// <summary>Collections an upgrade empties on purpose (Network 11 dropped WLAN groups), and churn: not worth an item.</summary>
    private static readonly HashSet<string> UpgradeRetired = new(StringComparer.Ordinal) { "wlangroup", "channelplan" };

    private static List<ImpactLine> Regressions(ConfigChange c) =>
        string.IsNullOrEmpty(c.ImpactJson) ? []
            : (System.Text.Json.JsonSerializer.Deserialize<List<ImpactLine>>(c.ImpactJson) ?? []).Where(l => l.Kind == "Regression").ToList();

    /// <summary>Audit issue keys in the newest snapshot the Security Audit ran on; null when none has.</summary>
    internal static HashSet<string>? CurrentIssueKeys(IEnumerable<ConfigSnapshot> newestFirst)
    {
        var json = newestFirst.FirstOrDefault(s => s.AuditIssuesJson != null)?.AuditIssuesJson;
        if (json == null) return null;
        var issues = System.Text.Json.JsonSerializer.Deserialize<List<ImpactLine>>(json) ?? [];
        return issues.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
    }

    public async Task DismissAttentionAsync(string siteSlug, string itemKey, string title, CancellationToken cancellationToken = default)
    {
        var by = _caller.Current?.ActorName is { Length: > 0 } n ? n : "unknown";
        await _registry.GetFor(siteSlug).Repositories.UseAsync((r, c) => r.AddDismissalAsync(new AttentionDismissal
        {
            ItemKey = itemKey,
            Title = title.Length > 300 ? title[..300] : title,
            DismissedBy = by,
            DismissedAt = DateTime.UtcNow,
        }, c), cancellationToken);
    }

    public Task RestoreAttentionAsync(string siteSlug, string itemKey, CancellationToken cancellationToken = default) =>
        _registry.GetFor(siteSlug).Repositories.UseAsync((r, c) => r.RemoveDismissalAsync(itemKey, c), cancellationToken);

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);

    /// <summary>"at 2:14 PM" today, "Oct 21" this year, "Oct 21, 2025" before that.</summary>
    internal static string LastChangeText(DateTime atUtc, DateTime nowUtc)
    {
        var at = Local(atUtc);
        var now = Local(nowUtc);
        return at.Date == now.Date ? $"at {at:h:mm tt}" : at.Year == now.Year ? at.ToString("MMM d") : at.ToString("MMM d, yyyy");
    }

    private static string Span(TimeSpan t) => t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} minutes" : t.TotalHours < 48 ? $"{(int)t.TotalHours} hours" : $"{(int)t.TotalDays} days";

    public async Task<List<ChangeRowView>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default)
    {
        var rows = new List<ChangeRowView>();
        foreach (var site in await SitesAsync(sites, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            // Every change is returned: the Network Optimizer source filter on Changes decides whether automatic writes show.
            var (changes, incidents, rollbacks, summaries, appliers) = await engine.Repositories.UseAsync(async (r, c) =>
            {
                var all = await r.GetChangesAsync(fromUtc, toUtc, 2000, c);
                return (all, await ChangeIncidents.StartedAsync(r, fromUtc, toUtc + ChangeControlSiteEngine.IncidentWindow, c),
                    ChangeControlService.RollbacksByChange(await r.GetChangeRollbacksAsync(c)),
                    await ChangeControlService.SummariesAsync(r, all, c), await ChangeControlService.PlanAppliersAsync(r, all, c));
            }, cancellationToken);
            appliers = await ChangeControlService.ShownAsync(appliers, _scopes, cancellationToken);
            rows.AddRange(changes.Select(x => ChangeControlService.Row(x, site.Slug, site.Name, ChangeControlService.LinkedAfter(x, incidents),
                rollbacks, summaries, appliers)));
        }
        return rows.OrderByDescending(r => r.OccurredAt).ToList();
    }

    // A closed gap shorter than this is a deploy or restart, and stays off the timeline.
    private static readonly TimeSpan ShortGap = TimeSpan.FromMinutes(15);

    public async Task<List<TimelineSiteRow>> GetTimelineAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default)
    {
        var result = new List<TimelineSiteRow>();
        var changes = await GetChangesAsync(fromUtc, toUtc, sites, cancellationToken);
        foreach (var site in await SitesAsync(sites, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            var view = await SiteViewAsync(site, withDrift: false, cancellationToken);
            var span = toUtc - fromUtc;
            var (gaps, incidents, hours) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetGapsAsync(fromUtc, toUtc, c), await ChangeIncidents.StartedAsync(r, fromUtc, toUtc, c),
                 await r.GetHealthHoursAsync(SiteHealth.SiteHealthJob.FloorHour(fromUtc - span), toUtc, c)), cancellationToken);
            hours = _registry.WithProvisionalHealth(site.Slug, hours);
            var health = SiteHealth.SiteHealthSeriesBuilder.Build(hours, SiteHealth.SiteHealthJob.FloorHour(fromUtc), toUtc,
                hours.Where(h => h.HourStart < fromUtc && h.HourStart >= fromUtc - span).ToList());
            var bands = gaps.Where(g => g.EndedAt is not { } end || end - g.StartedAt >= ShortGap).Select(g => new TimelineBand(g.StartedAt, g.EndedAt ?? DateTime.UtcNow, "gap", g.Reason))
                .Concat(incidents.Select(s => new TimelineBand(s.Incident.FirstTriggeredAt, s.End, "incident", s.Incident.Title))).ToList();
            var markers = changes.Where(c => c.SiteSlug == site.Slug).ToList();
            // The site row draws Site Health from the stored hours, so a timeline load reads no Influx.
            result.Add(new TimelineSiteRow
            {
                Site = view,
                Markers = markers,
                Bands = bands,
                Sparkline = health.Points.Select(p => (p.Start, p.Health)).ToList(),
                ChangeCount = markers.Count,
                RegressionCount = markers.Sum(m => m.RegressionCount),
                Health = health,
            });
        }
        return result;
    }

    private SiteMetricsReader Reader(string slug) =>
        new(_influx.GetFor(slug), _isp.GetFor(slug, null), _logger);

    public async Task<List<TimelineLane>> GetLanesAsync(string siteSlug, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        if (!await _access.IsAuthorizedAsync(siteSlug)) return [];
        var hours = await _registry.GetFor(siteSlug).Repositories.UseAsync((r, c) =>
            r.GetHealthHoursAsync(SiteHealth.SiteHealthJob.FloorHour(fromUtc), toUtc, c), cancellationToken);
        var lanes = await Reader(siteSlug).LanesAsync(fromUtc, toUtc, cancellationToken);
        lanes.Insert(0, HealthLane(SiteHealth.SiteHealthSeriesBuilder.Build(_registry.WithProvisionalHealth(siteSlug, hours), SiteHealth.SiteHealthJob.FloorHour(fromUtc), toUtc)));
        return lanes;
    }

    // Area colors for the stacked lane: ISP and local Internet share a hue family, Wi-Fi takes the 5 GHz blue.
    private static readonly (string Name, string Color, Func<SiteHealth.HealthPoint, double> Value)[] HealthAreas =
    [
        ("Internet (ISP)", "#f59e0b", p => p.Isp),
        ("Internet (local)", "#ef5858", p => p.Local),
        ("Wi-Fi", "var(--band-5-color)", p => p.Wifi),
        ("Availability", "#a78bfa", p => p.Availability),
        ("LAN", "#2ba89a", p => p.Lan),
    ];

    /// <summary>Health points lost per bucket, stacked by area; the latest health beside it.</summary>
    internal static TimelineLane HealthLane(SiteHealth.SiteHealthSeries series)
    {
        var lower = series.Points.ToDictionary(p => p.Start, _ => 0.0);
        var stack = new List<StackLayer>();
        foreach (var (name, color, value) in HealthAreas)
        {
            var points = new List<(DateTime, double, double)>();
            foreach (var p in series.Points)
            {
                var lo = lower[p.Start];
                lower[p.Start] = lo + value(p) * 100;
                points.Add((p.Start, lo, lower[p.Start]));
            }
            var latest = series.Points.Count > 0 ? (int)Math.Round(value(series.Points[^1]) * 100) : 0;
            if (points.Any(x => x.Item3 > x.Item2)) stack.Add(new StackLayer(name, color, points, latest));
        }
        // The right column: health now, then what the latest bucket lost, by area in words.
        var last = series.Points.LastOrDefault();
        var losses = last == null ? [] : HealthAreas
            .Select(a => (a.Name, Points: a.Value(last) * 100)).Where(a => a.Points >= 0.05).OrderByDescending(a => a.Points)
            .Select(a => $"{a.Name} {a.Points.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}").ToList();
        return new TimelineLane
        {
            Key = "health",
            Title = "Site Health",
            Subtitle = "Points lost, by area",
            SeriesAName = "now",
            SeriesA = series.Points.Select(p => (p.Start, (double?)(100 - (p.Health ?? 100)))).ToList(),
            Stack = stack,
            LatestA = series.Latest is { } h ? h.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : null,
            LatestB = last == null ? null : losses.Count > 0 ? "lost: " + string.Join(", ", losses) : "nothing lost",
        };
    }

    public async Task<List<SiteHealth.SiteHealthSummary>> GetSiteHealthAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var result = new List<SiteHealth.SiteHealthSummary>();
        foreach (var site in await SitesAsync(null, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            var connection = _registry.Connection(site.Slug);
            var (hours, changes, incidents, plans, gaps) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetHealthHoursAsync(now.AddHours(-48), now, c), await r.GetChangesAsync(now.AddDays(-1), now, 500, c),
                 await ChangeIncidents.StartedAsync(r, now.AddDays(-1), now, c), await r.GetPlansAsync(null, 50, c),
                 await r.GetGapsAsync(now.AddDays(-7), now, c)), cancellationToken);
            hours = _registry.WithProvisionalHealth(site.Slug, hours);
            var counted = changes.Where(x => x.ActorKind != ConfigChangeActorKind.NetworkOptimizer || x.PlanId != null).ToList();
            result.Add(new SiteHealth.SiteHealthSummary
            {
                Slug = site.Slug,
                Name = site.Name,
                Online = connection.IsConnected,
                AgentOffline = connection.IsAwaitingAgent,
                Connection = connection.IsAwaitingAgent ? "Agent offline"
                    : !connection.IsConnected ? "Console unreachable"
                    : connection.IsViaAgent ? "Agent online" : "Console connected",
                UnreachableSince = connection.IsConnected ? null : gaps.Where(g => g.EndedAt == null).Select(g => (DateTime?)g.StartedAt).FirstOrDefault(),
                Health = SiteHealth.SiteHealthSeriesBuilder.Build(hours, SiteHealth.SiteHealthJob.FloorHour(now.AddHours(-24)), now,
                    hours.Where(h => h.HourStart < SiteHealth.SiteHealthJob.FloorHour(now.AddHours(-24))).ToList()),
                Incidents24h = incidents.Count,
                Changes24h = counted.Count,
                Regressions24h = changes.Where(x => x.Kind == ConfigChangeKind.Config).Sum(x => x.RegressionCount),
                PendingApprovals = plans.Count(p => p.Status == ConfigPlanStatus.PendingApproval),
            });
        }
        // Worst first: no data from an unreachable site, then lowest health.
        return result.OrderBy(s => s.Online ? 1 : 0).ThenBy(s => s.Health.Latest ?? s.Health.Average ?? 101).ThenBy(s => s.Name).ToList();
    }

    public async Task<ChangeDetailView?> GetChangeDetailAsync(string siteSlug, int changeId, CancellationToken cancellationToken = default)
    {
        if (!await _access.IsAuthorizedAsync(siteSlug)) return null;
        // Read through the site's own service in a scope pinned to that site; access was checked above.
        await using var scope = _scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        return await scope.ServiceProvider.GetRequiredService<IChangeControlService>().GetChangeDetailAsync(changeId, cancellationToken);
    }
}
