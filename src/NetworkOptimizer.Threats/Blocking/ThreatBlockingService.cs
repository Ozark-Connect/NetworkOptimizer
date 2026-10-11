using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;

namespace NetworkOptimizer.Threats.Blocking;

/// <summary>The last sync to one target, for the Settings and Threat Intelligence cards.</summary>
public sealed record BlockTargetStatus(BlockTargetKind Kind, DateTime At, bool Ok, int Count, string? Error);

/// <summary>
/// Automatic Blocking, per site. Once a minute, in two passes: every site decides new blocks from
/// its own WAF events and patterns and records them in its own database; then every site in Enforce
/// reconciles its targets to the active Enforce blocks of its sharing group (itself, unless it shares),
/// filtered by its own allowlist and the group's WAN addresses. Dry Run records and sends nothing. A
/// target that stops being wanted is emptied once, so nothing Network Optimizer added is left behind.
/// </summary>
public sealed class ThreatBlockingService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Resync = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan WanCacheFor = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThreatBlockingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly NetworkOptimizer.Core.ISiteWorkGate? _siteWorkGate;
    private readonly IThreatSystemAudit? _audit;
    private readonly SemaphoreSlim _trigger = new(0, 1);

    // Keyed by site ("" = default site).
    private readonly ConcurrentDictionary<(string Site, BlockTargetKind Kind), BlockTargetStatus> _status = new();
    // The set last sent to each target, and whether it may still hold entries (a failed sync may have).
    private readonly Dictionary<(string Site, BlockTargetKind Kind), (string Fingerprint, DateTime At, bool Ok, bool NonEmpty)> _sent = new();
    private readonly Dictionary<string, DateTime> _lastRun = new();
    private readonly Dictionary<string, DateTime> _lastPrune = new();
    private readonly Dictionary<string, (DateTime At, IReadOnlyList<string> Ips)> _wanCache = new();

    public ThreatBlockingService(
        IServiceScopeFactory scopeFactory,
        ILogger<ThreatBlockingService> logger,
        IHttpClientFactory httpClientFactory,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null,
        NetworkOptimizer.Core.ISiteWorkGate? siteWorkGate = null,
        IThreatSystemAudit? audit = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _siteContext = siteContext;
        _siteWorkGate = siteWorkGate;
        _audit = audit;
    }

    /// <summary>Runs a cycle now, e.g. after a release or a settings change.</summary>
    public void Trigger()
    {
        try { _trigger.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
    }

    /// <summary>The last sync to each of a site's targets since this process started (null = default site).</summary>
    public IReadOnlyList<BlockTargetStatus> TargetStatus(string? site) =>
        _status.Where(kv => kv.Key.Site == Key(site)).Select(kv => kv.Value).OrderBy(s => s.Kind).ToList();

    private static string Key(string? site) => site ?? "";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCycleAsync(DateTime.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Automatic Blocking cycle failed");
            }

            try
            {
                await _trigger.WaitAsync(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<List<string?>> SitesAsync(CancellationToken ct)
    {
        if (_siteContext == null) return [null];
        var sites = new List<string?>();
        foreach (var key in await _siteContext.GetSiteKeysAsync(ct))
        {
            var site = key == _siteContext.DefaultKey ? null : key;
            if (_siteWorkGate?.IsSiteOperational(site) == false) continue;
            sites.Add(site);
        }
        return sites;
    }

    private IServiceScope ScopeFor(string? site)
    {
        var scope = _scopeFactory.CreateScope();
        _siteContext?.PinScope(scope, site ?? _siteContext.DefaultKey);
        return scope;
    }

    internal async Task RunCycleAsync(DateTime now, CancellationToken ct)
    {
        var sites = await SitesAsync(ct);
        var settingsBySite = new Dictionary<string, BlockingSettings>();
        foreach (var site in sites)
        {
            try
            {
                settingsBySite[Key(site)] = await DecideAsync(site, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Automatic Blocking failed to decide for site {Site}", site ?? "main");
            }
        }
        foreach (var site in sites)
        {
            if (!settingsBySite.TryGetValue(Key(site), out var settings)) continue;
            try
            {
                await ReconcileAsync(site, settings, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Automatic Blocking failed to sync site {Site}", site ?? "main");
            }
        }
    }

    // Pass 1: this site's own decisions, expiry audit, and pruning.
    private async Task<BlockingSettings> DecideAsync(string? site, DateTime now, CancellationToken ct)
    {
        using var scope = ScopeFor(site);
        var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
        var accessor = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();
        var settings = await BlockingSettings.LoadAsync(k => accessor.GetSettingAsync(k, ct), site);

        if (settings.Mode != BlockingMode.Off)
        {
            var filters = await repository.GetNoiseFiltersAsync(ct);
            var allowlist = new BlockAllowlist(settings.Allowlist, filters, await WanAddressesAsync(scope.ServiceProvider, [site], now, ct));
            var windowStart = now.AddMinutes(-settings.WindowMinutes);
            var counts = await repository.GetWafEventCountsBySourceAsync(windowStart, now, ct);
            var patterns = settings.UsePatterns ? await repository.GetPatternsSeenAsync(windowStart, now, ct) : [];
            var current = await repository.GetBlocksAsync(now, now, ct);
            var active = current.Where(b => b.IsActive(now)).Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var strikes = await repository.GetBlockCountsBySourceAsync(now - BlockDecisionEngine.StrikeWindow, ct);

            var decided = BlockDecisionEngine.Decide(settings, now, counts, patterns, active, strikes, allowlist);
            if (decided.Count > 0)
            {
                await repository.AddBlocksAsync(decided, ct);
                _logger.LogInformation("Automatic Blocking ({Mode}) on {Site}: {Count} new block(s): {Ips}",
                    settings.Mode, site ?? "main", decided.Count, string.Join(", ", decided.Select(b => b.SourceIp)));
                if (settings.Mode == BlockingMode.Enforce)
                {
                    var targets = EnabledTargets(settings, site).Select(t => t.ToString()).ToList();
                    foreach (var b in decided)
                        _audit?.SourceBlocked(site, b.SourceIp, b.Reason, b.ExpiresAt, targets);
                }
            }
        }

        // Blocks that ran out since the last cycle; only Enforce ones changed anything to audit.
        var lastRun = _lastRun.TryGetValue(Key(site), out var lr) ? lr : now;
        var recent = await repository.GetBlocksAsync(now, lastRun.AddMinutes(-BlockingSettings.MaxBanMinutes - 1), ct);
        foreach (var b in recent.Where(b => b.Enforced && b.ReleasedAt == null && b.ExpiresAt > lastRun && b.ExpiresAt <= now))
            _audit?.BlockExpired(site, b.SourceIp);
        _lastRun[Key(site)] = now;

        if (!_lastPrune.TryGetValue(Key(site), out var pruned) || now - pruned > TimeSpan.FromDays(1))
        {
            _lastPrune[Key(site)] = now;
            var count = await repository.PruneBlocksAsync(now - Retention, ct);
            if (count > 0) _logger.LogInformation("Automatic Blocking: pruned {Count} ended block(s) on {Site}", count, site ?? "main");
        }
        return settings;
    }

    // Pass 2: send this site's targets the active Enforce blocks of its sharing group.
    private async Task ReconcileAsync(string? site, BlockingSettings settings, DateTime now, CancellationToken ct)
    {
        using var scope = ScopeFor(site);
        var services = scope.ServiceProvider;
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        List<ThreatBlock> desired = [];

        if (settings.Mode == BlockingMode.Enforce)
        {
            var sharing = services.GetService<IBlockSharingScope>();
            var peers = sharing == null ? [site] : await sharing.GetPeerSitesAsync(site, ct);
            var union = new List<ThreatBlock>();
            foreach (var peer in peers)
            {
                using var peerScope = peer == site ? null : ScopeFor(peer);
                var repo = (peerScope?.ServiceProvider ?? services).GetRequiredService<IThreatRepository>();
                union.AddRange(await repo.GetBlocksAsync(now, now, ct));
            }
            // This site's own allowlist governs what it enforces, whoever raised the block.
            var filters = await services.GetRequiredService<IThreatRepository>().GetNoiseFiltersAsync(ct);
            var allowlist = new BlockAllowlist(settings.Allowlist, filters, await WanAddressesAsync(services, peers, now, ct));
            desired = BlockDecisionEngine.DesiredSet(union.Where(b => allowlist.Exempts(b.SourceIp) == null), now, settings.MaxEntries);
        }

        await ReconcileTargetsAsync(site, services, accessor, settings, desired, now, ct);
    }

    private async Task<IReadOnlyList<string>> WanAddressesAsync(IServiceProvider services, IEnumerable<string?> sites,
        DateTime now, CancellationToken ct)
    {
        var clients = services.GetService<IUniFiClientAccessor>();
        if (clients == null) return [];
        var all = new List<string>();
        foreach (var site in sites)
        {
            if (!_wanCache.TryGetValue(Key(site), out var cached) || now - cached.At > WanCacheFor)
            {
                try
                {
                    cached = (now, await SiteWanAddresses.GetAsync(clients, site, ct));
                    _wanCache[Key(site)] = cached;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not read WAN addresses for {Site}", site ?? "main");
                    cached = (now, cached.Ips ?? []);
                }
            }
            all.AddRange(cached.Ips);
        }
        return all;
    }

    private async Task ReconcileTargetsAsync(string? site, IServiceProvider services, IThreatSettingsAccessor settingsAccessor,
        BlockingSettings settings, List<ThreatBlock> desired, DateTime now, CancellationToken ct)
    {
        var enabled = settings.Mode == BlockingMode.Enforce ? EnabledTargets(settings, site) : [];
        var fingerprint = string.Join(",", desired.Select(b => b.SourceIp).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var kind in Enum.GetValues<BlockTargetKind>())
        {
            var key = (Key(site), kind);
            var known = _sent.TryGetValue(key, out var last);
            var target = CreateTarget(kind, site, services, settingsAccessor);

            if (!enabled.Contains(kind))
            {
                // Empty a target that is no longer wanted. After a restart nothing is known about
                // what an earlier process sent, so each target gets one cleanup attempt; a target
                // that was never set up fails it, and that failure is not worth reporting.
                if (known && !last.NonEmpty) continue;
                if (target == null)
                {
                    _sent[key] = ("", now, true, false);
                    continue;
                }
                var cleared = await target.SyncAsync([], ct);
                _sent[key] = ("", now, cleared.Ok, !cleared.Ok && known && last.NonEmpty);
                if (known && last.NonEmpty)
                    _status[key] = new BlockTargetStatus(kind, now, cleared.Ok, 0, cleared.Error);
                continue;
            }

            if (target == null)
            {
                _status[key] = new BlockTargetStatus(kind, now, false, 0, "Not available on this site.");
                continue;
            }

            var due = target.SyncEveryCycle || !known || !last.Ok || last.Fingerprint != fingerprint || now - last.At > Resync;
            if (!due) continue;

            var result = await target.SyncAsync(desired, ct);
            _sent[key] = (fingerprint, now, result.Ok, !result.Ok || desired.Count > 0);
            _status[key] = new BlockTargetStatus(kind, now, result.Ok, result.Count, result.Error);
            if (!result.Ok)
                _logger.LogWarning("Automatic Blocking: {Target} sync failed on {Site}: {Error}", kind, site ?? "main", result.Error);
        }
    }

    // The WAF sits beside the default site's proxy, so only the default site can use it.
    private static List<BlockTargetKind> EnabledTargets(BlockingSettings s, string? site)
    {
        var list = new List<BlockTargetKind>();
        if (s.TargetGateway) list.Add(BlockTargetKind.Gateway);
        if (s.TargetWaf && site == null) list.Add(BlockTargetKind.Waf);
        if (s.TargetCloudflare) list.Add(BlockTargetKind.Cloudflare);
        return list;
    }

    private IBlockTarget? CreateTarget(BlockTargetKind kind, string? site, IServiceProvider services, IThreatSettingsAccessor settings) => kind switch
    {
        BlockTargetKind.Waf => site == null ? new WafBlockTarget(new WafClient(_httpClientFactory.CreateClient("Waf")), settings) : null,
        BlockTargetKind.Cloudflare => new CloudflareBlockTarget(new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare")), settings, site),
        BlockTargetKind.Gateway => services.GetService<IGatewayBlocklistWriter>() is { } writer ? new GatewayBlockTarget(writer, site) : null,
        _ => null
    };
}
