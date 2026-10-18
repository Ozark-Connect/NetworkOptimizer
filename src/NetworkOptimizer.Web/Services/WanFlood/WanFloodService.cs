using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.WanFlood;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services.WanFlood;

/// <summary>A drop or limit as the Threat Intelligence card shows it.</summary>
public sealed record WanFloodDropView(string Wan, string Kind, string Protocol, string? Source, string Destination, int Port,
    DateTime AppliedAt, DateTime? ExpiresAt, long Packets, double Share);

/// <summary>One WAN's protection, as the gateway agent last reported it.</summary>
public sealed record WanFloodWanView(string Wan, bool HooksPresent, bool SmartQueues, bool SmartQueuesRedirect);

/// <summary>What the gateway agent last reported, for the card.</summary>
public sealed record WanFloodStatusView(bool Ok, string? Error, string State, bool Current, bool TableFull,
    int ConntrackCount, int ConntrackMax, IReadOnlyList<WanFloodWanView> Wans, IReadOnlyList<WanFloodDropView> Drops, DateTime ReportedAt);

/// <summary>What the site's gateway agent can do about WAN Flood Protection.</summary>
public enum WanFloodAgentState
{
    /// <summary>No On-Site Agent on the gateway is connected.</summary>
    NoAgent,
    /// <summary>The gateway agent runs it.</summary>
    Capable,
    /// <summary>The gateway agent predates it: updating the agent turns it on.</summary>
    NeedsUpdate,
    /// <summary>The gateway agent knows it, but the gateway lacks what it needs.</summary>
    Unsupported,
}

/// <summary>
/// The site's WAN Flood Protection: its settings, the gateway agent that enforces it (null when none is
/// connected), and that agent's last report.
/// </summary>
public sealed record WanFloodOverview(WanFloodSettings Settings, string? AgentName, bool AgentCapable, WanFloodStatusView? Status)
{
    /// <summary>Each WAN interface's name as the rest of the app shows it ("Fiber (WAN1)"), keyed by interface.</summary>
    public IReadOnlyDictionary<string, string> WanLabels { get; init; } = new Dictionary<string, string>();

    /// <summary>What the gateway agent can do about it, for the update prompt.</summary>
    public WanFloodAgentState AgentState { get; init; } = WanFloodAgentState.NoAgent;

    /// <summary>Each WAN interface's WAN number (WAN1 = 1), for ordering.</summary>
    public IReadOnlyDictionary<string, int> WanOrder { get; init; } = new Dictionary<string, int>();

    /// <summary>
    /// WAN interfaces of the gateway's active WANs (a link or an address). The card shows only these; empty when the
    /// WAN list could not be read, and then it shows every WAN the agent reports.
    /// </summary>
    public IReadOnlySet<string> ActiveWans { get; init; } = new HashSet<string>();

    /// <summary>The WAN's display name, or the interface when it is not a known WAN.</summary>
    public string WanLabel(string wan) => WanLabels.TryGetValue(wan, out var label) ? label : wan;

    /// <summary>Whether the card shows a WAN.</summary>
    public bool ShowsWan(string wan) => ActiveWans.Count == 0 || ActiveWans.Contains(wan);

    /// <summary>Sort key: WAN number, then name, so WAN1 comes before WAN2.</summary>
    public int WanRank(string wan) => WanOrder.TryGetValue(wan, out var n) ? n : int.MaxValue;
}

/// <summary>
/// Runs WAN Flood Protection for every site: builds each site's config from its settings and WANs, sends it
/// to the On-Site Agent on the gateway, and turns what the agent reports into history, alerts, and audit
/// entries. The agent decides and acts on its own; this only configures it and records what it did.
/// </summary>
public sealed class WanFloodService : BackgroundService
{
    private static readonly TimeSpan CycleEvery = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ResendEvery = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ReleaseHeldFor = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly AgentTunnelRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IScheduleSiteContext _siteContext;
    private readonly IAlertEventBus _alerts;
    private readonly IServiceProvider _services;
    private readonly ILogger<WanFloodService> _logger;
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly ConcurrentDictionary<string, WanFloodConfig> _desired = new();
    private readonly ConcurrentDictionary<int, (string Fingerprint, DateTime At)> _sent = new();
    private readonly ConcurrentDictionary<int, (WanFloodStatus Status, DateTime At)> _status = new();
    private readonly ConcurrentDictionary<string, List<(WanFloodRelease Release, DateTime At)>> _releases = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _userReleased = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _audited = new();
    private readonly ConcurrentDictionary<int, int> _hooksMissing = new();
    private readonly ConcurrentDictionary<string, DateTime> _unenforcedAlertedAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _noAgentSince = new();
    private DateTime _lastPrune = DateTime.MinValue;

    public WanFloodService(AgentTunnelRegistry registry, IServiceScopeFactory scopeFactory, IScheduleSiteContext siteContext,
        IAlertEventBus alerts, IServiceProvider services, ILogger<WanFloodService> logger)
    {
        _registry = registry;
        _scopeFactory = scopeFactory;
        _siteContext = siteContext;
        _alerts = alerts;
        _services = services;
        _logger = logger;
    }

    private static string? Site(string slug) => slug == SiteManagementService.DefaultSiteSlug ? null : slug;

    private List<AgentTunnelConnection> Capable(string slug) =>
        _registry.GetForSite(slug).Where(c => c.OnGateway == true && c.HasCapability(WanFloodRules.Capability) && !c.IsStale).ToList();

    private IServiceScope ScopeFor(string slug)
    {
        var scope = _scopeFactory.CreateScope();
        _siteContext.PinScope(scope, slug);
        return scope;
    }

    /// <summary>Rebuilds and sends a site's config now (after a settings save or a release).</summary>
    public void Trigger()
    {
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "WAN flood protection cycle failed");
            }
            try
            {
                await _wake.WaitAsync(CycleEvery, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var prune = DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(6);
        foreach (var slug in await _siteContext.GetSiteKeysAsync(ct))
        {
            using var scope = ScopeFor(slug);
            var config = await BuildConfigAsync(scope.ServiceProvider, slug, ct);
            _desired[slug] = config;
            Push(slug, config);
            if (config.Enabled) AlertIfUnenforced(slug);
            if (prune)
                await scope.ServiceProvider.GetRequiredService<IWanFloodIncidentRepository>().PruneAsync(DateTime.UtcNow - Retention, ct);
        }
        if (prune) _lastPrune = DateTime.UtcNow;
    }

    private async Task<WanFloodConfig> BuildConfigAsync(IServiceProvider services, string slug, CancellationToken ct)
    {
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        var settings = await WanFloodSettings.LoadAsync(k => accessor.GetSettingAsync(k, ct), Site(slug));
        var config = new WanFloodConfig { Enabled = settings.Mode != WanFloodMode.Off };
        if (config.Enabled)
        {
            var db = services.GetRequiredService<NetworkOptimizerDbContext>();
            var profiles = await db.WanProfiles.AsNoTracking()
                .Where(p => p.DataPathInterface != null && p.DataPathInterface != "")
                .Select(p => new { Ifname = p.DataPathInterface!, p.WanNetworkgroup })
                .ToListAsync(ct);
            // Only the gateway's active WANs (a link or an address). A disabled WAN keeps its profile but has nothing
            // to protect. When the WAN list cannot be read, every profiled WAN stays covered.
            var active = (await services.GetRequiredService<Monitoring.MonitoringPathView>().GetWansAsync(ct))
                .Where(w => w.IsActive).Select(w => w.WanInterface).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var wans = profiles
                .Where(p => active.Count == 0 || active.Contains(NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(p.WanNetworkgroup)))
                .Select(p => p.Ifname)
                .ToList();
            config.Mode = settings.Mode == WanFloodMode.Enforce ? "enforce" : "detect";
            config.WanIfnames.AddRange(wans.Where(WanFloodRules.IsValidInterface).Distinct(StringComparer.OrdinalIgnoreCase));
            config.UdpNewPerSource = settings.UdpPerSource;
            config.UdpNewBurst = settings.UdpBurst;
            config.SynNewPerSource = settings.SynPerSource;
            config.SynNewBurst = settings.SynBurst;
            config.ForwardedPortNew = settings.ForwardedPort;
            config.ForwardedPortBurst = settings.ForwardedBurst;
            config.ForwardedPortCapped = settings.ForwardedCapped;
            config.ReactiveDrop = settings.ReactiveDrop;
            config.DropTtlSeconds = settings.DropTtlSeconds;
            config.MaxDropTtlSeconds = settings.MaxDropTtlSeconds;
            config.NeverDrop.AddRange(settings.NeverDropEntries());
            config.TestDrop = settings.TestDrop;
            config.Thresholds = new WanFloodThresholds();
            if (_releases.TryGetValue(slug, out var releases))
            {
                lock (releases)
                {
                    releases.RemoveAll(r => DateTime.UtcNow - r.At > ReleaseHeldFor);
                    config.Releases.AddRange(releases.Select(r => r.Release));
                }
            }
        }
        config.Fingerprint = Convert.ToHexString(SHA256.HashData(config.ToByteArray()))[..16];
        return config;
    }

    private void Push(string slug, WanFloodConfig config)
    {
        var now = DateTime.UtcNow;
        foreach (var agent in Capable(slug))
        {
            var applied = _status.TryGetValue(agent.AgentId, out var s) && s.Status.Fingerprint == config.Fingerprint;
            var sent = _sent.TryGetValue(agent.AgentId, out var last);
            if (applied && sent && last.Fingerprint == config.Fingerprint && now - last.At < ResendEvery) continue;
            if (!applied && sent && last.Fingerprint == config.Fingerprint && now - last.At < TimeSpan.FromSeconds(55)) continue;
            Send(agent, config, now);
        }
    }

    private void Send(AgentTunnelConnection agent, WanFloodConfig config, DateTime now)
    {
        if (_registry.TrySend(agent.AgentId, new ServerMessage { WanFloodConfig = config }))
            _sent[agent.AgentId] = (config.Fingerprint, now);
    }

    // ---- Tunnel events ----

    /// <summary>Sends a newly connected gateway agent its site's config, if there is one yet.</summary>
    public void OnAgentConnected(AgentTunnelConnection connection)
    {
        _sent.TryRemove(connection.AgentId, out _);
        _status.TryRemove(connection.AgentId, out _);
        _hooksMissing.TryRemove(connection.AgentId, out _);
        if (connection.OnGateway != true || !connection.HasCapability(WanFloodRules.Capability)) return;
        if (_desired.TryGetValue(connection.SiteSlug, out var config))
            Send(connection, config, DateTime.UtcNow);
        else
            Trigger();
    }

    public void OnAgentDisconnected(AgentTunnelConnection connection)
    {
        _sent.TryRemove(connection.AgentId, out _);
        _status.TryRemove(connection.AgentId, out _);
        _hooksMissing.TryRemove(connection.AgentId, out _);
    }

    /// <summary>Records an agent's report, and audits and alerts on what changed since its last one.</summary>
    public void OnStatus(AgentTunnelConnection connection, WanFloodStatus status)
    {
        if (!Accepts(connection)) return;
        var slug = connection.SiteSlug;
        var site = Site(slug);
        var previous = _status.TryGetValue(connection.AgentId, out var p) ? p.Status : null;
        _status[connection.AgentId] = (status, DateTime.UtcNow);
        var audited = _audited.GetOrAdd(slug, _ => new HashSet<string>());
        var released = _userReleased.GetOrAdd(slug, _ => new HashSet<string>());

        if (!status.Ok)
            _logger.LogWarning("WAN flood protection on agent {Agent} ({Site}) failed: {Error}", connection.AgentName, slug, status.Error);

        var current = status.Drops.Where(d => d.Kind != "tc-test" && !d.WouldDrop).ToList();
        lock (audited)
        {
            // Keys only matter while their drop can still be reported; a long-lived instance sheds old ones.
            if (audited.Count > 5000)
            {
                audited.Clear();
                foreach (var d in current) audited.Add(Key(d));
            }
            if (previous == null)
            {
                // First report on this connection: what it holds was applied before we were listening.
                foreach (var d in current) audited.Add(Key(d));
            }
            else
            {
                foreach (var d in current.Where(d => audited.Add(Key(d))))
                    AuditDrop(AuditActions.WanFloodDropApplied, site, d, "automatic");
                var currentKeys = current.Select(Key).ToHashSet();
                foreach (var gone in previous.Drops.Where(d => d.Kind != "tc-test" && !d.WouldDrop && !currentKeys.Contains(Key(d))))
                {
                    lock (released)
                    {
                        if (released.Remove(ReleaseKey(gone.Wan, gone.Protocol, ReleaseSource(gone.Kind, gone.Source, gone.Destination), gone.Port, gone.Kind))) continue;
                    }
                    AuditDrop(AuditActions.WanFloodDropExpired, site, gone, "expired");
                }
            }
        }

        if (!string.IsNullOrEmpty(status.Reapplied))
        {
            _logger.LogInformation("WAN flood protection on agent {Agent} ({Site}) put back {What} after the gateway removed it",
                connection.AgentName, slug, status.Reapplied);
            Audit(AuditActions.WanFloodRulesReapplied, site, "wan_flood", string.Join(",", status.Wans.Select(w => w.Wan)),
                new { repaired = status.Reapplied });
        }

        if (status.TableFull != (previous?.TableFull ?? false) && previous != null)
        {
            Audit(AuditActions.WanFloodTableFull, site, "wan_flood", string.Join(",", status.Wans.Select(w => w.Wan)),
                new { change = status.TableFull ? "per_source_rules_removed" : "per_source_rules_restored" });
            if (status.TableFull)
                _ = PublishAsync("threats.wan_flood_table_full", AlertSeverity.Warning, site,
                    "WAN flood: a limit is paused",
                    "A flood filled one of the gateway's limit tables, so new inbound connections it had no room for were being dropped. That limit is paused until the flood drains; drops stay on.",
                    new Dictionary<string, string>());
        }

        var hooked = status.Wans.Count == 0 || status.Wans.All(w => w.HooksPresent);
        _hooksMissing[connection.AgentId] = hooked ? 0 : _hooksMissing.GetValueOrDefault(connection.AgentId) + 1;
    }

    /// <summary>Saves an agent's episode report, then alerts on a new one and audits drops not yet audited.</summary>
    public async Task OnIncidentAsync(AgentTunnelConnection connection, AgentProtocol.WanFloodIncident incident, CancellationToken ct)
    {
        if (!Accepts(connection)) return;
        var slug = connection.SiteSlug;
        var site = Site(slug);
        var now = DateTime.UtcNow;
        var actions = incident.Actions.Select(View).ToList();
        var row = new Storage.Models.WanFloodIncident
        {
            IncidentId = incident.IncidentId.Length > 36 ? incident.IncidentId[..36] : incident.IncidentId,
            Wan = incident.Wan.Length > 128 ? incident.Wan[..128] : incident.Wan,
            Kind = incident.Kind,
            StartedAt = FromMs(incident.StartedUnixMs) ?? now,
            EndedAt = FromMs(incident.EndedUnixMs),
            PeakUnsolicitedPps = incident.PeakUnsolicitedPps,
            PeakSoftirqPercent = incident.PeakSoftirqPercent,
            HotCpu = incident.HotCpu,
            PeakConntrackPercent = incident.PeakConntrackPercent,
            SoftnetDropped = incident.SoftnetDropped,
            TableFull = incident.TableFull,
            ActionsJson = JsonSerializer.Serialize(actions),
            Delivery = "agent",
            Mode = incident.Mode,
            UpdatedAt = now,
        };
        bool inserted;
        using (var scope = ScopeFor(slug))
            inserted = await scope.ServiceProvider.GetRequiredService<IWanFloodIncidentRepository>().UpsertAsync(row, ct);

        // A drop that came and went between two status reports is only in the episode's list.
        var audited = _audited.GetOrAdd(slug, _ => new HashSet<string>());
        lock (audited)
        {
            foreach (var d in incident.Actions.Where(d => !d.WouldDrop && audited.Add(Key(d))))
                AuditDrop(AuditActions.WanFloodDropApplied, site, d, "automatic");
        }

        if (inserted && incident.Phase == "start")
        {
            var conntrack = incident.Kind == "conntrack";
            await PublishAsync(conntrack ? "threats.wan_flood_conntrack" : "threats.wan_flood",
                conntrack ? AlertSeverity.Critical : AlertSeverity.Warning, site,
                conntrack ? "WAN flood: conntrack table under pressure" : "WAN flood detected",
                conntrack
                    ? "New connections to a port forward are filling the gateway's connection table. WAN Flood Protection is limiting new connections to the busiest port forwards."
                    : $"The gateway is receiving about {incident.PeakUnsolicitedPps:N0} unsolicited packets per second. "
                      + (incident.Mode == "enforce" ? "WAN Flood Protection is finding and dropping the source." : "WAN Flood Protection is in Detect mode and will only report what it would drop."),
                new Dictionary<string, string>
                {
                    ["incident_id"] = incident.IncidentId,
                    ["unsolicited_pps"] = incident.PeakUnsolicitedPps.ToString(),
                    ["mode"] = incident.Mode,
                }, ct);
        }
    }

    private static bool Accepts(AgentTunnelConnection connection) =>
        connection.OnGateway == true && connection.HasCapability(WanFloodRules.Capability);

    // ---- What the admin service reads and does ----

    /// <summary>The site's gateway agent and its last report.</summary>
    public (string? AgentName, bool Capable, WanFloodStatusView? Status) Describe(string slug)
    {
        var agent = Capable(slug).FirstOrDefault();
        if (agent == null) return (null, false, null);
        if (!_status.TryGetValue(agent.AgentId, out var s)) return (agent.AgentName, true, null);
        return (agent.AgentName, true, StatusView(slug, s.Status, s.At));
    }

    /// <summary>
    /// What the site's gateway agent can do about WAN Flood Protection. Keyed off capabilities, never a version, so
    /// <see cref="WanFloodAgentState.NeedsUpdate"/> clears on the first hello from an updated agent.
    /// </summary>
    public WanFloodAgentState AgentState(string slug)
    {
        var onGateway = _registry.GetForSite(slug).Where(c => c.OnGateway == true && !c.IsStale).ToList();
        if (onGateway.Any(c => c.HasCapability(WanFloodRules.Capability))) return WanFloodAgentState.Capable;
        if (onGateway.Count == 0) return WanFloodAgentState.NoAgent;
        return onGateway.Any(c => c.HasCapability(WanFloodRules.UnavailableCapability))
            ? WanFloodAgentState.Unsupported
            : WanFloodAgentState.NeedsUpdate;
    }

    private WanFloodStatusView StatusView(string slug, WanFloodStatus status, DateTime at)
    {
        var desired = _desired.TryGetValue(slug, out var d) ? d.Fingerprint : null;
        return new WanFloodStatusView(
            status.Ok, string.IsNullOrEmpty(status.Error) ? null : status.Error, status.State,
            desired == null || status.Fingerprint == desired, status.TableFull, status.ConntrackCount, status.ConntrackMax,
            status.Wans.Select(w => new WanFloodWanView(w.Wan, w.HooksPresent, w.SmartQueues, w.SqRedirectPresent)).ToList(),
            status.Drops.Select(View).ToList(), at);
    }

    /// <summary>Asks the agent to end a drop or limit now; it is not re-added for one time to live.</summary>
    public void Release(string slug, WanFloodRelease release)
    {
        var list = _releases.GetOrAdd(slug, _ => new());
        lock (list) list.Add((release, DateTime.UtcNow));
        var released = _userReleased.GetOrAdd(slug, _ => new HashSet<string>());
        lock (released) released.Add(ReleaseKey(release.Wan, release.Protocol, release.Source, release.Port, release.Kind));
        Trigger();
    }

    // ---- Helpers ----

    private void AlertIfUnenforced(string slug)
    {
        var agent = Capable(slug).FirstOrDefault();
        var now = DateTime.UtcNow;
        // An agent reconnecting after a server or agent restart is not missing: wait five minutes first.
        if (agent != null) _noAgentSince.TryRemove(slug, out _);
        else if (now - _noAgentSince.GetOrAdd(slug, now) < TimeSpan.FromMinutes(5)) return;
        var missing = agent != null && _hooksMissing.GetValueOrDefault(agent.AgentId) >= 2;
        if (agent != null && !missing) return;
        if (_unenforcedAlertedAt.TryGetValue(slug, out var at) && DateTime.UtcNow - at < TimeSpan.FromHours(6)) return;
        _unenforcedAlertedAt[slug] = DateTime.UtcNow;
        _ = PublishAsync("threats.wan_flood_unenforced", AlertSeverity.Warning, Site(slug),
            "WAN Flood Protection is not enforced",
            agent == null
                ? "WAN Flood Protection is on, but no On-Site Agent on this site's gateway is connected to enforce it."
                : "WAN Flood Protection is on, but its rules are not in place on the gateway.",
            new Dictionary<string, string>());
    }

    private async Task PublishAsync(string eventType, AlertSeverity severity, string? site, string title, string message,
        Dictionary<string, string> context, CancellationToken ct = default)
    {
        try
        {
            await _alerts.PublishAsync(new AlertEvent
            {
                EventType = eventType,
                Source = "threats",
                SiteSlug = site,
                Severity = severity,
                Title = title,
                Message = message,
                SourceUrl = "/threats?tab=blocking#wan-flood",
                Context = context,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WAN flood protection: could not publish {EventType}", eventType);
        }
    }

    private void AuditDrop(string action, string? site, WanFloodDrop d, string reason) =>
        Audit(action, site, "wan_flood_drop", $"{d.Protocol} {(string.IsNullOrEmpty(d.Source) ? "any" : d.Source)}:{d.Port} on {d.Wan}",
            new { kind = d.Kind, reason, share = Math.Round(d.Share, 2), destination = d.Destination, expires = FromMs(d.ExpiresUnixMs) });

    private void Audit(string action, string? site, string targetType, string targetName, object details) =>
        _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
            AuditCategories.Settings, action, targetType: targetType, targetName: targetName, siteSlug: site, details: details));

    private static string Key(WanFloodDrop d) => $"{d.Kind}|{d.Wan}|{d.Protocol}|{d.Source}|{d.Destination}|{d.Port}|{d.AppliedUnixMs}";

    private static string ReleaseKey(string wan, string protocol, string? source, int port, string kind) =>
        $"{kind}|{wan}|{protocol}|{source ?? ""}|{port}";

    /// <summary>What a release names as the source: the internal address for a port-forward limit, none for a port limit.</summary>
    public static string ReleaseSource(string kind, string? source, string destination) => kind switch
    {
        "forward-cap" => destination,
        "port-limit" or "tc-port" => "",
        _ => source ?? "",
    };

    private static WanFloodDropView View(WanFloodDrop d) => new(d.Wan, d.Kind, d.Protocol,
        string.IsNullOrEmpty(d.Source) ? null : d.Source, d.Destination, d.Port,
        FromMs(d.AppliedUnixMs) ?? DateTime.UtcNow, FromMs(d.ExpiresUnixMs), d.Packets, d.Share);

    private static DateTime? FromMs(long ms) => ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : null;
}
