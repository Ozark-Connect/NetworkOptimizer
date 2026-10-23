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

    public ChangeControlFleetService(ChangeControlRegistry registry, IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        ISiteAccessFilter access, MonitoringInfluxRegistry influx, IspHealthRegistry isp, IServiceScopeFactory scopes,
        ILogger<ChangeControlFleetService> logger)
    {
        _scopes = scopes;
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
        var (latest, changes, state) = await engine.Repositories.UseAsync(async (r, c) =>
            (await r.GetLatestSnapshotAsync(c), await r.GetChangesAsync(now.AddDays(-7), now, 2000, c), await r.GetDetectionStateAsync(c)), ct);
        var config = changes.Where(x => x.Kind == ConfigChangeKind.Config && x.ActorKind != ConfigChangeActorKind.NetworkOptimizer).ToList();
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
            Slug = site.Slug, Name = site.Name,
            Detail = latest == null ? "No snapshot yet" : $"{latest.ObjectCount} objects",
            Online = connection.IsConnected,
            AgentOffline = connection.IsAwaitingAgent,
            LastSnapshotAt = latest?.TakenAt,
            Changes7d = config.Count,
            Changes24h = config.Count(c => c.OccurredAt > now.AddDays(-1)),
            Regressions = config.Sum(c => c.RegressionCount),
            Unattributed = config.Count(c => c.ActorKind is ConfigChangeActorKind.NoAdminRecorded or ConfigChangeActorKind.LikelyUniFi),
            DriftObjects = drift,
            NetworkAppVersion = state.NetworkAppVersion,
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

            var (changes, plans, gaps) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetChangesAsync(now.AddDays(-30), now, 2000, c),
                 await r.GetPlansAsync(null, 50, c),
                 await r.GetGapsAsync(now.AddDays(-7), now, c)), cancellationToken);

            foreach (var ch in changes.Where(c => c.Kind == ConfigChangeKind.Config && c.RegressionCount > 0 && c.OccurredAt > now.AddDays(-7)).Take(5))
            {
                var upgrade = ch.ActorKind == ConfigChangeActorKind.LikelyUniFi && ch.Summary.Contains("Network app upgrade", StringComparison.Ordinal);
                view.Attention.Add(new AttentionItem
                {
                    Kind = "Regression", SiteSlug = site.Slug, SiteName = site.Name, At = ch.OccurredAt, ChangeId = ch.Id,
                    FromSnapshotId = upgrade ? ch.PreviousSnapshotId : null, ToSnapshotId = upgrade ? ch.SnapshotId : null,
                    Title = upgrade ? $"{ch.RegressionCount} regression{(ch.RegressionCount == 1 ? "" : "s")} from the Network app upgrade" : FirstImpact(ch) ?? ch.Summary,
                    Detail = upgrade ? ch.Summary : $"{ch.Summary}. {ChangeControlService.Who(ch)}.",
                });
            }
            foreach (var p in plans.Where(p => p.Status == ConfigPlanStatus.PendingApproval))
                view.Attention.Add(new AttentionItem
                {
                    Kind = "Approval", SiteSlug = site.Slug, SiteName = site.Name, At = p.SubmittedAt ?? p.CreatedAt, PlanId = p.Id, Title = p.Name,
                    Detail = $"Submitted by {p.SubmittedBy}." + (p.ScheduledFor is { } s ? $" Scheduled {Local(s):MMM d, h:mm tt} once approved." : "")
                             + (p.HighestTier >= PlanTiers.Lockout ? " Possible lockout." : ""),
                });
            foreach (var p in plans.Where(p => p.Status == ConfigPlanStatus.Reverted && p.CompletedAt > now.AddDays(-2)))
                view.Attention.Add(new AttentionItem
                {
                    Kind = "Reverted", SiteSlug = site.Slug, SiteName = site.Name, At = p.CompletedAt ?? now, PlanId = p.Id,
                    Title = $"Plan reverted: {p.Name}", Detail = p.Message ?? "",
                });
            foreach (var g in gaps.Where(g => g.EndedAt == null))
                view.Attention.Add(new AttentionItem
                {
                    Kind = "Gap", SiteSlug = site.Slug, SiteName = site.Name, At = g.StartedAt,
                    Title = g.Reason.Contains("agent", StringComparison.OrdinalIgnoreCase) ? $"Agent offline for {Span(now - g.StartedAt)}" : $"UniFi Console unreachable for {Span(now - g.StartedAt)}",
                    Detail = "Change detection is paused. Network Optimizer catches up from the UniFi audit log when it reconnects.",
                });
            if (siteView.DriftObjects is > 0)
                view.Attention.Add(new AttentionItem
                {
                    Kind = "Drift", SiteSlug = site.Slug, SiteName = site.Name, At = now,
                    Title = $"{siteView.DriftObjects} objects drifted from the Baseline", Detail = "Return to Baseline opens a plan; Accept as Baseline keeps them.",
                });

            view.Plans.AddRange(plans.Where(p => ConfigPlanStatuses.InProgress.Contains(p.Status)).Select(p => ChangeControlService.Summary(p, site.Slug, site.Name)));

            var upgradeRow = changes.Where(c => c.Kind == ConfigChangeKind.NetworkAppUpgrade).OrderByDescending(c => c.OccurredAt).FirstOrDefault();
            if (upgradeRow != null)
            {
                var during = changes.Where(c => c.Kind == ConfigChangeKind.Config && c.OccurredAt >= upgradeRow.OccurredAt
                    && c.OccurredAt <= upgradeRow.OccurredAt + ChangeAttributor.UpgradeWindow && c.ActorKind == ConfigChangeActorKind.LikelyUniFi).ToList();
                view.Upgrades.Add(new UpgradeRow(site.Slug, site.Name, upgradeRow.OccurredAt, upgradeRow.VersionFrom ?? "", upgradeRow.VersionTo ?? "",
                    during.Sum(c => c.ObjectCount), during.Sum(c => c.RegressionCount), during.FirstOrDefault()?.Id));
            }
        }
        var order = new Dictionary<string, int> { ["Reverted"] = 0, ["Regression"] = 1, ["Approval"] = 2, ["Gap"] = 3, ["Drift"] = 4 };
        view.Attention.Sort((a, b) => order[a.Kind] != order[b.Kind] ? order[a.Kind].CompareTo(order[b.Kind]) : b.At.CompareTo(a.At));
        view.Upgrades.Sort((a, b) => b.At.CompareTo(a.At));
        return view;
    }

    private static string? FirstImpact(ConfigChange c)
    {
        if (string.IsNullOrEmpty(c.ImpactJson)) return null;
        var lines = System.Text.Json.JsonSerializer.Deserialize<List<ImpactLine>>(c.ImpactJson);
        return lines?.FirstOrDefault(l => l.Kind == "Regression")?.Title;
    }

    private static DateTime Local(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local);

    private static string Span(TimeSpan t) => t.TotalMinutes < 90 ? $"{(int)t.TotalMinutes} minutes" : t.TotalHours < 48 ? $"{(int)t.TotalHours} hours" : $"{(int)t.TotalDays} days";

    public async Task<List<ChangeRowView>> GetChangesAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default)
    {
        var rows = new List<ChangeRowView>();
        foreach (var site in await SitesAsync(sites, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            var (changes, incidents, settings) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetChangesAsync(fromUtc, toUtc, 2000, c), await r.GetIncidentsStartedAsync(fromUtc, toUtc + ChangeControlSiteEngine.IncidentWindow, c), await r.GetSettingsAsync(c)), cancellationToken);
            rows.AddRange(changes
                .Where(x => settings.ShowOwnChanges || x.ActorKind != ConfigChangeActorKind.NetworkOptimizer || x.PlanId != null)
                .Select(x => ChangeControlService.Row(x, site.Slug, site.Name, x.Kind != ConfigChangeKind.Config ? 0
                    : incidents.Count(i => !ChangeControlService.IsWanIncident(i) && i.FirstTriggeredAt >= x.OccurredAt
                        && i.FirstTriggeredAt - x.OccurredAt <= ChangeControlSiteEngine.IncidentWindow))));
        }
        return rows.OrderByDescending(r => r.OccurredAt).ToList();
    }

    public async Task<List<TimelineSiteRow>> GetTimelineAsync(DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? sites, CancellationToken cancellationToken = default)
    {
        var result = new List<TimelineSiteRow>();
        var changes = await GetChangesAsync(fromUtc, toUtc, sites, cancellationToken);
        foreach (var site in await SitesAsync(sites, cancellationToken))
        {
            var engine = _registry.GetFor(site.Slug);
            var view = await SiteViewAsync(site, withDrift: false, cancellationToken);
            var (gaps, incidents) = await engine.Repositories.UseAsync(async (r, c) =>
                (await r.GetGapsAsync(fromUtc, toUtc, c), await r.GetIncidentsStartedAsync(fromUtc, toUtc, c)), cancellationToken);
            var bands = gaps.Select(g => new TimelineBand(g.StartedAt, g.EndedAt ?? DateTime.UtcNow, "gap", g.Reason))
                .Concat(incidents.Select(i => new TimelineBand(i.FirstTriggeredAt, i.ResolvedAt ?? i.LastTriggeredAt, "incident", i.Title))).ToList();
            var markers = changes.Where(c => c.SiteSlug == site.Slug).ToList();
            List<(DateTime, double?)> sparkline = [];
            try
            {
                var lanes = await Reader(site.Slug).LanesAsync(fromUtc, toUtc, cancellationToken);
                sparkline = lanes.FirstOrDefault(l => l.Key == "latency")?.SeriesB.Select(p => (p.Time, p.Value ?? lanes.First(l => l.Key == "latency").SeriesA.FirstOrDefault(a => a.Time == p.Time).Value)).ToList() ?? [];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Sparkline failed for {Site}", site.Slug);
            }
            result.Add(new TimelineSiteRow
            {
                Site = view, Markers = markers, Bands = bands, Sparkline = sparkline,
                ChangeCount = markers.Count(m => m.Kind == ConfigChangeKind.Config), RegressionCount = markers.Sum(m => m.RegressionCount),
            });
        }
        return result;
    }

    private SiteMetricsReader Reader(string slug) =>
        new(_influx.GetFor(slug), _isp.GetFor(slug, null), _logger);

    public async Task<List<TimelineLane>> GetLanesAsync(string siteSlug, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken = default)
    {
        if (!await _access.IsAuthorizedAsync(siteSlug)) return [];
        return await Reader(siteSlug).LanesAsync(fromUtc, toUtc, cancellationToken);
    }

    public async Task<ChangeDetailView?> GetChangeDetailAsync(string siteSlug, int changeId, CancellationToken cancellationToken = default)
    {
        if (!await _access.IsAuthorizedAsync(siteSlug)) return null;
        // Read through the site's own service in a scope pinned to that site; access was checked above.
        using var scope = _scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(siteSlug);
        using var system = Identity.SystemScope.Enter(scope.ServiceProvider, ChangeControlRepositoryAccessor.SystemActor);
        return await scope.ServiceProvider.GetRequiredService<IChangeControlService>().GetChangeDetailAsync(changeId, cancellationToken);
    }
}