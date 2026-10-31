using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Core;

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
public sealed class ThreatBlockingService : BackgroundService, IPublicListLookup
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Resync = TimeSpan.FromHours(1);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    // Site addresses are read every 2 minutes: one cached stat/device call per site.
    private static readonly TimeSpan ObserveEvery = TimeSpan.FromMinutes(2);
    // The public lists' Network Lists are rewritten only when their entries change, and no more often than this.
    private static readonly TimeSpan FeedWriteEvery = TimeSpan.FromMinutes(15);
    // How long a gateway agent that stopped confirming the set still holds back the console writes.
    private static readonly TimeSpan AgentGrace = TimeSpan.FromMinutes(5);
    // What a site's public lists may add up to, within what a UniFi Network List and the tunnel to a
    // gateway agent hold; lists past the cap are cut, least useful first.
    internal const int MaxFeedEntries = 100_000;
    internal const int MaxOutboundFeedEntries = 50_000;
    // What a UniFi Network List may hold: the gateway refuses a larger set ("exceeds maximum allowed size")
    // and fails the whole config apply with it. Shared by the site's own blocks and its public lists.
    public const int MaxConsoleEntries = 10_000;
    private readonly ConcurrentDictionary<string, int> _feedsCapped = new();
    private readonly ConcurrentDictionary<string, int> _consoleCapped = new();
    // Public list entries cut around each site's latency targets at its last cycle, for the log.
    private readonly ConcurrentDictionary<string, int> _feedsCut = new();

    /// <summary>Public list entries the caps left out on a site at its last cycle (null site = default site).</summary>
    public int FeedEntriesCapped(string? site) => _feedsCapped.TryGetValue(Key(site), out var n) ? n : 0;

    /// <summary>Public list entries the gateway agent holds that UniFi Network's Network Lists leave out (null site = default site).</summary>
    public int FeedEntriesNotInConsole(string? site) => _consoleCapped.TryGetValue(Key(site), out var n) ? n : 0;

    // How long an enforcing gateway agent may be disconnected (a restart) before the console takes over.
    private static readonly TimeSpan AgentDisconnectGrace = TimeSpan.FromMinutes(2);
    // Public list entries on a gateway agent outlive a lost server by this much; they are re-sent well within it.
    private static readonly TimeSpan AgentFeedTtl = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThreatBlockingService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly NetworkOptimizer.Core.ISiteWorkGate? _siteWorkGate;
    private readonly IThreatSystemAudit? _audit;
    private readonly PublicBlockListCache? _lists;
    private readonly IGatewayAgentBlocklist? _agents;
    private readonly SemaphoreSlim _trigger = new(0, 1);
    // When each site's gateway agent last held the current set with its rules in place.
    private readonly ConcurrentDictionary<string, DateTime> _agentEnforcingAt = new();
    // A release or settings change after a console write sends the next one at once: every site's, or
    // one site's when only that site's set can have changed.
    private DateTime _consoleUrgentAt = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _consoleUrgentSiteAt = new();
    // The first cycle's time: a console not written since stands as written then.
    private DateTime _startedAt = DateTime.MinValue;

    // Keyed by site ("" = default site).
    private readonly ConcurrentDictionary<(string Site, BlockTargetKind Kind), BlockTargetStatus> _status = new();
    // The set last sent to each target, and whether it may still hold entries (a failed sync may have).
    private readonly Dictionary<(string Site, BlockTargetKind Kind), (string Fingerprint, DateTime At, bool Ok, bool NonEmpty)> _sent = new();
    private readonly Dictionary<string, DateTime> _lastRun = new();
    private readonly Dictionary<string, DateTime> _lastPrune = new();
    private readonly Dictionary<string, DateTime> _lastObserved = new();
    // The public list entries last written to each site's gateway, keyed by site.
    private readonly Dictionary<string, (string Fingerprint, string Lists, DateTime At, bool Ok)> _feedsSent = new();
    // Entries on each site's gateway after its last successful public list write, for the UI.
    private readonly ConcurrentDictionary<string, int> _feedEntries = new();
    // When each site's blocklist policies were last checked for new-connections-only matching, and whether it worked.
    private readonly Dictionary<string, (DateTime At, bool Ok)> _policiesChecked = new();
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
        PublicBlockListCache? lists = null,
        IGatewayAgentBlocklist? agents = null)
    {
        _lists = lists;
        _agents = agents;
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

    /// <summary>
    /// Runs a cycle now and writes every site's UniFi Console in it, including sites whose gateway
    /// agent holds the set and would otherwise wait for the daily write: after a release or a
    /// settings change, the console must stop blocking what the agent stopped blocking.
    /// </summary>
    public void TriggerConsoleSync()
    {
        _consoleUrgentAt = DateTime.UtcNow;
        Trigger();
    }

    /// <summary>
    /// As <see cref="TriggerConsoleSync"/>, for one site (null = default site): for a change that can only
    /// alter that site's own set, such as its Trusted Sources. A release or a blocking settings change
    /// reaches the sites sharing the site's blocks, so those use <see cref="TriggerConsoleSync"/>.
    /// </summary>
    public void TriggerConsoleSyncFor(string? site)
    {
        _consoleUrgentSiteAt[Key(site)] = DateTime.UtcNow;
        Trigger();
    }

    /// <summary>The last sync to each of a site's targets since this process started (null = default site).</summary>
    public IReadOnlyList<BlockTargetStatus> TargetStatus(string? site) =>
        _status.Where(kv => kv.Key.Site == Key(site)).Select(kv => kv.Value).OrderBy(s => s.Kind).ToList();

    /// <summary>The last Trusted Sources sync to a site's console (Gateway) or WAF, or null (null site = default site).</summary>
    public BlockTargetStatus? TrustedSourcesStatus(string? site, BlockTargetKind kind) =>
        _trustedStatus.TryGetValue((Key(site), kind), out var s) ? s : null;

    /// <summary>
    /// Public list entries on a site's gateway after its last successful write since this process
    /// started, or null before the first one (null site = default site).
    /// </summary>
    public int? GatewayFeedEntries(string? site) =>
        _feedEntries.TryGetValue(Key(site), out var n) ? n : null;

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
        if (_startedAt == DateTime.MinValue) _startedAt = now;
        var sites = await SitesAsync(ct);
        var settingsBySite = new Dictionary<string, BlockingSettings>();
        // Filter on ct, not on OperationCanceledException: a Console's HttpClient timeout is one, and
        // it must fail only its own site.
        foreach (var site in sites)
        {
            try
            {
                await ObserveAddressesAsync(site, now, ct);
                settingsBySite[Key(site)] = await DecideAsync(site, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Automatic Blocking failed to decide for site {Site}", site ?? SiteSlugs.Default);
            }
        }
        foreach (var site in sites)
        {
            if (!settingsBySite.TryGetValue(Key(site), out var settings)) continue;
            try
            {
                await ReconcileAsync(site, settings, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Automatic Blocking failed to sync site {Site}", site ?? SiteSlugs.Default);
            }
            try
            {
                await SyncTrustedSourcesAsync(site, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Trusted Sources failed to sync site {Site}", site ?? SiteSlugs.Default);
            }
            try
            {
                await SyncWafExclusionsAsync(site, now, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "WAF exclusions failed to sync site {Site}", site ?? SiteSlugs.Default);
            }
        }
    }

    private readonly ConcurrentDictionary<string, (string Fingerprint, DateTime At, bool Ok)> _exclusionsSent = new();

    // Re-sends a site's managed exclusions to its WAF on change and hourly, so a replaced WAF gets
    // them back. A site that never had any is skipped, which keeps an older WAF from logging a failure.
    private async Task SyncWafExclusionsAsync(string? site, DateTime now, CancellationToken ct)
    {
        using var scope = ScopeFor(site);
        var services = scope.ServiceProvider;
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        if (!await BlockingSettings.WafConnectedAsync(k => accessor.GetSettingAsync(k, ct), site)) return;

        var list = await WafRuleExclusions.LoadAsync(accessor, site, ct);
        var fingerprint = WafRuleExclusions.Fingerprint(list);
        var sent = _exclusionsSent.TryGetValue(Key(site), out var last);
        if (!sent && list.Count == 0) return;
        if (sent && last.Fingerprint == fingerprint && now - last.At < (last.Ok ? Resync : TimeSpan.FromMinutes(5))) return;

        var url = await accessor.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, site), ct);
        var token = await accessor.GetDecryptedSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Token, site), ct);
        var http = services.GetService<ISiteHttpClientProvider>() is { } provider
            ? await provider.GetClientAsync(site, ct) : _httpClientFactory.CreateClient("Waf");
        try
        {
            await new WafClient(http).PutExclusionsAsync(url!, token!, list, ct);
            _exclusionsSent[Key(site)] = (fingerprint, now, true);
        }
        catch (WafClientException ex)
        {
            _exclusionsSent[Key(site)] = (fingerprint, now, false);
            _logger.LogWarning("WAF exclusions: sync failed on {Site}: {Error}", site ?? SiteSlugs.Default, ex.Message);
        }
    }

    /// <summary>Records a push the caller already made, so the next sync does not repeat it.</summary>
    public void RecordExclusionsSent(string? site, IEnumerable<WafRuleExclusion> list) =>
        _exclusionsSent[Key(site)] = (WafRuleExclusions.Fingerprint(list), DateTime.UtcNow, true);

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
            // In Blocking mode a Detection Only record is not a block: a source still attacking gets a real one.
            var active = current.Where(b => b.IsActive(now) && (b.Enforced || settings.Mode != BlockingMode.Enforce))
                .Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var strikes = await repository.GetBlockCountsBySourceAsync(now - BlockDecisionEngine.StrikeWindow, ct);

            var decided = BlockDecisionEngine.Decide(settings, now, counts, patterns, active, strikes, allowlist);
            if (decided.Count > 0)
            {
                await repository.AddBlocksAsync(decided, ct);
                _logger.LogInformation("Automatic Blocking ({Mode}) on {Site}: {Count} new block(s): {Ips}",
                    settings.Mode, site ?? SiteSlugs.Default, decided.Count, string.Join(", ", decided.Select(b => b.SourceIp)));
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
        foreach (var e in (await BlockingExceptions.LoadAsync(accessor, site, ct)).Where(e => e.ExpiresAt > lastRun && e.ExpiresAt <= now))
            _audit?.ExceptionExpired(site, e.Id, BlockingExceptions.Describe(e));
        _lastRun[Key(site)] = now;

        if (!_lastPrune.TryGetValue(Key(site), out var pruned) || now - pruned > TimeSpan.FromDays(1))
        {
            _lastPrune[Key(site)] = now;
            var count = await repository.PruneBlocksAsync(now - Retention, ct);
            if (count > 0) _logger.LogInformation("Automatic Blocking: pruned {Count} ended block(s) on {Site}", count, site ?? SiteSlugs.Default);
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
        IReadOnlyList<string> feedsOut = [];
        IReadOnlyList<string> consoleFeeds = [];
        IReadOnlyList<string> consoleFeedsOut = [];

        // Off sends nothing. Dry Run sends only manual blocks and no public lists: automatic blocks
        // enforced before the switch to Dry Run stay Enforced in the table, so Enforced alone is not enough.
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
                var blocks = await repo.GetBlocksAsync(now, now, ct);
                // A peer's manual reason names its user, and the reason reaches this site's Cloudflare list.
                union.AddRange(peer == site ? blocks : blocks.Select(b => b.ForOtherSites()));
            }
            // This site's own allowlist governs what it enforces, whoever raised the block.
            var allowlist = await BuildAllowlistAsync(services, site, settings, now, ct, peers);
            desired = BlockDecisionEngine.DesiredSet(union.Where(b => allowlist.Exempts(b.SourceIp) == null
                && (settings.Mode == BlockingMode.Enforce || b.Trigger == BlockTrigger.Manual)), now, settings.MaxEntries);
            // Dry Run downloads the chosen lists without sending them, so Settings shows their counts,
            // devices reaching them are flagged, and the first Enforce has them at once.
            // The site's own blocks come first in what UniFi Network's Network Lists hold.
            var pool = services.GetService<IBlockListPool>() is { } pools ? await pools.GetPoolAsync(site, ct) : "";
            var probed = services.GetService<IProbedAddressProvider>() is { } targets ? await targets.GetAsync(ct) : [];
            var set = await FeedEntriesAsync(site, pool, accessor, settings, allowlist, probed, MaxConsoleEntries - desired.Count, now, ct);
            _listMatchers[Key(site)] = set.Matcher;
            if (settings.Mode == BlockingMode.Enforce)
            {
                feeds = set.Entries;
                feedsOut = set.Outbound;
                consoleFeeds = set.ConsoleEntries;
                consoleFeedsOut = set.ConsoleOutbound;
            }
        }
        else
        {
            _listMatchers.TryRemove(Key(site), out _);
        }

        // Exceptions exist only against the public lists, which only Blocking mode enforces.
        var exceptions = settings.Mode == BlockingMode.Enforce
            ? BlockingExceptions.Active(await BlockingExceptions.LoadAsync(accessor, site, ct), now)
            : [];
        PushToGatewayAgent(site, settings, desired, feeds, feedsOut, exceptions, now);
        await ReconcileTargetsAsync(site, services, accessor, settings, desired, feeds, now, ct);
        await EnsureZonePoliciesAsync(site, services, settings, now, ct);
        await ReconcileGatewayFeedsAsync(site, services, settings, consoleFeeds, consoleFeedsOut, now, ct);
        await ReconcileGatewayExceptionsAsync(site, services, settings, exceptions, now, ct);
        await EnsureSourcePoliciesNewOnlyAsync(site, services, now, ct);
    }

    // The gateway target's fast path: the site's gateway agent holds the same blocks and public list
    // entries the console groups do, applied in seconds and without a reprovision.
    private void PushToGatewayAgent(string? site, BlockingSettings settings, List<ThreatBlock> desired,
        IReadOnlyList<string> feeds, IReadOnlyList<string> feedsOut, IReadOnlyList<BlockingException> exceptions, DateTime now)
    {
        if (_agents == null) return;
        var enabled = settings.Mode != BlockingMode.Off && settings.TargetGateway;
        var entries = new List<GatewayAgentEntry>();
        var feedEntries = new List<GatewayAgentEntry>();
        if (enabled)
        {
            entries.AddRange(desired.Select(b => new GatewayAgentEntry(b.SourceIp, b.ExpiresAt)));
            var own = desired.Select(b => b.SourceIp).ToHashSet(StringComparer.OrdinalIgnoreCase);
            feedEntries.AddRange(feeds.Where(f => !own.Contains(f)).Select(f => new GatewayAgentEntry(f, now + AgentFeedTtl)));
        }
        // Outbound only for the outbound public lists, and only where the site blocks them outbound.
        var outbound = enabled && settings.FeedsOutbound
            ? feedsOut.Select(f => new GatewayAgentEntry(f, now + AgentFeedTtl)).ToList()
            : [];
        var sentExceptions = enabled ? exceptions : [];
        var fingerprint = AgentFingerprint(enabled, desired, feeds, outbound.Count > 0, outbound.Select(o => o.Address), sentExceptions);
        var connected = _agents.Push(site, enabled, entries, outbound, fingerprint, feedEntries, sentExceptions);
        var held = entries.Count + feedEntries.Count;
        // An agent that was enforcing and dropped off is most often restarting. Its rules stay on the gateway
        // for an hour after it stops, so the console waits out a short disconnect instead of reprovisioning.
        if (!connected && enabled && _agentEnforcingAt.TryGetValue(Key(site), out var lastEnforcing)
            && now - lastEnforcing <= AgentDisconnectGrace)
            return;
        if (!connected || !enabled)
        {
            _agentEnforcingAt.TryRemove(Key(site), out _);
            _status.TryRemove((Key(site), BlockTargetKind.GatewayAgent), out _);
            return;
        }
        // A connected gateway agent is trusted for AgentGrace when first seen: after a restart nothing
        // is confirmed yet, and writing every site's console then would reprovision every gateway.
        if (_agents.IsEnforcing(site, fingerprint) || !_agentEnforcingAt.ContainsKey(Key(site)))
            _agentEnforcingAt[Key(site)] = now;

        // Reported once the console has taken over, not while a fresh push awaits its ack.
        var key = (Key(site), BlockTargetKind.GatewayAgent);
        if (now - _agentEnforcingAt[Key(site)] > AgentGrace && _agents.Problem(site, fingerprint) is { } problem)
            _status[key] = new BlockTargetStatus(BlockTargetKind.GatewayAgent, now, false, 0, $"{problem}. Updating UniFi Network instead.");
        else
            _status[key] = new BlockTargetStatus(BlockTargetKind.GatewayAgent, now, true, held, null);
    }

    /// <summary>Whether the site has a connected gateway On-Site Agent that can hold its blocklist (null site = default site).</summary>
    public bool GatewayAgentPresent(string? site) => _agents?.HasGatewayAgent(site) == true;

    /// <summary>Whether the site's gateway On-Site Agents, if any, enforce exceptions (null site = default site).</summary>
    public bool GatewayAgentEnforcesExceptions(string? site) => _agents?.EnforcesExceptions(site) != false;

    /// <summary>Whether the site's gateway On-Site Agent currently holds its blocklist (null site = default site).</summary>
    public bool GatewayAgentEnforcing(string? site) =>
        _status.TryGetValue((Key(site), BlockTargetKind.GatewayAgent), out var s) && s.Ok;

    // Blocks count with their expiry (a re-ban extends one); public list entries without it, since
    // theirs is renewed on every send.
    internal static string AgentFingerprint(bool enabled, IEnumerable<ThreatBlock> blocks, IEnumerable<string> feeds, bool outbound = false,
        IEnumerable<string>? outboundEntries = null, IEnumerable<BlockingException>? exceptions = null)
    {
        var text = new System.Text.StringBuilder(enabled ? "on|" : "off|").Append(outbound ? "out|" : "in|");
        foreach (var b in blocks.OrderBy(b => b.SourceIp, StringComparer.OrdinalIgnoreCase))
            text.Append(b.SourceIp.ToLowerInvariant()).Append('@').Append(b.ExpiresAt.ToString("yyyyMMddHHmm")).Append(',');
        text.Append('|');
        foreach (var f in feeds.Order(StringComparer.OrdinalIgnoreCase))
            text.Append(f.ToLowerInvariant()).Append(',');
        text.Append('|');
        foreach (var f in (outboundEntries ?? []).Order(StringComparer.OrdinalIgnoreCase))
            text.Append(f.ToLowerInvariant()).Append(',');
        text.Append('|').Append(BlockingExceptions.Fingerprint(exceptions ?? []));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    /// <summary>
    /// Whether a console write that is due waits instead. While the site's gateway agent enforces the
    /// set, the console is only the backstop: written once after each daily quiet hour, or at once after
    /// a release or settings change. An agent that stops confirming lets writes through after
    /// <see cref="AgentGrace"/>, and a failed write (the console was unreachable) is retried as usual.
    /// Nothing written since this process started counts as written at start.
    /// </summary>
    private bool ConsoleHeld(string? site, BlockingSettings settings, DateTime? lastWrite, bool lastOk, DateTime now)
    {
        if (!_agentEnforcingAt.TryGetValue(Key(site), out var at) || now - at > AgentGrace) return false;
        if (!lastOk) return false;
        var written = lastWrite ?? _startedAt;
        if (_consoleUrgentAt > written) return false;
        if (_consoleUrgentSiteAt.TryGetValue(Key(site), out var siteUrgent) && siteUrgent > written) return false;
        return written >= LastConsoleWindow(settings.ConsoleSyncHour, now);
    }

    /// <summary>The most recent start of the daily console window at or before <paramref name="nowUtc"/>, in UTC.</summary>
    internal static DateTime LastConsoleWindow(int hour, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var window = local.Date.AddHours(hour);
        if (window > local) window = window.AddDays(-1);
        // A window inside a spring-forward gap starts when the clock jumps past it.
        while (zone.IsInvalidTime(window)) window = window.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(window, DateTimeKind.Unspecified), zone);
    }

    // Consoles set up before new-connections-only matching, or policies edited back in UniFi Network,
    // drop replies too. Checked hourly per site (one policy read); a failed check retries after 30 minutes.
    private async Task EnsureSourcePoliciesNewOnlyAsync(string? site, IServiceProvider services, DateTime now, CancellationToken ct)
    {
        if (_policiesChecked.TryGetValue(Key(site), out var last)
            && now - last.At < (last.Ok ? TimeSpan.FromHours(1) : TimeSpan.FromMinutes(30))) return;
        if (services.GetService<IGatewayBlocklistWriter>() is not { } writer) return;
        try
        {
            var changed = await writer.EnsureSourcePoliciesNewOnlyAsync(site, ct);
            _policiesChecked[Key(site)] = (now, true);
            if (changed > 0)
                _audit?.GatewayPoliciesNarrowed(site, changed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _policiesChecked[Key(site)] = (now, false);
            _logger.LogWarning(ex, "Automatic Blocking: could not narrow the gateway blocklist policies on {Site}", site ?? SiteSlugs.Default);
        }
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

    // The enabled public lists' entries, minus anything overlapping protected space and with the site's
    // probed addresses cut out. Every list blocks inbound; only Outbound lists are blocked outbound and
    // matched for contact alerts.
    private async Task<FeedSet> FeedEntriesAsync(string? site, string pool, IThreatSettingsAccessor accessor,
        BlockingSettings settings, BlockAllowlist allowlist, IReadOnlyList<System.Net.IPAddress> probed, int consoleBudget,
        DateTime now, CancellationToken ct)
    {
        if (_lists == null || settings.Feeds.Count == 0)
        {
            _consoleCapped[Key(site)] = 0;
            return FeedSet.Empty;
        }
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outbound = new Dictionary<string, PublicBlockList>(StringComparer.OrdinalIgnoreCase);
        var perList = new List<(PublicBlockList List, List<string> Entries)>();
        var dropped = 0;
        var cut = 0;
        // Outbound lists first, so an entry on several lists is credited to the one that alerts; then by
        // usefulness, so the caps cut the least useful lists.
        foreach (var list in PublicBlockLists.All.Where(l => settings.Feeds.Contains(l.Id)).OrderBy(l => l.Outbound ? 0 : 1))
        {
            var token = list.NeedsToken
                ? await accessor.GetDecryptedSettingAsync(BlockingSettingKeys.ForSite(BlockingSettingKeys.FeedToken(list.Id), site), ct)
                : null;
            var kept = new List<string>();
            perList.Add((list, kept));
            foreach (var listed in await _lists.GetAsync(list.Id, accessor, now, ct, token, pool))
            {
                if (allowlist.ExemptsRange(listed) != null) continue;
                var pieces = CutAroundProbed(listed, probed);
                if (pieces.Count != 1 || pieces[0] != listed) cut++;
                foreach (var entry in pieces)
                {
                    if (all.Contains(entry)) continue;
                    if (all.Count >= MaxFeedEntries)
                    {
                        dropped++;
                        continue;
                    }
                    all.Add(entry);
                    kept.Add(entry);
                    // Past the outbound cap an entry is still blocked inbound; it only stops being blocked outbound.
                    if (list.Outbound && outbound.Count < MaxOutboundFeedEntries) outbound.TryAdd(entry, list);
                }
            }
        }
        if (dropped > 0 && dropped != _feedsCapped.GetValueOrDefault(Key(site)))
            _logger.LogWarning("Automatic Blocking: public block lists on {Site} capped at {Max:N0} entries; {Dropped:N0} left out",
                site ?? SiteSlugs.Default, MaxFeedEntries, dropped);
        _feedsCapped[Key(site)] = dropped;
        if (cut > 0 && cut != _feedsCut.GetValueOrDefault(Key(site)))
            _logger.LogInformation("Automatic Blocking: {Cut} public list entries on {Site} cut around its latency targets",
                cut, site ?? SiteSlugs.Default);
        _feedsCut[Key(site)] = cut;

        // UniFi Network's Network List gets a share of every list, within what the gateway accepts after the
        // site's own blocks; the gateway agent holds them all.
        var share = ConsoleShare(perList.Select(p => p.Entries.Count).ToList(), consoleBudget);
        var console = perList.SelectMany((p, i) => p.Entries.Take(share[i])).ToList();
        var consoleOut = console.Where(outbound.ContainsKey).ToList();
        _consoleCapped[Key(site)] = all.Count - console.Count;
        return new FeedSet(new PublicListMatcher(outbound.Select(kv => (kv.Key, kv.Value))),
            all.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            outbound.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            console.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            consoleOut.Order(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// A public list entry with the site's probed addresses cut out: the entry itself when it holds none,
    /// nothing when it is one, otherwise the prefixes covering the rest of it.
    /// </summary>
    internal static IReadOnlyList<string> CutAroundProbed(string entry, IReadOnlyList<System.Net.IPAddress> probed)
    {
        if (probed.Count == 0) return [entry];
        System.Net.IPNetwork range;
        if (System.Net.IPAddress.TryParse(entry, out var single))
            range = new System.Net.IPNetwork(single, single.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? 128 : 32);
        else if (!System.Net.IPNetwork.TryParse(entry, out range))
            return [entry];
        if (!probed.Any(range.Contains)) return [entry];
        return NetworkOptimizer.Core.Helpers.BlocklistEntry.CutOut(range, probed)
            .Select(p => PublicBlockLists.Normalize(p.ToString())!)
            .ToList();
    }

    /// <summary>
    /// How many of each list's entries fit in <paramref name="budget"/>, shared evenly: a list smaller than
    /// its share keeps all of it, and what it leaves goes to the larger lists. Each list keeps its first entries.
    /// </summary>
    public static int[] ConsoleShare(IReadOnlyList<int> sizes, int budget)
    {
        var share = new int[sizes.Count];
        var left = Math.Max(0, budget);
        var open = Enumerable.Range(0, sizes.Count).OrderBy(i => sizes[i]).ToList();
        while (open.Count > 0)
        {
            var even = left / open.Count;
            var i = open[0];
            if (sizes[i] <= even)
            {
                share[i] = sizes[i];
                left -= share[i];
                open.RemoveAt(0);
                continue;
            }
            // Every list left is bigger than an even share: split what is left, the remainder to the first.
            var extra = left - even * open.Count;
            foreach (var j in open) share[j] = even + (extra-- > 0 ? 1 : 0);
            break;
        }
        return share;
    }

    /// <summary>
    /// A site's public list entries: all of them (inbound) and those also blocked outbound, for the gateway
    /// agent; and the shares of both that fit UniFi Network's Network Lists.
    /// </summary>
    private sealed record FeedSet(PublicListMatcher Matcher, IReadOnlyList<string> Entries, IReadOnlyList<string> Outbound,
        IReadOnlyList<string> ConsoleEntries, IReadOnlyList<string> ConsoleOutbound)
    {
        public static readonly FeedSet Empty = new(PublicListMatcher.Empty, [], [], [], []);
    }

    private readonly ConcurrentDictionary<string, PublicListMatcher> _listMatchers = new();

    /// <inheritdoc />
    public PublicListMatcher For(string? site) =>
        _listMatchers.TryGetValue(Key(site), out var matcher) ? matcher : PublicListMatcher.Empty;

    // Public lists go to their own Network Lists: on a change of chosen lists at once, otherwise when entries
    // change, at most every FeedWriteEvery.
    private async Task ReconcileGatewayFeedsAsync(string? site, IServiceProvider services, BlockingSettings settings,
        IReadOnlyList<string> feeds, IReadOnlyList<string> feedsOut, DateTime now, CancellationToken ct)
    {
        var wanted = settings.Mode == BlockingMode.Enforce && settings.TargetGateway ? feeds : [];
        // Part of the fingerprint, so turning outbound blocking on fills its groups on the next write.
        var wantedOut = wanted.Count > 0 && settings.FeedsOutbound ? feedsOut : [];
        var lists = wanted.Count == 0 ? "" : string.Join(",", settings.Feeds.Order());
        var fingerprint = string.Join(",", wanted) + "|" + string.Join(",", wantedOut);
        var known = _feedsSent.TryGetValue(Key(site), out var last);
        if (known && last.Ok && last.Fingerprint == fingerprint) return;
        if (!known && wanted.Count == 0) { _feedsSent[Key(site)] = (fingerprint, lists, now, true); _feedEntries[Key(site)] = 0; return; }
        if (wanted.Count > 0 && ConsoleHeld(site, settings, known ? last.At : null, !known || last.Ok, now)) return;
        if (known && last.Ok && last.Lists == lists && now - last.At < FeedWriteEvery) return;
        if (known && !last.Ok && now - last.At < TimeSpan.FromMinutes(30)) return;
        if (services.GetService<IGatewayBlocklistWriter>() is not { } writer) return;

        var result = await writer.SyncFeedsAsync(site, wanted, wantedOut, ct);
        _feedsSent[Key(site)] = (fingerprint, lists, now, result.Ok);
        if (result.Ok)
            _feedEntries[Key(site)] = result.Count;
        if (!result.Ok)
            _logger.LogWarning("Automatic Blocking: public lists not written to the gateway on {Site}: {Error}", site ?? SiteSlugs.Default, result.Error);
        else if (wanted.Count > 0)
            _logger.LogInformation("Automatic Blocking: {Count} public list entries on the gateway of {Site}", result.Count, site ?? SiteSlugs.Default);
    }

    private readonly ConcurrentDictionary<string, DateTime> _zonesCheckedAt = new();
    // The console sync request each site's last zone check answered, so one request makes one check.
    private readonly ConcurrentDictionary<string, DateTime> _zonesRequestSeen = new();
    private static readonly TimeSpan ZoneCheckEvery = TimeSpan.FromMinutes(5);

    // The per-zone Block policies follow the site's zones, wherever a zone came from (UniFi Network, Set Up Zones, a
    // Roll Back): checked every few minutes, and at once on a console sync request (Change Control sends one once the
    // gateway settles after a zone change). A rewrite re-sends the exceptions, whose Allow policies are per zone too.
    private async Task EnsureZonePoliciesAsync(string? site, IServiceProvider services, BlockingSettings settings, DateTime now, CancellationToken ct)
    {
        if (!settings.TargetGateway) return;
        var key = Key(site);
        var last = _zonesCheckedAt.GetValueOrDefault(key, DateTime.MinValue);
        var request = _consoleUrgentSiteAt.TryGetValue(key, out var siteUrgent) && siteUrgent > _consoleUrgentAt ? siteUrgent : _consoleUrgentAt;
        var requested = request > _zonesRequestSeen.GetValueOrDefault(key, DateTime.MinValue);
        if (!requested && now - last < ZoneCheckEvery) return;
        if (services.GetService<IGatewayBlocklistWriter>() is not { } writer) return;
        _zonesCheckedAt[key] = now;
        _zonesRequestSeen[key] = request;
        try
        {
            if (await writer.EnsureZonePoliciesAsync(site, ct))
                _exceptionsWritten.TryRemove(key, out _);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Automatic Blocking: per-zone gateway policies not checked on {Site}", site ?? SiteSlugs.Default);
        }
    }

    private readonly ConcurrentDictionary<string, (string Fingerprint, DateTime At, bool Ok)> _exceptionsWritten = new();
    private readonly ConcurrentDictionary<string, BlockTargetStatus> _exceptionsStatus = new();

    /// <summary>
    /// Forgets the last exception write and runs a cycle, so a console set up again (or cleared) gets the
    /// site's exception policies now rather than at the next hourly check (null site = default site).
    /// </summary>
    public void ResyncExceptions(string? site)
    {
        _exceptionsWritten.TryRemove(Key(site), out _);
        TriggerConsoleSyncFor(site);
    }

    /// <summary>The last write of a site's exception policies to its console, or null (null site = default site).</summary>
    public BlockTargetStatus? ExceptionsConsoleStatus(string? site) =>
        _exceptionsStatus.TryGetValue(Key(site), out var s) ? s : null;

    // The exceptions' Allow policies follow the public lists' Network Lists: written on a change (a save
    // triggers it at once), re-checked hourly, and held like them while a gateway agent enforces.
    private async Task ReconcileGatewayExceptionsAsync(string? site, IServiceProvider services, BlockingSettings settings,
        IReadOnlyList<BlockingException> exceptions, DateTime now, CancellationToken ct)
    {
        var wanted = settings.Mode == BlockingMode.Enforce && settings.TargetGateway ? exceptions : [];
        var fingerprint = BlockingExceptions.Fingerprint(wanted) + (settings.FeedsOutbound ? "|out" : "|in");
        var known = _exceptionsWritten.TryGetValue(Key(site), out var last);
        if (known && last.Ok && last.Fingerprint == fingerprint && now - last.At < Resync) return;
        if (known && !last.Ok && last.Fingerprint == fingerprint && now - last.At < TimeSpan.FromMinutes(30)) return;
        if (wanted.Count > 0 && ConsoleHeld(site, settings, known ? last.At : null, !known || last.Ok, now)) return;
        if (services.GetService<IGatewayBlocklistWriter>() is not { } writer) return;

        var result = await writer.SyncExceptionsAsync(site, wanted, settings.FeedsOutbound, ct);
        _exceptionsWritten[Key(site)] = (fingerprint, now, result.Ok);
        _exceptionsStatus[Key(site)] = new BlockTargetStatus(BlockTargetKind.Gateway, now, result.Ok, result.Count, result.Error);
        if (!result.Ok)
            _logger.LogWarning("Automatic Blocking: exceptions not written to the gateway on {Site}: {Error}", site ?? SiteSlugs.Default, result.Error);
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
            _logger.LogDebug(ex, "Could not read the addresses of {Site}", site ?? SiteSlugs.Default);
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
                site ?? SiteSlugs.Default, string.Join(", ", added), string.Join(", ", dropped));
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

        // Cloudflare's ranges go only to the separate "+ Cloudflare" UniFi groups, by choice: a port
        // forward to a proxied host sees Cloudflare as the source. Never to the WAF, which sees the visitor.
        // A site's own WAN addresses stay out of its own UniFi groups: a port forward there never
        // needs to admit the site itself.
        if (uniFi && services.GetService<TrustedSourcesGroup>() is { } writer
            && await TrustedMembersAsync(services, site, ct, excludeOwn: true) is { Count: > 0 } others)
        {
            var cloudflare = await TrustedSourcesSettings.UniFiIncludesCloudflareAsync(accessor, site, ct) && _lists != null
                ? await _lists.GetCloudflareRangesAsync(accessor, now, ct)
                : null;
            var groupFingerprint = string.Join(",", others) + "|" + string.Join(",", cloudflare ?? []);
            if (Due(BlockTargetKind.Gateway, groupFingerprint))
                Record(BlockTargetKind.Gateway, groupFingerprint, await writer.SyncAsync(site, others, cloudflare, ct));
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
                _logger.LogWarning("Trusted Sources: {Target} sync failed on {Site}: {Error}", kind, site ?? SiteSlugs.Default, result.Error);
        }
    }

    /// <summary>
    /// A site's Trusted Sources: every address of the sites it trusts, plus its extra addresses.
    /// With <paramref name="excludeOwn"/>, the site's own addresses are left out (its UniFi groups),
    /// unless another trusted site or an extra address also holds them.
    /// </summary>
    public static async Task<List<string>> TrustedMembersAsync(IServiceProvider services, string? site, CancellationToken ct,
        bool excludeOwn = false)
    {
        var accessor = services.GetRequiredService<IThreatSettingsAccessor>();
        var trusted = await TrustedSourcesSettings.ResolveSitesAsync(accessor, services.GetService<IBlockSharingScope>(), site, ct);
        var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in trusted.Where(s => !excludeOwn || s != site))
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
            if (kind == BlockTargetKind.GatewayAgent) continue;
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

            // The WAF holds its list in memory with expiries: it is sent when a block, an expiry, or the
            // lists change, when it restarts (its start time is in the fingerprint), and every WafResync,
            // well inside the list entries' lifetime there.
            var isWaf = kind == BlockTargetKind.Waf;
            var targetFingerprint = isWaf ? WafFingerprint(desired, feeds, WafStarted(services, site)) : fingerprint;
            var due = target.SyncEveryCycle || !known || !last.Ok || last.Fingerprint != targetFingerprint
                      || now - last.At > (isWaf ? WafResync : Resync);
            if (!due) continue;
            if (kind == BlockTargetKind.Gateway && ConsoleHeld(site, settings, known ? last.At : null, !known || last.Ok, now)) continue;

            var feedTarget = target as IFeedBlockTarget;
            if (feedTarget != null) feedTarget.Feeds = feeds;
            var result = await target.SyncAsync(desired, ct);
            _sent[key] = (targetFingerprint, now, result.Ok, !result.Ok || desired.Count > 0 || (feedTarget != null && feeds.Count > 0));
            _status[key] = new BlockTargetStatus(kind, now, result.Ok, result.Count, result.Error);
            if (!result.Ok)
                _logger.LogWarning("Automatic Blocking: {Target} sync failed on {Site}: {Error}", kind, site ?? SiteSlugs.Default, result.Error);
        }
    }

    private static readonly TimeSpan WafResync = TimeSpan.FromMinutes(30);

    // Blocks with their expiry (a re-ban extends one), the list entries, and the WAF's start time.
    internal static string WafFingerprint(IEnumerable<ThreatBlock> blocks, IEnumerable<string> feeds, DateTimeOffset? wafStarted)
    {
        var text = new System.Text.StringBuilder(wafStarted?.ToUnixTimeSeconds().ToString() ?? "").Append('|');
        foreach (var b in blocks.OrderBy(b => b.SourceIp, StringComparer.OrdinalIgnoreCase))
            text.Append(b.SourceIp.ToLowerInvariant()).Append('@').Append(b.ExpiresAt.ToString("yyyyMMddHHmm")).Append(',');
        text.Append('|');
        foreach (var f in feeds.Order(StringComparer.OrdinalIgnoreCase))
            text.Append(f.ToLowerInvariant()).Append(',');
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16];
    }

    // When the site's WAF last started, as Threat Intelligence's status poll saw it. Resolved here rather
    // than injected: collection depends on this service, so a constructor dependency would be a cycle.
    private static DateTimeOffset? WafStarted(IServiceProvider services, string? site) =>
        services.GetService<ThreatCollectionService>()?.WafStatusFor(site)?.Started;

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
