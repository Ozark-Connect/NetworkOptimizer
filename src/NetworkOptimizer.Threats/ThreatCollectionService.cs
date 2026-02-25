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
    private bool _hasCollectedOnce;

    // Geo database staleness check (24h cooldown to avoid checking every cycle)
    private DateTimeOffset _lastGeoCheck = DateTimeOffset.MinValue;
    private bool _geoBackfillComplete;

    // Track source IPs we've already alerted on for attack chains (reset periodically)
    private readonly HashSet<string> _alertedChainIps = new();
    private readonly HashSet<string> _alertedChainAttemptIps = new();
    private DateTime _lastChainAlertReset = DateTime.UtcNow;

    public ThreatCollectionService(
        IServiceScopeFactory scopeFactory,
        ILogger<ThreatCollectionService> logger,
        ThreatEventNormalizer normalizer,
        GeoEnrichmentService geoService,
        KillChainClassifier classifier,
        ThreatPatternAnalyzer patternAnalyzer,
        IAlertEventBus alertEventBus,
        IHttpClientFactory httpClientFactory,
        IUniFiClientAccessor uniFiClientAccessor)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _normalizer = normalizer;
        _geoService = geoService;
        _classifier = classifier;
        _patternAnalyzer = patternAnalyzer;
        _alertEventBus = alertEventBus;
        _httpClientFactory = httpClientFactory;
        _uniFiClientAccessor = uniFiClientAccessor;
    }

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
    /// Whether the service has completed at least one collection cycle.
    /// </summary>
    public bool HasCollectedOnce => _hasCollectedOnce;

    /// <summary>
    /// How far back the gradual backfill has reached. Null if backfill hasn't started or is complete.
    /// The dashboard uses this to show "Data from {date} - present (building...)" coverage info.
    /// </summary>
    public DateTimeOffset? BackfillCursor { get; private set; }

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
                await CollectAndProcessAsync(stoppingToken);
                _hasCollectedOnce = true;
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

    private async Task CollectAndProcessAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();

        await LoadConfigAsync(settings, cancellationToken);

        var enabled = await settings.GetSettingAsync("threats.enabled", cancellationToken);
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Threat collection disabled");
            return;
        }

        var apiClient = _uniFiClientAccessor.Client;
        if (apiClient == null)
        {
            _logger.LogDebug("UniFi API client not available, skipping threat collection");
            return;
        }

        // === PHASE 1: Incremental collection from last sync ===
        // On first run (or after >24h gap), sweep the full 24 hours in 1-hour chunks
        // to avoid hitting the ~1000-result pagination cap. On subsequent runs, only
        // query from last sync (with 2-min overlap for API eventual consistency).
        var now = DateTimeOffset.UtcNow;
        var lastSyncStr = await settings.GetSettingAsync("threats.last_sync_timestamp", cancellationToken);
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

            var chunkEvents = await CollectRangeAsync(apiClient, chunkCursor, chunkEnd, maxPages: int.MaxValue, cancellationToken);
            await ProcessAndSaveAsync(chunkEvents, repository, cancellationToken);
            totalRecentEvents += chunkEvents.Count;

            chunkCursor = chunkEnd;
        }

        await settings.SaveSettingAsync("threats.last_sync_timestamp", now.ToString("O"));

        if (totalRecentEvents > 0)
            _logger.LogInformation("Collected {Count} threat events", totalRecentEvents);

        // === PHASE 2: Gradual backfill (>24h ago) - page-limited to stay gentle ===
        // Backfill 30 days (data retention is separate at _retentionDays)
        const int backfillDays = 30;
        var backfillCursorStr = await settings.GetSettingAsync("threats.backfill_cursor", cancellationToken);
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

                var backfillEvents = await CollectRangeAsync(apiClient, chunkStart, chunkEnd, maxPages: 20, cancellationToken);
                await ProcessAndSaveAsync(backfillEvents, repository, cancellationToken);

                cursor = chunkStart;
                await settings.SaveSettingAsync("threats.backfill_cursor", cursor.ToString("O"));
                BackfillCursor = cursor;

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
            BackfillCursor = null; // Backfill complete
            _logger.LogDebug("Backfill complete - coverage back to {Days}d", backfillDays);
        }

        // Periodic geo database staleness check (every 24h, triggered by dashboard loading)
        if (DateTimeOffset.UtcNow - _lastGeoCheck > TimeSpan.FromHours(24))
        {
            _lastGeoCheck = DateTimeOffset.UtcNow;
            await TryAutoDownloadGeoDatabasesAsync(cancellationToken);
        }

        // Re-enrich existing events that lack geo data (runs each cycle until complete)
        if (_geoService.IsCityAvailable && !_geoBackfillComplete)
        {
            var enriched = await repository.BackfillGeoDataAsync(
                events => _geoService.EnrichEvents(events), batchSize: 2000, cancellationToken);
            if (enriched == 0)
            {
                _geoBackfillComplete = true;
                _logger.LogDebug("Geo data backfill complete - all events enriched");
            }
            else
            {
                _logger.LogInformation("Geo backfill: enriched {Count} events with geo data", enriched);
            }
        }

        // Periodic cleanup (3 AM UTC)
        if (DateTime.UtcNow.Hour == 3 && DateTime.UtcNow.Minute < _pollIntervalMinutes)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            await repository.PurgeOldEventsAsync(cutoff, cancellationToken);
            await repository.PurgeCrowdSecCacheAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Collect traffic flows and IPS events for a specific time range.
    /// </summary>
    private async Task<List<ThreatEvent>> CollectRangeAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start, DateTimeOffset end,
        int maxPages, CancellationToken cancellationToken)
    {
        var allEvents = new List<ThreatEvent>();

        var flowEvents = await CollectTrafficFlowsAsync(apiClient, start, end, maxPages, cancellationToken);
        allEvents.AddRange(flowEvents);

        var ipsEvents = await CollectIpsEventsAsync(apiClient, start, end, cancellationToken);
        allEvents.AddRange(ipsEvents);

        return allEvents;
    }

    /// <summary>
    /// Enrich, classify, save events, run pattern analysis, and publish alerts.
    /// </summary>
    private async Task ProcessAndSaveAsync(List<ThreatEvent> events,
        IThreatRepository repository, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;

        _geoService.EnrichEvents(events);

        foreach (var evt in events)
            evt.KillChainStage = _classifier.Classify(evt);

        await repository.SaveEventsAsync(events, cancellationToken);

        // Pattern analysis on recent data
        try
        {
            var recentEvents = await repository.GetEventsAsync(
                DateTime.UtcNow.AddHours(-6), DateTime.UtcNow, limit: 5000, cancellationToken: cancellationToken);
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
                        Severity = severity,
                        Title = pattern.Description,
                        Message = $"{pattern.PatternType} pattern detected: {pattern.EventCount} events, confidence {pattern.Confidence:P0}",
                        DeviceIp = firstSourceIp,
                        Context = new Dictionary<string, string>
                        {
                            ["pattern_type"] = pattern.PatternType.ToString(),
                            ["event_count"] = pattern.EventCount.ToString(),
                            ["confidence"] = pattern.Confidence.ToString("F2"),
                            ["target_port"] = pattern.TargetPort?.ToString() ?? "",
                            ["source_ips"] = string.Join(", ", sourceIps.Take(5))
                        }
                    }, cancellationToken);

                    await repository.MarkPatternAlertedAsync(pattern.Id, DateTime.UtcNow, cancellationToken);
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
        try
        {
            // Reset alerted IPs every 6 hours to allow re-alerting on continued chains
            if ((DateTime.UtcNow - _lastChainAlertReset).TotalHours >= 6)
            {
                _alertedChainIps.Clear();
                _alertedChainAttemptIps.Clear();
                _lastChainAlertReset = DateTime.UtcNow;
            }

            var sequences = await repository.GetAttackSequencesAsync(
                DateTime.UtcNow.AddHours(-6), DateTime.UtcNow, limit: 20, cancellationToken);

            // Alert on chains with 2+ stages ending in ActiveExploitation, PostExploitation, or Monitored
            var alertableEndings = new[] { KillChainStage.ActiveExploitation, KillChainStage.PostExploitation, KillChainStage.Monitored };
            foreach (var seq in sequences)
            {
                if (seq.Stages.Count < 2) continue;

                var lastStage = seq.Stages[^1].Stage;
                if (!alertableEndings.Contains(lastStage)) continue;

                // Skip if already alerted for this IP in this window
                if (!_alertedChainIps.Add(seq.SourceIp)) continue;

                var stageNames = string.Join(" -> ", seq.Stages.Select(s => s.Stage.ToDisplayString()));
                var totalEvents = seq.Stages.Sum(s => s.EventCount);
                var severity = lastStage is KillChainStage.ActiveExploitation or KillChainStage.PostExploitation
                    ? AlertSeverity.Critical : AlertSeverity.Warning;

                // 2-stage chains ending in Monitored are likely normal admin/scanning traffic
                var isLowConfidence = seq.Stages.Count == 2 && lastStage == KillChainStage.Monitored;
                var message = $"{seq.SourceIp} ({seq.CountryCode ?? "unknown"}) progressed through {seq.Stages.Count} kill chain stages with {totalEvents} events";
                if (isLowConfidence)
                    message += ". Note: 2-stage chains ending in Monitored may be typical administration or scanning traffic rather than a real attack.";

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "threats.attack_chain",
                    Source = "threats",
                    Severity = severity,
                    Title = $"Attack chain: {stageNames}",
                    Message = message,
                    DeviceIp = seq.SourceIp,
                    Context = new Dictionary<string, string>
                    {
                        ["stages"] = stageNames,
                        ["stage_count"] = seq.Stages.Count.ToString(),
                        ["total_events"] = totalEvents.ToString(),
                        ["country"] = seq.CountryCode ?? "unknown",
                        ["asn"] = seq.AsnOrg ?? "unknown"
                    }
                }, cancellationToken);

                _logger.LogInformation("Attack chain detected: {Ip} ({Country}) - {Stages}",
                    seq.SourceIp, seq.CountryCode, stageNames);
            }

            // Second pass: early-stage chains (e.g. Recon -> AttemptedExploitation) that didn't meet
            // the end-state filter above. These are lower confidence but useful for security-minded users.
            foreach (var seq in sequences)
            {
                if (seq.Stages.Count < 2) continue;

                // Skip if already alerted as a full chain or as an attempt
                if (_alertedChainIps.Contains(seq.SourceIp)) continue;
                if (!_alertedChainAttemptIps.Add(seq.SourceIp)) continue;

                var stageNames = string.Join(" -> ", seq.Stages.Select(s => s.Stage.ToDisplayString()));
                var totalEvents = seq.Stages.Sum(s => s.EventCount);

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = "threats.attack_chain_attempt",
                    Source = "threats",
                    Severity = AlertSeverity.Info,
                    Title = $"Early-stage attack chain: {stageNames}",
                    Message = $"{seq.SourceIp} ({seq.CountryCode ?? "unknown"}) progressed through {seq.Stages.Count} early kill chain stages with {totalEvents} events. " +
                              "This may indicate a blocked attack or reconnaissance activity that did not reach exploitation.",
                    DeviceIp = seq.SourceIp,
                    Context = new Dictionary<string, string>
                    {
                        ["stages"] = stageNames,
                        ["stage_count"] = seq.Stages.Count.ToString(),
                        ["total_events"] = totalEvents.ToString(),
                        ["country"] = seq.CountryCode ?? "unknown",
                        ["asn"] = seq.AsnOrg ?? "unknown"
                    }
                }, cancellationToken);

                _logger.LogDebug("Early-stage attack chain detected: {Ip} ({Country}) - {Stages}",
                    seq.SourceIp, seq.CountryCode, stageNames);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Attack chain detection failed");
        }

        // Publish high-severity events to alert bus
        foreach (var evt in events.Where(e => e.Severity >= 4))
        {
            try
            {
                var eventType = evt.EventSource == Models.EventSource.TrafficFlow
                    ? "threats.traffic_flow" : "threats.ips_event";
                var titlePrefix = evt.EventSource == Models.EventSource.TrafficFlow ? "Flow" : "IPS";

                await _alertEventBus.PublishAsync(new AlertEvent
                {
                    EventType = eventType,
                    Source = "threats",
                    Severity = evt.Severity >= 5 ? AlertSeverity.Critical : AlertSeverity.Error,
                    Title = $"{titlePrefix}: {evt.SignatureName}",
                    Message = $"{evt.Action} {evt.Protocol} from {evt.SourceIp}:{evt.SourcePort} to {evt.DestIp}:{evt.DestPort} - {evt.Category}",
                    DeviceIp = evt.SourceIp,
                    Context = new Dictionary<string, string>
                    {
                        ["signature_id"] = evt.SignatureId.ToString(),
                        ["category"] = evt.Category,
                        ["kill_chain_stage"] = evt.KillChainStage.ToDisplayString(),
                        ["country"] = evt.CountryCode ?? "unknown"
                    }
                }, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to publish alert for threat event");
            }
        }
    }

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
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var events = new List<ThreatEvent>();

        // Pass 1: Unfiltered - catches medium/high risk + sensitive port probes via FlowInterestFilter
        var pass1 = await CollectFlowsPassAsync(apiClient, start, end, maxPages,
            actionFilter: null, applyInterestFilter: true, cancellationToken);
        events.AddRange(pass1);

        // Pass 2: Blocked-only - ensures ALL blocked flows are captured regardless of pagination
        var pass1Ids = events.Select(e => e.InnerAlertId).ToHashSet();
        var pass2 = await CollectFlowsPassAsync(apiClient, start, end, maxPages,
            actionFilter: new[] { "blocked" }, applyInterestFilter: false, cancellationToken);
        events.AddRange(pass2.Where(e => !pass1Ids.Contains(e.InnerAlertId)));

        if (events.Count > 0)
            _logger.LogDebug("Collected {Count} flow events (pass1={Pass1}, pass2={Pass2})",
                events.Count, pass1.Count, pass2.Count);

        return events;
    }

    private async Task<List<ThreatEvent>> CollectFlowsPassAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        string[]? actionFilter,
        bool applyInterestFilter,
        CancellationToken cancellationToken)
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
                    if (!applyInterestFilter || Analysis.FlowInterestFilter.IsInteresting(flow))
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
                if (events.Count > 0)
                {
                    _logger.LogDebug("Collected {Count} IPS events via v2 API", events.Count);
                    return events;
                }
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
