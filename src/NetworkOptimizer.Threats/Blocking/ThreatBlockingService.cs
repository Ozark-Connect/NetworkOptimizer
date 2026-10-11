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
/// its own WAF events and patterns and records them in its own database; then every site not Off
/// reconciles its targets to the active enforced blocks of its sharing group (itself, unless it shares),
/// filtered by its own allowlist and the group's WAN addresses. Dry Run records its own decisions without
/// enforcing them, so there only manual blocks are sent. A
/// target that stops being wanted is emptied once, so nothing Network Optimizer added is left behind.
/// </summary>
public sealed class ThreatBlockingService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Resync = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly TimeSpan ObserveEvery = TimeSpan.FromMinutes(10);
    // Public lists change daily at most where it matters; the console is written no more often.
    private static readonly TimeSpan FeedWriteEvery = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThreatBlockingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly NetworkOptimizer.Core.ISiteWorkGate? _siteWorkGate;
    private readonly IThreatSystemAudit? _audit;
    private readonly PublicBlockListCache? _lists;
    private readonly SemaphoreSlim _trigger = new(0, 1);

    // Keyed by site ("" = default site).
    private readonly ConcurrentDictionary<(string Site, BlockTargetKind Kind), BlockTargetStatus> _status = new();
    // The set last sent to each target, and whether it may still hold entries (a failed sync may have).
    private readonly Dictionary<(string Site, BlockTargetKind Kind), (string Fingerprint, DateTime At, bool Ok, bool NonEmpty)> _sent = new();
    private readonly Dictionary<string, DateTime> _lastRun = new();
    private readonly Dictionary<string, DateTime> _lastPrune = new();
    private readonly Dictionary<string, DateTime> _lastObserved = new();
    // The public list entries last written to each site's gateway, keyed by site.
    private readonly Dictionary<string, (string Fingerprint, string Lists, DateTime At, bool Ok)> _feedsSent = new();
    // The Trusted Sources members last written to each site's console.
    private readonly Dictionary<(string Site, BlockTargetKind Kind), (string Fingerprint, DateTime At, bool Ok)> _trustedSent = new();
    private readonly ConcurrentDictionary<(string Site, BlockTargetKind Kind), BlockTargetStatus> _trustedStatus = new();

    public ThreatBlockingService(
        IServiceScopeFactory scopeFactory,
        ILogger<ThreatBlockingService> logger,
        IHttpClientFactory httpClientFactory,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null,
        NetworkOptimizer.Core.ISiteWorkGate? siteWorkGate = null,
        IThreatSystemAudit? audit = null,
        PublicBlockListCache? lists = null)
    {
        _lists = lists;
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

    /// <summary>The last Trusted Sources sync to a site's console (Gateway) or WAF, or null (null site = default site).</summary>
    public BlockTargetStatus? TrustedSourcesStatus(string? site, BlockTargetKind kind) =>
        _trustedStatus.TryGetValue((Key(site), kind), out var s) ? s : null;

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
                await ObserveAddressesAsync(site, now, ct);
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
            try
            {
                await SyncTrustedSourcesAsync(site, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Trusted Sources failed to sync site {Site}", site ?? "main");
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
            var allowlist = await BuildAllowlistAsync(scope.ServiceProvider, site, settings, now, ct);
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
        IReadOnlyList<string> feeds = [];

        // Off sends nothing. Dry Run sends only Enforced blocks, which there are the manual ones,
        // and no public lists: they are automatic blocking too.
        if (settings.Mode != BlockingMode.Off)
        {
            var sharing = services.GetService<IBlockSharingScope>();
            var peers = sharing == null ? [site] : await sharing.GetPeerSitesAsync(site, ct);
            var union = new List<ThreatBlock>();
            foreach (var peer in peers)
            {
                // A peer switched Off neither contributes nor enforces; its old blocks stay with it.
                if (peer != site && (await BlockingSettings.LoadAsync(k => accessor.GetSettingAsync(k, ct), peer)).Mode == BlockingMode.Off)
                    continue;
                using var peerScope = peer == site ? null : ScopeFor(peer);
                var repo = (peerScope?.ServiceProvider ?? services).GetRequiredService<IThreatRepository>();
                union.AddRange(await repo.GetBlocksAsync(now, now, ct));
            }
            // This site's own allowlist governs what it enforces, whoever raised the block.
            var allowlist = await BuildAllowlistAsync(services, site, settings, now, ct, peers);
            desired = BlockDecisionEngine.DesiredSet(union.Where(b => allowlist.Exempts(b.SourceIp) == null), now, settings.MaxEntries);
            if (settings.Mode == BlockingMode.Enforce)
                feeds = await FeedEntriesAsync(accessor, settings, allowlist, now, ct);
            else if (_lists != null)
            {
                // Dry Run downloads the chosen lists without sending them, so Settings shows their
                // counts and the first Enforce has them at once. The cache keeps each list's interval.
                foreach (var id in settings.Feeds)
                    await _lists.GetAsync(id, accessor, now, ct);
            }
        }

        await ReconcileTargetsAsync(site, services, accessor, settings, desired, feeds, now, ct);
        await ReconcileGatewayFeedsAsync(site, services, settings, feeds, now, ct);
    }

    /// <summary>
    /// Everything a site never blocks: Cloudflare, its Trusted Sources (its own and its chosen sites'
    /// addresses, plus its extra addresses), its sharing group's addresses, and its trusted filters.
    /// </summary>
    public async Task<BlockAllowlist> BuildAllowlistAsync(IServiceProvider services, string? site, BlockingSettings settings,
        DateTime now, CancellationToken ct, IEnumerable<string?>? peers = null)
    {
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        var sharing = services.GetService<IBlockSharingScope>();
        var trusted = await TrustedSourcesSettings.ResolveSitesAsync(accessor, sharing, site, ct);
        peers ??= sharing == null ? [site] : await sharing.GetPeerSitesAsync(site, ct);
        var addresses = new List<string>();
        foreach (var s in trusted.Concat(peers).Distinct())
            addresses.AddRange((await TrustedAddressBook.LoadAsync(accessor, s, ct)).Select(a => a.Address));
        var cloudflare = _lists == null ? null : await _lists.GetCloudflareRangesAsync(accessor, now, ct);
        var filters = await services.GetRequiredService<IThreatRepository>().GetNoiseFiltersAsync(ct);
        var extras = await accessor.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Extras, site), ct);
        return new BlockAllowlist($"{settings.Allowlist}\n{extras}", filters, addresses, cloudflare);
    }

    // The enabled public lists' entries, minus anything overlapping protected space.
    private async Task<IReadOnlyList<string>> FeedEntriesAsync(IThreatSettingsAccessor accessor, BlockingSettings settings,
        BlockAllowlist allowlist, DateTime now, CancellationToken ct)
    {
        if (_lists == null || settings.Feeds.Count == 0) return [];
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in settings.Feeds)
        {
            foreach (var entry in await _lists.GetAsync(id, accessor, now, ct))
            {
                if (allowlist.ExemptsRange(entry) == null) entries.Add(entry);
            }
        }
        return entries.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    // Public lists go to their own gateway groups: on a change of chosen lists at once, otherwise daily.
    private async Task ReconcileGatewayFeedsAsync(string? site, IServiceProvider services, BlockingSettings settings,
        IReadOnlyList<string> feeds, DateTime now, CancellationToken ct)
    {
        var wanted = settings.Mode == BlockingMode.Enforce && settings.TargetGateway ? feeds : [];
        var lists = wanted.Count == 0 ? "" : string.Join(",", settings.Feeds.Order());
        var fingerprint = string.Join(",", wanted);
        var known = _feedsSent.TryGetValue(Key(site), out var last);
        if (known && last.Ok && last.Fingerprint == fingerprint) return;
        if (!known && wanted.Count == 0) { _feedsSent[Key(site)] = (fingerprint, lists, now, true); return; }
        if (known && last.Ok && last.Lists == lists && now - last.At < FeedWriteEvery) return;
        if (known && !last.Ok && now - last.At < TimeSpan.FromMinutes(30)) return;
        if (services.GetService<IGatewayBlocklistWriter>() is not { } writer) return;

        var result = await writer.SyncFeedsAsync(site, wanted, ct);
        _feedsSent[Key(site)] = (fingerprint, lists, now, result.Ok);
        if (!result.Ok)
            _logger.LogWarning("Automatic Blocking: public lists not written to the gateway on {Site}: {Error}", site ?? "main", result.Error);
        else if (wanted.Count > 0)
            _logger.LogInformation("Automatic Blocking: {Count} public list entries on the gateway of {Site}", result.Count, site ?? "main");
    }

    // Records the site's current addresses; a site that cannot be read keeps the ones it had.
    private async Task ObserveAddressesAsync(string? site, DateTime now, CancellationToken ct)
    {
        if (_lastObserved.TryGetValue(Key(site), out var at) && now - at < ObserveEvery) return;
        _lastObserved[Key(site)] = now;
        using var scope = ScopeFor(site);
        var clients = scope.ServiceProvider.GetService<IUniFiClientAccessor>();
        if (clients == null) return;
        IReadOnlyList<string>? current;
        try
        {
            current = await TrustedAddressBook.ReadSiteAsync(clients, site, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the addresses of {Site}", site ?? "main");
            return;
        }
        if (current == null) return;

        var accessor = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();
        var stored = await TrustedAddressBook.LoadAsync(accessor, site, ct);
        if (TrustedAddressBook.Observe(stored, current, now) is not { } updated) return;
        await TrustedAddressBook.SaveAsync(accessor, site, updated, ct);

        var before = stored.Select(a => a.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = updated.Select(a => a.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = after.Except(before).ToList();
        var dropped = before.Except(after).ToList();
        if (added.Count > 0 || dropped.Count > 0)
        {
            _logger.LogInformation("Trusted Sources: {Site} addresses changed, added {Added}, dropped {Dropped}",
                site ?? "main", string.Join(", ", added), string.Join(", ", dropped));
            _audit?.TrustedAddressesChanged(site, added, dropped);
        }
    }

    // Writes a site's Trusted Sources to its console when the members change, and hourly.
    private async Task SyncTrustedSourcesAsync(string? site, DateTime now, CancellationToken ct)
    {
        using var scope = ScopeFor(site);
        var services = scope.ServiceProvider;
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        var uniFi = await TrustedSourcesSettings.UniFiGroupEnabledAsync(accessor, site, ct);
        var waf = await BlockingSettings.WafConnectedAsync(k => accessor.GetSettingAsync(k, ct), site);
        if (!uniFi && !waf) return;

        // Never sent empty: an empty list would close every route that uses it.
        var members = await TrustedMembersAsync(services, site, ct);
        if (members.Count == 0) return;
        bool Due(BlockTargetKind kind, string fingerprint) =>
            !_trustedSent.TryGetValue((Key(site), kind), out var last) || last.Fingerprint != fingerprint
            || now - last.At >= (last.Ok ? Resync : TimeSpan.FromMinutes(5));

        // Cloudflare's ranges go to the UniFi groups only, by choice: a port forward to a proxied
        // host sees Cloudflare as the source. Never to the WAF, which sees the visitor behind it.
        if (uniFi && services.GetService<TrustedSourcesGroup>() is { } writer)
        {
            var groupMembers = await TrustedSourcesSettings.UniFiIncludesCloudflareAsync(accessor, site, ct) && _lists != null
                ? [.. members, .. await _lists.GetCloudflareRangesAsync(accessor, now, ct)]
                : members;
            var groupFingerprint = string.Join(",", groupMembers);
            if (Due(BlockTargetKind.Gateway, groupFingerprint))
                Record(BlockTargetKind.Gateway, groupFingerprint, await writer.SyncAsync(site, groupMembers, ct));
        }
        var fingerprint = string.Join(",", members);

        // The WAF's "trusted" list, for routes that use /check?allow=trusted. Pushed whatever the
        // blocking mode: it is an access list the operator opted routes into, not a block.
        if (waf && Due(BlockTargetKind.Waf, fingerprint))
        {
            var url = await accessor.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, site), ct);
            var token = await accessor.GetDecryptedSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Token, site), ct);
            var http = services.GetService<ISiteHttpClientProvider>() is { } provider
                ? await provider.GetClientAsync(site, ct) : _httpClientFactory.CreateClient("Waf");
            BlockTargetResult result;
            try
            {
                result = BlockTargetResult.Success(await new WafClient(http).PutAllowListAsync(url!, token!, TrustedSourcesKeys.WafListName, members, ct));
            }
            catch (WafClientException ex)
            {
                result = BlockTargetResult.Failure(ex.Message);
            }
            Record(BlockTargetKind.Waf, fingerprint, result);
        }

        void Record(BlockTargetKind kind, string sent, BlockTargetResult result)
        {
            _trustedSent[(Key(site), kind)] = (sent, now, result.Ok);
            _trustedStatus[(Key(site), kind)] = new BlockTargetStatus(kind, now, result.Ok, result.Count, result.Error);
            if (!result.Ok)
                _logger.LogWarning("Trusted Sources: {Target} sync failed on {Site}: {Error}", kind, site ?? "main", result.Error);
        }
    }

    /// <summary>A site's Trusted Sources: every address of the sites it trusts, plus its extra addresses.</summary>
    public static async Task<List<string>> TrustedMembersAsync(IServiceProvider services, string? site, CancellationToken ct)
    {
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        var trusted = await TrustedSourcesSettings.ResolveSitesAsync(accessor, services.GetService<IBlockSharingScope>(), site, ct);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in trusted)
            foreach (var a in await TrustedAddressBook.LoadAsync(accessor, s, ct))
                members.Add(a.Address);
        var extras = await accessor.GetSettingAsync(BlockingSettingKeys.ForSite(TrustedSourcesKeys.Extras, site), ct);
        foreach (var e in BlockAllowlist.ParseList(extras))
        {
            if (PublicBlockLists.Normalize(e) is { } n) members.Add(n);
        }
        return members.Order(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task ReconcileTargetsAsync(string? site, IServiceProvider services, IThreatSettingsAccessor settingsAccessor,
        BlockingSettings settings, List<ThreatBlock> desired, IReadOnlyList<string> feeds, DateTime now, CancellationToken ct)
    {
        var enabled = settings.Mode != BlockingMode.Off ? EnabledTargets(settings, site) : [];
        var fingerprint = string.Join(",", desired.Select(b => b.SourceIp).Order(StringComparer.OrdinalIgnoreCase));

        foreach (var kind in Enum.GetValues<BlockTargetKind>())
        {
            var key = (Key(site), kind);
            var known = _sent.TryGetValue(key, out var last);
            var target = await CreateTargetAsync(kind, site, services, settingsAccessor, ct);

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

            if (target is IFeedBlockTarget feedTarget) feedTarget.Feeds = feeds;
            var result = await target.SyncAsync(desired, ct);
            _sent[key] = (fingerprint, now, result.Ok, !result.Ok || desired.Count > 0);
            _status[key] = new BlockTargetStatus(kind, now, result.Ok, result.Count, result.Error);
            if (!result.Ok)
                _logger.LogWarning("Automatic Blocking: {Target} sync failed on {Site}: {Error}", kind, site ?? "main", result.Error);
        }
    }

    /// <summary>The targets a site's settings turn on.</summary>
    public static List<BlockTargetKind> EnabledTargets(BlockingSettings s, string? site)
    {
        var list = new List<BlockTargetKind>();
        if (s.TargetGateway) list.Add(BlockTargetKind.Gateway);
        if (s.TargetWaf) list.Add(BlockTargetKind.Waf);
        if (s.TargetCloudflare) list.Add(BlockTargetKind.Cloudflare);
        return list;
    }

    private async Task<IBlockTarget?> CreateTargetAsync(BlockTargetKind kind, string? site, IServiceProvider services,
        IThreatSettingsAccessor settings, CancellationToken ct) => kind switch
        {
            BlockTargetKind.Waf => new WafBlockTarget(new WafClient(services.GetService<ISiteHttpClientProvider>() is { } http
                ? await http.GetClientAsync(site, ct) : _httpClientFactory.CreateClient("Waf")), settings, site),
            BlockTargetKind.Cloudflare => new CloudflareBlockTarget(new CloudflareListClient(_httpClientFactory.CreateClient("Cloudflare")), settings, site),
            BlockTargetKind.Gateway => services.GetService<IGatewayBlocklistWriter>() is { } writer ? new GatewayBlockTarget(writer, site) : null,
            _ => null
        };
}
