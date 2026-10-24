using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Threats.Analysis;
using NetworkOptimizer.Threats.Enrichment;
using NetworkOptimizer.Threats.Interfaces;
using NetworkOptimizer.Threats.Models;
using NetworkOptimizer.Threats.Waf;

namespace NetworkOptimizer.Threats;

/// <summary>
/// Background service that polls UniFi for IPS/IDS events, normalizes, enriches,
/// and stores them. Also runs pattern analysis and publishes high-severity events to the alert bus.
/// </summary>
public class ThreatCollectionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ThreatCollectionService> _logger;
    private readonly ThreatEventNormalizer _normalizer;
    private readonly GeoEnrichmentService _geoService;
    private readonly KillChainClassifier _classifier;
    private readonly ThreatPatternAnalyzer _patternAnalyzer;
    private readonly IAlertEventBus _alertEventBus;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IUniFiClientAccessor _uniFiClientAccessor;

    // Configurable via SystemSettings (defaults)
    private int _pollIntervalMinutes = 1;
    private int _retentionDays = 90;

    // On-demand trigger: released by TriggerCollection(), waited on during poll sleep
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);
    // Per-site progress state. The collection loop fans out over sites, so these
    // must be keyed by site or the dashboard shows whichever site the loop touched last.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _collectedSites = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset?> _backfillCursorBySite = new();

    // Each site's netopt-waf as of its last poll, keyed like _collectedSites ("" = default site).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WafStatus> _wafStatus = new();
    private readonly ISiteHttpClientProvider? _siteHttp;

    // Geo database staleness check (24h cooldown to avoid checking every cycle)
    private DateTimeOffset _lastGeoCheck = DateTimeOffset.MinValue;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _geoBackfillCompleteSites = new();

    // Track attack chain alerts: key = "chain:{ip}" or "attempt:{ip}", value = "stageCount:totalEvents:utcTicks"
    // Persisted to SystemSettings as JSON so dedup survives restarts.
    // Chain-alert dedup state, kept per originating site ("" = default). Attacker IPs
    // repeat across sites, so this state must never be shared between them.
    private readonly Dictionary<string, Dictionary<string, string>> _chainStateBySite = new();
    private readonly HashSet<string> _chainStateLoadedSites = new();
    // Optional per-site fan-out. Null (unregistered) means single-site behavior.
    private readonly NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? _siteContext;
    private readonly NetworkOptimizer.Core.ISiteWorkGate? _siteWorkGate;
    private readonly IThreatSystemAudit? _systemAudit;
    private readonly IInfrastructureSourceProvider? _infrastructureSources;
    private readonly Blocking.IPublicListLookup? _publicLists;
    // Outbound drops gateway agents reported, saved with each site's next cycle so they run through
    // the same tagging, de-duplication, and alerting as collected flows ("" = default site).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentQueue<ThreatEvent>> _gatewayDrops = new();

    public ThreatCollectionService(
        IServiceScopeFactory scopeFactory,
        ILogger<ThreatCollectionService> logger,
        ThreatEventNormalizer normalizer,
        GeoEnrichmentService geoService,
        KillChainClassifier classifier,
        ThreatPatternAnalyzer patternAnalyzer,
        IAlertEventBus alertEventBus,
        IHttpClientFactory httpClientFactory,
        IUniFiClientAccessor uniFiClientAccessor,
        NetworkOptimizer.Alerts.Interfaces.IScheduleSiteContext? siteContext = null,
        NetworkOptimizer.Core.ISiteWorkGate? siteWorkGate = null,
        IThreatSystemAudit? systemAudit = null,
        ISiteHttpClientProvider? siteHttp = null,
        IInfrastructureSourceProvider? infrastructureSources = null,
        Blocking.IPublicListLookup? publicLists = null)
    {
        _publicLists = publicLists;
        _systemAudit = systemAudit;
        _infrastructureSources = infrastructureSources;
        _siteHttp = siteHttp;
        _siteWorkGate = siteWorkGate;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _normalizer = normalizer;
        _geoService = geoService;
        _classifier = classifier;
        _patternAnalyzer = patternAnalyzer;
        _alertEventBus = alertEventBus;
        _httpClientFactory = httpClientFactory;
        _uniFiClientAccessor = uniFiClientAccessor;
        _siteContext = siteContext;
    }

    private string DefaultSiteKey => _siteContext?.DefaultKey ?? "";

    // Namespaces per-site cursor/state keys stored in the (global) settings DB. The default
    // site keeps the bare key so existing single-site state is preserved exactly.
    private string SiteScopedKey(string baseKey, string? siteKey) =>
        string.IsNullOrEmpty(siteKey) || siteKey == DefaultSiteKey ? baseKey : $"{baseKey}:{siteKey}";

    /// <summary>
    /// Signal the background loop to run a collection cycle immediately.
    /// Safe to call from anywhere (dashboard, API, etc.). No-op if already running.
    /// </summary>
    public void TriggerCollection()
    {
        // TryRelease: if semaphore is already at 1, this is a no-op (avoids SemaphoreFullException)
        try { _triggerSignal.Release(); }
        catch (SemaphoreFullException) { /* already signaled */ }
    }

    /// <summary>
    /// Whether the service has completed at least one collection cycle for the given site.
    /// Pass null (or the default site's slug) for the main site.
    /// </summary>
    public bool HasCollectedOnceFor(string? siteSlug) => _collectedSites.ContainsKey(NormalizeSite(siteSlug));

    /// <summary>
    /// How far back the gradual backfill has reached for the given site. Null if backfill hasn't
    /// started or is complete. The dashboard uses this to show "Data from {date} - present
    /// (building...)" coverage info. Pass null (or the default site's slug) for the main site.
    /// </summary>
    public DateTimeOffset? BackfillCursorFor(string? siteSlug) =>
        _backfillCursorBySite.TryGetValue(NormalizeSite(siteSlug), out var cursor) ? cursor : null;

    /// <summary>
    /// Queues outbound drops a site's gateway agent reported (devices that tried to reach an address on
    /// one of the site's public block lists). They are saved and alerted on with the site's next cycle.
    /// </summary>
    public void QueueGatewayDrops(string? siteSlug, IEnumerable<ThreatEvent> drops)
    {
        var queue = _gatewayDrops.GetOrAdd(NormalizeSite(siteSlug), _ => new());
        foreach (var drop in drops)
        {
            // A device stuck in a retry loop must not grow this without bound between cycles.
            if (queue.Count >= 1000) break;
            queue.Enqueue(drop);
        }
        TriggerCollection();
    }

    /// <summary>The site's public lists as Automatic Blocking last built them.</summary>
    private Blocking.PublicListMatcher ListsFor(string? siteKey)
    {
        var site = NormalizeSite(siteKey);
        return _publicLists?.For(site.Length == 0 ? null : site) ?? Blocking.PublicListMatcher.Empty;
    }

    /// <summary>
    /// The site's netopt-waf as of its last poll, or null when the site has no WAF configured.
    /// </summary>
    public WafStatus? WafStatusFor(string? siteSlug) => _wafStatus.TryGetValue(NormalizeSite(siteSlug), out var status) ? status : null;

    /// <summary>
    /// Collapses the default site's various identifiers (null, empty, and the configured default
    /// key) to one canonical key so the loop's write and the dashboard's read line up.
    /// </summary>
    private string NormalizeSite(string? siteKey) =>
        string.IsNullOrEmpty(siteKey) || siteKey == DefaultSiteKey ? "" : siteKey;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Threat collection service starting");

        // Wait for app startup
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

        // Attempt auto-download of MaxMind databases if configured and missing/stale
        await TryAutoDownloadGeoDatabasesAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var siteKeys = _siteContext == null
                    ? new List<string?> { null }
                    : (await _siteContext.GetSiteKeysAsync(stoppingToken)).Cast<string?>().ToList();
                foreach (var siteKey in siteKeys)
                {
                    // Collection polls the site's console and writes to its database, so a site
                    // licensing has closed does neither.
                    if (_siteWorkGate?.IsSiteOperational(siteKey) == false)
                    {
                        _logger.LogDebug("Skipping threat collection for non-operational site {Site}",
                            siteKey ?? "main");
                        continue;
                    }
                    try
                    {
                        await CollectAndProcessAsync(siteKey, stoppingToken);
                        _collectedSites.TryAdd(NormalizeSite(siteKey), 0);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Threat collection failed for site {Site}", siteKey ?? "main");
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Threat collection cycle failed");
            }

            // Wait for poll interval OR an on-demand trigger, whichever comes first
            try
            {
                await _triggerSignal.WaitAsync(TimeSpan.FromMinutes(_pollIntervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Threat collection service stopped");
    }

    private async Task CollectAndProcessAsync(string? siteKey, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        // Pin the scope to this site so the scoped threat repository reads/writes ITS database.
        // The settings accessor stays instance-wide (main DB); per-site cursors are namespaced.
        _siteContext?.PinScope(scope, siteKey ?? DefaultSiteKey);
        var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();

        await LoadConfigAsync(settings, cancellationToken);

        await EnsureInfrastructureFiltersAsync(siteKey, repository, cancellationToken);

        // Per-site enable flag (bare key for the default site, so single-site installs are
        // unchanged): each site opts in/out of collection independently. Unset defaults to
        // ON, so a new site collects until turned off - matching single-site semantics.
        // Interval and retention (LoadConfigAsync above) stay global: one shared loop.
        var enabled = await settings.GetSettingAsync(SiteScopedKey("threats.enabled", siteKey), cancellationToken);
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Threat collection disabled for site {Site}", siteKey ?? "main");
            return;
        }

        // A site's WAF sits beside its proxy (reached through its agent when routed that way) and does not need the console.
        await CollectWafAsync(repository, settings, siteKey, cancellationToken);

        if (_gatewayDrops.TryGetValue(NormalizeSite(siteKey), out var drops) && !drops.IsEmpty)
        {
            var batch = new List<ThreatEvent>();
            while (drops.TryDequeue(out var drop)) batch.Add(drop);
            await ProcessAndSaveAsync(batch, repository, settings, siteKey, cancellationToken);
        }

        var apiClient = _uniFiClientAccessor.GetClient(siteKey);
        if (apiClient == null)
        {
            _logger.LogDebug("UniFi API client not available for site {Site}, skipping threat collection", siteKey ?? "main");
            return;
        }

        // === PHASE 1: Incremental collection from last sync ===
        // On first run (or after >24h gap), sweep the full 24 hours in 1-hour chunks
        // to avoid hitting the ~1000-result pagination cap. On subsequent runs, only
        // query from last sync (with 2-min overlap for API eventual consistency).
        var now = DateTimeOffset.UtcNow;
        var lastSyncKey = SiteScopedKey("threats.last_sync_timestamp", siteKey);
        var lastSyncStr = await settings.GetSettingAsync(lastSyncKey, cancellationToken);
        DateTimeOffset recentStart;

        if (lastSyncStr != null && DateTimeOffset.TryParse(lastSyncStr, out var lastSync) && lastSync > now.AddHours(-24))
        {
            // Small overlap to catch any events delayed in the API
            recentStart = lastSync.AddMinutes(-2);
        }
        else
        {
            // First run or stale - full 24h sweep
            recentStart = now.AddHours(-24);
        }

        var totalRecentEvents = 0;

        var chunkCursor = recentStart;
        while (chunkCursor < now)
        {
            var chunkEnd = chunkCursor.AddHours(1);
            if (chunkEnd > now) chunkEnd = now;

            var chunkEvents = await CollectRangeAsync(apiClient, siteKey, chunkCursor, chunkEnd, maxPages: int.MaxValue, cancellationToken);
            await ProcessAndSaveAsync(chunkEvents, repository, settings, siteKey, cancellationToken);
            totalRecentEvents += chunkEvents.Count;

            chunkCursor = chunkEnd;
        }

        await settings.SaveSettingAsync(lastSyncKey, now.ToString("O"));

        if (totalRecentEvents > 0)
            _logger.LogDebug("Collected {Count} threat events", totalRecentEvents);

        // === PHASE 2: Gradual backfill (>24h ago) - page-limited to stay gentle ===
        // Backfill 30 days (data retention is separate at _retentionDays)
        const int backfillDays = 30;
        var backfillCursorKey = SiteScopedKey("threats.backfill_cursor", siteKey);
        var backfillCursorStr = await settings.GetSettingAsync(backfillCursorKey, cancellationToken);
        var backfillLimit = DateTimeOffset.UtcNow.AddDays(-backfillDays);

        // Initialize cursor to 24h ago on first run (Phase 1 covers recent 24h)
        var cursor = backfillCursorStr != null ? DateTimeOffset.Parse(backfillCursorStr) : recentStart;

        if (cursor > backfillLimit)
        {
            // Work backwards in 6-hour chunks, 20 pages per cycle
            // When chunks return 0 events, accelerate through sparse periods (up to 48h per cycle)
            var maxChunksPerCycle = 8;
            for (var chunk = 0; chunk < maxChunksPerCycle && cursor > backfillLimit; chunk++)
            {
                var chunkEnd = cursor;
                var chunkStart = cursor.AddHours(-6);
                if (chunkStart < backfillLimit) chunkStart = backfillLimit;

                var backfillEvents = await CollectRangeAsync(apiClient, siteKey, chunkStart, chunkEnd, maxPages: 20, cancellationToken);
                await ProcessAndSaveAsync(backfillEvents, repository, settings, siteKey, cancellationToken);

                cursor = chunkStart;
                await settings.SaveSettingAsync(backfillCursorKey, cursor.ToString("O"));
                _backfillCursorBySite[NormalizeSite(siteKey)] = cursor;

                if (backfillEvents.Count > 0)
                {
                    _logger.LogInformation("Backfill: {Count} events from {From} to {To}", backfillEvents.Count, chunkStart, chunkEnd);
                    break; // Found events, yield to next cycle
                }

                _logger.LogDebug("Backfill: 0 events from {From} to {To}, accelerating", chunkStart, chunkEnd);
            }
        }
        else
        {
            _backfillCursorBySite[NormalizeSite(siteKey)] = null; // Backfill complete
            _logger.LogDebug("Backfill complete - coverage back to {Days}d", backfillDays);
        }

        // Periodic geo database staleness check (every 24h, triggered by dashboard loading)
        if (DateTimeOffset.UtcNow - _lastGeoCheck > TimeSpan.FromHours(24))
        {
            _lastGeoCheck = DateTimeOffset.UtcNow;
            await TryAutoDownloadGeoDatabasesAsync(cancellationToken);
        }

        // Re-enrich existing events that lack geo data (runs each cycle until complete)
        if (_geoService.IsCityAvailable && !_geoBackfillCompleteSites.ContainsKey(NormalizeSite(siteKey)))
        {
            var enriched = await repository.BackfillGeoDataAsync(
                events => _geoService.EnrichEvents(events), batchSize: 2000, cancellationToken);
            if (enriched == 0)
            {
                _geoBackfillCompleteSites.TryAdd(NormalizeSite(siteKey), 0);
                _logger.LogDebug("Geo data backfill complete - all events enriched");
            }
            else
            {
                _logger.LogDebug("Geo backfill: enriched {Count} events with geo data", enriched);
            }
        }

        // Periodic cleanup (3 AM UTC)
        if (DateTime.UtcNow.Hour == 3 && DateTime.UtcNow.Minute < _pollIntervalMinutes)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            await repository.PurgeOldEventsAsync(cutoff, cancellationToken);
            await repository.PurgeCrowdSecCacheAsync(cancellationToken);
            if (scope.ServiceProvider.GetService<CrowdSec.ICtiReputationCache>() is { } ctiCache)
                await ctiCache.PurgeExpiredAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Collect traffic flows and IPS events for a specific time range.
    /// </summary>
    private async Task<List<ThreatEvent>> CollectRangeAsync(
        UniFi.UniFiApiClient apiClient, string? siteKey,
        DateTimeOffset start, DateTimeOffset end,
        int maxPages, CancellationToken cancellationToken)
    {
        var allEvents = new List<ThreatEvent>();

        var flowEvents = await CollectTrafficFlowsAsync(apiClient, ListsFor(siteKey), start, end, maxPages, cancellationToken);
        allEvents.AddRange(flowEvents);

        var ipsEvents = await CollectIpsEventsAsync(apiClient, start, end, cancellationToken);
        allEvents.AddRange(ipsEvents);

        return allEvents;
    }

    /// <summary>
    /// Enrich, classify, save events, run pattern analysis, and publish alerts.
    /// </summary>
    private async Task ProcessAndSaveAsync(List<ThreatEvent> events,
        IThreatRepository repository, IThreatSettingsAccessor settings, string? siteKey, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;

        // Alert events must carry the originating site so the processor evaluates them
        // against that site's rules and delivers to its channels (null = default site).
        var normalizedSite = NormalizeSite(siteKey);
        var alertSiteSlug = normalizedSite.Length == 0 ? null : normalizedSite;

        _geoService.EnrichEvents(events);

        foreach (var evt in events)
            evt.KillChainStage = _classifier.Classify(evt);

        // Devices reaching the site's public block lists. After classification, so the tag's stage holds;
        // drops a gateway agent reported arrive already marked and keep it even if the list has changed.
        var lists = ListsFor(siteKey);
        var contacts = new List<ThreatEvent>();
        foreach (var evt in events)
        {
            var list = Blocking.PublicListContact.IsCandidate(evt) ? lists.Find(evt.DestIp) : null;
            if (list == null && evt.Category != Blocking.PublicListContact.Category) continue;
            Blocking.PublicListContact.Tag(evt, list);
            contacts.Add(evt);
        }

        await repository.SaveEventsAsync(events, cancellationToken);

        // Load noise filters once for all alert checks in this cycle
        List<ThreatNoiseFilter> noiseFilters;
        try
        {
            noiseFilters = (await repository.GetNoiseFiltersAsync(cancellationToken))
                .Where(f => f.Enabled).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load noise filters, proceeding without filtering");
            noiseFilters = [];
        }

        // Pattern and chain queries below see filtered events only, so a filtered source
        // can never assemble an alert from its suppressed traffic.
        repository.SetNoiseFilters(noiseFilters);
        var sources = new AlertSourceDescriber(events, () => _uniFiClientAccessor.GetClient(siteKey));

        // Pattern analysis on recent data
        try
        {
            var recentEvents = await repository.GetEventsAsync(
                DateTime.UtcNow.AddHours(-6), DateTime.UtcNow, limit: 5000, cancellationToken: cancellationToken);
            sources.AddEvents(recentEvents);
            var patterns = _patternAnalyzer.DetectPatterns(recentEvents);
            foreach (var pattern in patterns)
                await repository.SavePatternAsync(pattern, cancellationToken);

            // Publish alert events for patterns with new activity (DB-persisted dedup)
            var unalertedPatterns = await repository.GetUnalertedPatternsAsync(cancellationToken);
            foreach (var pattern in unalertedPatterns)
            {
                try
                {
                    var sourceIps = System.Text.Json.JsonSerializer.Deserialize<List<string>>(pattern.SourceIpsJson) ?? [];
                    var firstSourceIp = sourceIps.FirstOrDefault() ?? "unknown";

                    // Always mark as alerted to prevent re-alerting every cycle,
                    // even if noise-filtered
                    await repository.MarkPatternAlertedAsync(pattern.Id, DateTime.UtcNow, cancellationToken);

                    // Skip publishing if noise-filtered
                    if (noiseFilters.Any(f => f.Matches(firstSourceIp, null, pattern.TargetPort)))
                        continue;

                    var severity = pattern.PatternType switch
                    {
                        Models.PatternType.DDoS => AlertSeverity.Critical,
                        Models.PatternType.BruteForce => AlertSeverity.Error,
                        Models.PatternType.ExploitCampaign => AlertSeverity.Error,
                        _ => AlertSeverity.Warning
                    };

                    await _alertEventBus.PublishAsync(new AlertEvent
                    {
                        EventType = "threats.attack_pattern",
                        Source = "threats",
                        SiteSlug = alertSiteSlug,
                        Severity = severity,
                        Title = pattern.Description,
                        Message = $"{pattern.PatternType} pattern detected: {pattern.EventCount} events, confidence {pattern.Confidence:P0}",
                        DeviceIp = firstSourceIp,
                        SourceUrl = IpDrilldownUrl(sourceIps.FirstOrDefault()),
                        Context = new Dictionary<string, string>
                        {
                            ["pattern_type"] = pattern.PatternType.ToString(),
                            ["event_count"] = pattern.EventCount.ToString(),
                            ["confidence"] = pattern.Confidence.ToString("F2"),
                            ["target_port"] = pattern.TargetPort?.ToString() ?? "",
                            ["source_ips"] = string.Join(", ", sourceIps.Take(5))
                        }
                    }, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to publish alert for pattern {PatternType}", pattern.PatternType);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pattern analysis failed");
        }

        // Check for multi-stage attack chains (2+ kill chain stages from same source)
        // State is persisted to SystemSettings so dedup survives restarts.
        // Only re-alerts when a chain has progressed (more stages or 50%+ more events).
        try
        {
            var chainScope = siteKey ?? "";
            var chainState = _chainStateBySite.TryGetValue(chainScope, out var cs)
                ? cs : (_chainStateBySite[chainScope] = new());
            var chainStateKey = SiteScopedKey("threats.chain_alert_state", siteKey);

            // Load persisted chain alert state from DB on first cycle for this site
            if (_chainStateLoadedSites.Add(chainScope))
            {
                var stateJson = await settings.GetSettingAsync(chainStateKey, cancellationToken);
                if (!string.IsNullOrEmpty(stateJson))
                {
                    try
                    {
                        var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(stateJson);
                        if (loaded != null)
                            foreach (var kv in loaded)
                                chainState[kv.Key] = kv.Value;
                    }
                    catch { /* corrupt state, start fresh */ }
                }
            }

            // Prune entries older than 24h
            var pruneThreshold = DateTime.UtcNow.AddHours(-24).Ticks;
            var staleKeys = chainState
                .Where(kv =>
                {
                    var parts = kv.Value.Split(':');
                    return parts.Length >= 3 && long.TryParse(parts[2], out var t) && t < pruneThreshold;
                })
                .Select(kv => kv.Key)
                .ToList();
            foreach (var key in staleKeys)
                chainState.Remove(key);

            var sequences = await repository.GetAttackSequencesAsync(
                DateTime.UtcNow.AddHours(-6), DateTime.UtcNow, limit: 20, cancellationToken);

            var stateChanged = staleKeys.Count > 0;

            // Alert on chains with 2+ stages ending in ActiveExploitation, PostExploitation, or Monitored
            var alertableEndings = new[] { KillChainStage.ActiveExploitation, KillChainStage.PostExploitation, KillChainStage.Monitored };
            foreach (var seq in sequences)
            {
                if (seq.Stages.Count < 2) continue;

                var lastStage = seq.Stages[^1].Stage;
                if (!alertableEndings.Contains(lastStage)) continue;

                var stateKey = $"chain:{seq.SourceIp}";
                var totalEvents = seq.Stages.Sum(s => s.EventCount);

                // Check if this chain has progressed since last alert
                if (chainState.TryGetValue(stateKey, out var prevValue))
                {
                    var parts = prevValue.Split(':');
                    if (parts.Length >= 2 &&
                        int.TryParse(parts[0], out var prevStages) &&
                        int.TryParse(parts[1], out var prevEvents))
                    {
                        // Full chains are high-risk - re-alert on any progression (more stages or 50%+ events)
                        if (seq.Stages.Count <= prevStages && totalEvents < prevEvents * 1.5)
                            continue;
                    }
                }

                // Skip publishing if noise-filtered (don't advance state so escalation
                // alerts fire when the filter is removed)
                if (noiseFilters.Any(f => f.Matches(seq.SourceIp, null, null)))
                    continue;

                chainState[stateKey] = $"{seq.Stages.Count}:{totalEvents}:{DateTime.UtcNow.Ticks}";
                stateChanged = true;

                var stageNames = string.Join(" -> ", seq.Stages.Select(s => s.Stage.ToDisplayString()));
                var severity = lastStage is KillChainStage.ActiveExploitation or KillChainStage.PostExploitation
                    ? AlertSeverity.Critical : AlertSeverity.Warning;

                // 2-stage chains ending in Monitored are likely normal admin/scanning traffic
                var isLowConfidence = seq.Stages.Count == 2 && lastStage == KillChainStage.Monitored;
                var source = await sources.DescribeAsync(seq.SourceIp, seq.CountryCode, seq.AsnOrg, cancellationToken);
                var message = $"{source.Text} progressed through {seq.Stages.Count} kill chain stages with {totalEvents} events";
                if (isLowConfidence)
                    message += ". Note: 2-stage chains ending in Monitored may be typical administration or scanning traffic rather than a real attack.";

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "threats.attack_chain",
                    Source = "threats",
                    SiteSlug = alertSiteSlug,
                    Severity = severity,
                    Title = $"Attack chain: {stageNames}",
                    Message = message,
                    DeviceIp = seq.SourceIp,
                    SourceUrl = IpDrilldownUrl(seq.SourceIp),
                    Context = source.WithContext(new Dictionary<string, string>
                    {
                        ["stages"] = stageNames,
                        ["stage_count"] = seq.Stages.Count.ToString(),
                        ["total_events"] = totalEvents.ToString()
                    })
                }, cancellationToken);

                _logger.LogInformation("Attack chain detected: {Ip} ({Country}) - {Stages}",
                    seq.SourceIp, seq.CountryCode, stageNames);
            }

            // Second pass: early-stage chains (e.g. Recon -> AttemptedExploitation) that didn't meet
            // the end-state filter above. These are lower confidence but useful for security-minded users.
            foreach (var seq in sequences)
            {
                if (seq.Stages.Count < 2) continue;

                // Skip if already alerted as a full chain
                if (chainState.ContainsKey($"chain:{seq.SourceIp}")) continue;

                var attemptKey = $"attempt:{seq.SourceIp}";
                var totalEvents = seq.Stages.Sum(s => s.EventCount);

                // Check if this attempt chain has progressed since last alert
                if (chainState.TryGetValue(attemptKey, out var prevValue))
                {
                    var parts = prevValue.Split(':');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[0], out var prevStages) &&
                        int.TryParse(parts[1], out var prevEvents) &&
                        long.TryParse(parts[2], out var prevTicks))
                    {
                        var hoursSinceLast = (DateTime.UtcNow.Ticks - prevTicks) / (double)TimeSpan.TicksPerHour;

                        if (seq.Stages.Count <= prevStages &&
                            (hoursSinceLast < 6 || totalEvents < prevEvents * 2))
                            continue;
                    }
                }

                // Skip publishing if noise-filtered (don't advance state so escalation
                // alerts fire when the filter is removed)
                if (noiseFilters.Any(f => f.Matches(seq.SourceIp, null, null)))
                    continue;

                chainState[attemptKey] = $"{seq.Stages.Count}:{totalEvents}:{DateTime.UtcNow.Ticks}";
                stateChanged = true;

                var stageNames = string.Join(" -> ", seq.Stages.Select(s => s.Stage.ToDisplayString()));
                var source = await sources.DescribeAsync(seq.SourceIp, seq.CountryCode, seq.AsnOrg, cancellationToken);

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "threats.attack_chain_attempt",
                    Source = "threats",
                    SiteSlug = alertSiteSlug,
                    Severity = AlertSeverity.Info,
                    Title = $"Early-stage attack chain: {stageNames}",
                    Message = $"{source.Text} progressed through {seq.Stages.Count} early kill chain stages with {totalEvents} events. " +
                              "This may indicate a blocked attack or reconnaissance activity that did not reach exploitation.",
                    DeviceIp = seq.SourceIp,
                    SourceUrl = IpDrilldownUrl(seq.SourceIp),
                    Context = source.WithContext(new Dictionary<string, string>
                    {
                        ["stages"] = stageNames,
                        ["stage_count"] = seq.Stages.Count.ToString(),
                        ["total_events"] = totalEvents.ToString()
                    })
                }, cancellationToken);

                _logger.LogDebug("Early-stage attack chain detected: {Ip} ({Country}) - {Stages}",
                    seq.SourceIp, seq.CountryCode, stageNames);
            }

            // Persist updated state to DB
            if (stateChanged)
            {
                var json = JsonSerializer.Serialize(chainState);
                await settings.SaveSettingAsync(chainStateKey, json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Attack chain detection failed");
        }

        // A CyberSecure (Suricata) event and a public list contact on the same device and address within
        // minutes are one contact: whichever is recorded second raises no alert of its own.
        var window = Blocking.PublicListContact.DuplicateWindow;
        var contactPairs = contacts.Select(c => (c.SourceIp, c.DestIp)).ToHashSet();
        var alertedPairs = new HashSet<(string, string)>();
        foreach (var contact in contacts.GroupBy(c => (c.SourceIp, c.DestIp, c.Action)).Select(g => g.First()))
        {
            if (noiseFilters.Any(f => f.Matches(contact.SourceIp, contact.DestIp, contact.DestPort)))
                continue;
            try
            {
                if (events.Any(e => e.EventSource == Models.EventSource.Ips && e.SourceIp == contact.SourceIp && e.DestIp == contact.DestIp)
                    || await repository.HasEventAsync(contact.SourceIp, contact.DestIp, contact.Timestamp - window, contact.Timestamp + window,
                        Models.EventSource.Ips, cancellationToken: cancellationToken))
                    continue;
                await PublishContactAsync(contact, alertSiteSlug, sources, cancellationToken);
                alertedPairs.Add((contact.SourceIp, contact.DestIp));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish public block list contact alert");
            }
        }

        // Publish high-severity events to alert bus (skip noise-filtered)
        foreach (var evt in events.Where(e => e.Severity >= 4 && e.Category != Blocking.PublicListContact.Category))
        {
            if (noiseFilters.Any(f => f.Matches(evt.SourceIp, evt.DestIp, evt.DestPort)))
                continue;
            // In the same batch the contact deferred to this event; from an earlier one, the contact alerted.
            if (evt.EventSource == Models.EventSource.Ips
                && (contactPairs.Contains((evt.SourceIp, evt.DestIp))
                    ? alertedPairs.Contains((evt.SourceIp, evt.DestIp))
                    : await repository.HasEventAsync(evt.SourceIp, evt.DestIp, evt.Timestamp - window, evt.Timestamp + window,
                        category: Blocking.PublicListContact.Category, cancellationToken: cancellationToken)))
                continue;

            try
            {
                var (eventType, titlePrefix) = evt.EventSource switch
                {
                    Models.EventSource.TrafficFlow => ("threats.traffic_flow", "Flow"),
                    Models.EventSource.Waf => ("threats.waf_event", "WAF"),
                    _ => ("threats.ips_event", "IPS")
                };
                var source = await sources.DescribeAsync(evt.SourceIp, evt.CountryCode, evt.AsnOrg, cancellationToken);

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = eventType,
                    Source = "threats",
                    SiteSlug = alertSiteSlug,
                    Severity = evt.Severity >= 5 ? AlertSeverity.Critical : AlertSeverity.Error,
                    Title = $"{titlePrefix}: {evt.SignatureName}",
                    Message = $"{evt.Action} {evt.Protocol} from {evt.SourceIp}:{evt.SourcePort}{source.InternalSuffix} to {evt.DestIp}:{evt.DestPort} - {evt.Category}",
                    DeviceIp = evt.SourceIp,
                    SourceUrl = IpDrilldownUrl(evt.SourceIp),
                    Context = source.WithContext(new Dictionary<string, string>
                    {
                        ["signature_id"] = evt.SignatureId.ToString(),
                        ["category"] = evt.Category,
                        ["kill_chain_stage"] = evt.KillChainStage.ToDisplayString()
                    })
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish alert for threat event");
            }
        }
    }

    /// <summary>Alerts on a device reaching (Critical) or trying to reach (Warning) an address on a public block list.</summary>
    private async Task PublishContactAsync(ThreatEvent contact, string? alertSiteSlug, AlertSourceDescriber sources,
        CancellationToken cancellationToken)
    {
        var blocked = contact.Action == ThreatAction.Blocked;
        var device = await sources.DescribeDeviceAsync(contact.SourceIp, contact.SourceMac, cancellationToken);
        var listName = Blocking.PublicListContact.ListName(contact.SignatureName);
        var service = NetworkOptimizer.Core.Helpers.NetworkUtilities.GetPortServiceName(contact.DestPort);
        var transport = service is null ? contact.Protocol : $"{contact.Protocol}, {service}";
        await _alertEventBus.PublishAsync(new AlertEvent
        {
            EventType = blocked ? "threats.public_list_attempt" : "threats.public_list_contact",
            Source = "threats",
            SiteSlug = alertSiteSlug,
            Severity = blocked ? AlertSeverity.Warning : AlertSeverity.Critical,
            Title = blocked ? $"Public Block List: {device.Text} tried to reach a listed address" : $"Public Block List: {device.Text} reached a listed address",
            Message = blocked
                ? $"{device.Text} tried to connect to {contact.DestIp}:{contact.DestPort} ({transport}), an address on {listName}. The connection was blocked. An exception can be added from its details in Threat Intelligence."
                : $"{device.Text} connected to {contact.DestIp}:{contact.DestPort} ({transport}), an address on {listName}.",
            DeviceIp = contact.SourceIp,
            SourceUrl = IpDrilldownUrl(contact.DestIp),
            Context = device.WithContext(new Dictionary<string, string>
            {
                ["list"] = listName,
                ["destination"] = contact.DestIp,
                ["destination_port"] = contact.DestPort.ToString(),
                ["action"] = blocked ? "blocked" : "allowed",
            })
        }, cancellationToken);
    }

    /// <summary>
    /// Threat Intelligence deep link that opens the drilldown for a source IP,
    /// or the page itself when the IP is unknown.
    /// </summary>
    private static string IpDrilldownUrl(string? ip) =>
        string.IsNullOrEmpty(ip) || ip == "unknown"
            ? "/threats"
            : "/threats?tab=drilldown&ip=" + Uri.EscapeDataString(ip);

    /// <summary>
    /// Two-pass flow collection:
    /// Pass 1 (unfiltered): Gets all flows through the page limit, FlowInterestFilter picks out
    ///   medium/high risk detected flows + sensitive port probes. May miss some blocked flows
    ///   buried deep in pagination (allowed flows fill up pages first).
    /// Pass 2 (blocked-only): Gets ALL blocked flows reliably since the API only returns blocked,
    ///   so pagination works through them efficiently.
    /// Deduplication happens in SaveEventsAsync via InnerAlertId.
    /// </summary>
    private async Task<List<ThreatEvent>> CollectTrafficFlowsAsync(
        UniFi.UniFiApiClient apiClient,
        Blocking.PublicListMatcher lists,
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var events = new List<ThreatEvent>();

        // Pass 1: Unfiltered - catches medium/high risk + sensitive port probes via FlowInterestFilter,
        // and allowed connections to the site's public block lists
        var pass1 = await CollectFlowsPassAsync(apiClient, start, end, maxPages,
            actionFilter: null, applyInterestFilter: true, cancellationToken, lists);
        events.AddRange(pass1);

        // Pass 2: Blocked-only - ensures ALL blocked flows are captured regardless of pagination
        var pass1Ids = events.Select(e => e.InnerAlertId).ToHashSet();
        var pass2 = await CollectFlowsPassAsync(apiClient, start, end, maxPages,
            actionFilter: new[] { "blocked" }, applyInterestFilter: false, cancellationToken);
        events.AddRange(pass2.Where(e => !pass1Ids.Contains(e.InnerAlertId)));
        events = await DropLateRepliesAsync(apiClient, events, start, end, maxPages, cancellationToken);

        if (events.Count > 0)
            _logger.LogDebug("Collected {Count} flow events (pass1={Pass1}, pass2={Pass2})",
                events.Count, pass1.Count, pass2.Count);

        return events;
    }

    // How far back a reply's connection may have been opened. Long-lived connections (push, sync) send
    // late packets hours after their last flow record.
    private static readonly TimeSpan ReplyLookback = TimeSpan.FromHours(12);

    /// <summary>
    /// Drops blocked packets that are the tail of a connection a LAN client opened (see
    /// <see cref="Analysis.FlowInterestFilter.IsReplyTo"/>): UniFi Network dropping them once the connection
    /// has closed, like Block Invalid Traffic, and never an attack. One filtered query per 100 addresses.
    /// When the lookup fails, everything is kept.
    /// </summary>
    private async Task<List<ThreatEvent>> DropLateRepliesAsync(
        UniFi.UniFiApiClient apiClient,
        List<ThreatEvent> events,
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var candidates = events.Where(Analysis.FlowInterestFilter.IsReplyCandidate).ToList();
        if (candidates.Count == 0)
            return events;

        var outgoing = new HashSet<(string Ip, int RemotePort, int ClientPort)>();
        try
        {
            foreach (var chunk in candidates.Select(e => e.SourceIp).Distinct(StringComparer.OrdinalIgnoreCase).Chunk(100))
            {
                for (var page = 0; page < maxPages; page++)
                {
                    var response = await apiClient.GetTrafficFlowsAsync(start - ReplyLookback, end, page,
                        actionFilter: ["allowed"], directionFilter: ["outgoing"], destinationIpFilter: chunk,
                        cancellationToken: cancellationToken);
                    if (response.ValueKind == JsonValueKind.Undefined)
                        return events;
                    outgoing.UnionWith(Analysis.FlowInterestFilter.OutgoingConnections(response));
                    if (!(response.TryGetProperty("has_next", out var hn) && hn.ValueKind == JsonValueKind.True))
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Late-reply lookup failed; keeping {Count} reply-shaped flow(s)", candidates.Count);
            return events;
        }

        var replies = candidates.Where(e => Analysis.FlowInterestFilter.IsReplyTo(e, outgoing)).ToHashSet();
        // Unmatched ones split by whether a client reached that server at all, which tells a moved client
        // port from a connection the flow log never recorded.
        var sameServer = candidates.Count(e => !replies.Contains(e)
            && outgoing.Any(o => o.Ip == e.SourceIp.ToLowerInvariant() && o.RemotePort == e.SourcePort));
        _logger.LogDebug("Skipped {Replies} of {Candidates} reply-shaped blocked flow(s) as late packets on LAN clients' own connections " +
            "({Outgoing} outgoing connections found; {SameServer} unmatched had a connection to the same server from another port)",
            replies.Count, candidates.Count, outgoing.Count, sameServer);
        return replies.Count == 0 ? events : events.Where(e => !replies.Contains(e)).ToList();
    }

    private async Task<List<ThreatEvent>> CollectFlowsPassAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        string[]? actionFilter,
        bool applyInterestFilter,
        CancellationToken cancellationToken,
        Blocking.PublicListMatcher? lists = null)
    {
        var events = new List<ThreatEvent>();

        try
        {
            var page = 0;

            while (page < maxPages)
            {
                var response = await apiClient.GetTrafficFlowsAsync(start, end, page,
                    actionFilter: actionFilter, cancellationToken: cancellationToken);
                if (response.ValueKind == JsonValueKind.Undefined)
                    break;

                if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    break;

                var flowsToNormalize = new List<JsonElement>();
                foreach (var flow in data.EnumerateArray())
                {
                    if (Analysis.FlowInterestFilter.IsInvalidStateDrop(flow))
                        continue;
                    if (!applyInterestFilter || Analysis.FlowInterestFilter.IsInteresting(flow)
                        || lists?.Find(Analysis.FlowInterestFilter.DestinationIp(flow)) != null)
                        flowsToNormalize.Add(flow);
                }

                if (flowsToNormalize.Count > 0)
                {
                    var filteredJson = JsonSerializer.Serialize(new { data = flowsToNormalize });
                    using var doc = JsonDocument.Parse(filteredJson);
                    var normalized = _normalizer.NormalizeFlowEvents(doc.RootElement);
                    events.AddRange(normalized);
                }

                var hasNext = response.TryGetProperty("has_next", out var hn) && hn.GetBoolean();
                if (!hasNext) break;
                page++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Traffic flows collection pass failed (filter={Filter})",
                actionFilter != null ? string.Join(",", actionFilter) : "none");
        }

        return events;
    }

    private async Task<List<ThreatEvent>> CollectIpsEventsAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var events = new List<ThreatEvent>();

        // Try v2 system-log first
        try
        {
            var v2Response = await apiClient.GetThreatLogEventsAsync(start, end, cancellationToken: cancellationToken);
            if (v2Response.ValueKind != JsonValueKind.Undefined)
            {
                if (v2Response.TryGetProperty("totalCount", out var totalCount))
                    _logger.LogDebug("v2 IPS API returned totalCount={TotalCount}", totalCount);

                events = _normalizer.NormalizeV2Events(v2Response);
                _logger.LogDebug("Collected {Count} IPS events via v2 API", events.Count);
                return events;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "v2 threat log API failed, falling back to v1");
        }

        // Fall back to v1
        try
        {
            var v1Response = await apiClient.GetIpsEventsAsync(start, end, cancellationToken: cancellationToken);
            if (v1Response.Count > 0)
            {
                var json = JsonSerializer.Serialize(v1Response);
                using var doc = JsonDocument.Parse(json);
                events = _normalizer.NormalizeV1Events(doc.RootElement);
                _logger.LogDebug("Collected {Count} IPS events via v1 API", events.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "v1 IPS events API also failed");
        }

        return events;
    }

    private async Task LoadConfigAsync(IThreatSettingsAccessor settings, CancellationToken ct)
    {
        var interval = await settings.GetSettingAsync("threats.poll_interval_minutes", ct);
        if (interval != null && int.TryParse(interval, out var mins) && mins >= 1)
            _pollIntervalMinutes = mins;

        var retention = await settings.GetSettingAsync("threats.retention_days", ct);
        if (retention != null && int.TryParse(retention, out var days) && days >= 1)
            _retentionDays = days;
    }

    /// <summary>
    /// Reads new events from netopt-waf and feeds them through the same enrichment, pattern,
    /// and alert path as IPS events. The cursor is per WAF instance: a restarted WAF starts over.
    /// </summary>
    private async Task CollectWafAsync(IThreatRepository repository, IThreatSettingsAccessor settings,
        string? siteKey, CancellationToken cancellationToken)
    {
        var statusKey = NormalizeSite(siteKey);
        var site = statusKey.Length == 0 ? null : statusKey;
        var enabled = await settings.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Enabled, site), cancellationToken);
        var url = await settings.GetSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Url, site), cancellationToken);
        var token = await settings.GetDecryptedSettingAsync(WafSettingKeys.ForSite(WafSettingKeys.Token, site), cancellationToken);
        if (enabled != "true" || string.IsNullOrWhiteSpace(url) || string.IsNullOrEmpty(token))
        {
            _wafStatus.TryRemove(statusKey, out _);
            return;
        }

        var cursorKey = WafSettingKeys.ForSite(WafSettingKeys.Cursor, site);
        var (instance, since) = ParseWafCursor(await settings.GetSettingAsync(cursorKey, cancellationToken));
        var http = _siteHttp != null ? await _siteHttp.GetClientAsync(site, cancellationToken) : _httpClientFactory.CreateClient();
        var client = new WafClient(http);
        try
        {
            // A few pages per cycle; a backlog drains over the next cycles.
            for (var pageNo = 0; pageNo < 10; pageNo++)
            {
                var page = await client.GetEventsAsync(url, token, since, cancellationToken);
                if (page.Instance != instance && since != 0)
                {
                    instance = page.Instance;
                    since = 0;
                    continue;
                }
                instance = page.Instance;

                var events = _normalizer.NormalizeWafEvents(page);
                await ProcessAndSaveAsync(events, repository, settings, siteKey, cancellationToken);
                since = page.Next;
                await settings.SaveSettingAsync(cursorKey, $"{instance}:{since}", cancellationToken);
                _wafStatus[statusKey] = new WafStatus(DateTimeOffset.UtcNow, page.Mode, page.Paranoia, page.Started, page.Stats, null, page.ModeSince,
                    page.Version, page.Crs);

                if (page.Events.Count < WafClient.PageSize) break;
            }
        }
        catch (WafClientException ex)
        {
            _wafStatus.TryGetValue(statusKey, out var last);
            _wafStatus[statusKey] = new WafStatus(DateTimeOffset.UtcNow, last?.Mode, last?.Paranoia ?? 0,
                last?.Started, last?.Stats, ex.Message, last?.ModeSince, last?.Version, last?.Crs);
            _logger.LogDebug("WAF poll failed for site {Site}: {Message}", site ?? "main", ex.Message);
        }
        // A WAF failure must never cost the site its IPS and flow collection, which runs next.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "WAF event processing failed");
        }
    }

    internal static (string Instance, ulong Since) ParseWafCursor(string? raw)
    {
        var parts = (raw ?? "").Split(':', 2);
        return parts.Length == 2 && ulong.TryParse(parts[1], out var since) ? (parts[0], since) : ("", 0);
    }

    /// <summary>
    /// Keeps the site's system Infrastructure filters in line with its management hosts: this host on
    /// the default site and each On-Site Agent on its own site, one filter per device-management port.
    /// A filter for a host that left is demoted and disabled but kept; a returning host re-promotes it.
    /// </summary>
    private async Task EnsureInfrastructureFiltersAsync(string? siteKey, IThreatRepository repository, CancellationToken cancellationToken)
    {
        try
        {
            var sources = new List<InfrastructureSource>();
            // This host's address only means something on the default site's LAN.
            if (NormalizeSite(siteKey) == "" && InfrastructureFilters.DetectSelfIpv4() is { } selfIp)
                sources.Add(new InfrastructureSource(selfIp, "Network Optimizer"));
            if (_infrastructureSources != null)
                sources.AddRange(await _infrastructureSources.GetAgentSourcesAsync(siteKey ?? DefaultSiteKey, cancellationToken));

            var changes = InfrastructureFilters.Plan(await repository.GetNoiseFiltersAsync(cancellationToken), sources);
            var site = NormalizeSite(siteKey) == "" ? null : siteKey;
            foreach (var f in changes.Retire)
            {
                await repository.DemoteAndDisableSystemFilterAsync(f.Id, cancellationToken);
                _systemAudit?.NoiseFilterChanged("infrastructure_filter_retired", f.SourceIp ?? "", site,
                    $"No management host on port {f.DestPort?.ToString() ?? "any"} at this address any more");
            }
            foreach (var f in changes.Promote)
            {
                await repository.PromoteToSystemFilterAsync(f.Id, cancellationToken);
                _systemAudit?.NoiseFilterChanged("infrastructure_filter_promoted", f.SourceIp ?? "", site,
                    $"A management host is back on this address (port {f.DestPort})");
            }
            foreach (var f in changes.Add)
            {
                await repository.SaveNoiseFilterAsync(f, cancellationToken);
                _systemAudit?.NoiseFilterChanged("infrastructure_filter_created", f.SourceIp ?? "", site,
                    $"{f.Label}: port {f.DestPort}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to update infrastructure filters for site {Site}", siteKey ?? "main");
        }
    }

    private async Task TryAutoDownloadGeoDatabasesAsync(CancellationToken cancellationToken)
    {
        if (_geoService.IsCityAvailable && _geoService.IsAsnAvailable)
        {
            // Check staleness - re-download if >30 days old
            var dataPath = GetDataPath();
            var dbInfo = _geoService.GetDatabaseInfo(dataPath);
            var staleThreshold = DateTime.UtcNow.AddDays(-30);

            if (dbInfo.CityDate > staleThreshold && dbInfo.AsnDate > staleThreshold)
                return; // Both fresh, nothing to do

            _logger.LogInformation("GeoLite2 databases are >30 days old, checking for auto-update");
        }
        else
        {
            _logger.LogInformation("GeoLite2 databases missing, checking for auto-download");
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();

            var accountId = await settings.GetDecryptedSettingAsync("maxmind.account_id", cancellationToken);
            var licenseKey = await settings.GetDecryptedSettingAsync("maxmind.license_key", cancellationToken);
            if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(licenseKey))
            {
                _logger.LogDebug("MaxMind account ID or license key not configured, skipping auto-download");
                return;
            }

            var dataPath = GetDataPath();
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var (success, message) = await _geoService.DownloadDatabasesAsync(accountId, licenseKey, dataPath, httpClient, cancellationToken);

            if (success)
            {
                _logger.LogInformation("Auto-downloaded GeoLite2 databases: {Message}", message);
                await settings.SaveSettingAsync("maxmind.last_download", DateTime.UtcNow.ToString("O"));
            }
            else
            {
                _logger.LogWarning("Failed to auto-download GeoLite2 databases: {Message}", message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GeoLite2 auto-download failed (non-fatal)");
        }
    }

    private static string GetDataPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            return "/app/data";
        if (OperatingSystem.IsWindows())
            return Path.Combine(AppContext.BaseDirectory, "data");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkOptimizer");
    }
}
