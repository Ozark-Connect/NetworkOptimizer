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

    // Gradual backfill: tracks how far back we've collected

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

        // === PHASE 1: Forward edge - collect new data since last sync ===
        var lastSync = await settings.GetSettingAsync("threats.last_sync_timestamp", cancellationToken);
        var forwardStart = lastSync != null ? DateTimeOffset.Parse(lastSync) : DateTimeOffset.UtcNow.AddMinutes(-5);
        var now = DateTimeOffset.UtcNow;

        var forwardEvents = await CollectRangeAsync(apiClient, forwardStart, now, maxPages: 50, cancellationToken);
        await ProcessAndSaveAsync(forwardEvents, repository, cancellationToken);
        await settings.SaveSettingAsync("threats.last_sync_timestamp", now.ToString("O"));

        if (forwardEvents.Count > 0)
            _logger.LogInformation("Forward: {Count} new events", forwardEvents.Count);

        // === PHASE 2: Gradual backfill - nibble backwards through history ===
        var backfillCursorStr = await settings.GetSettingAsync("threats.backfill_cursor", cancellationToken);
        var retentionLimit = DateTimeOffset.UtcNow.AddDays(-_retentionDays);

        // Initialize cursor to "now" on first run (we'll work backwards from here)
        var cursor = backfillCursorStr != null ? DateTimeOffset.Parse(backfillCursorStr) : now;

        if (cursor > retentionLimit)
        {
            // Work backwards in 6-hour chunks, 20 pages per cycle (~6h to reach 90 days)
            var chunkEnd = cursor;
            var chunkStart = cursor.AddHours(-6);
            if (chunkStart < retentionLimit) chunkStart = retentionLimit;

            var backfillEvents = await CollectRangeAsync(apiClient, chunkStart, chunkEnd, maxPages: 20, cancellationToken);
            await ProcessAndSaveAsync(backfillEvents, repository, cancellationToken);

            // Move cursor back
            cursor = chunkStart;
            await settings.SaveSettingAsync("threats.backfill_cursor", cursor.ToString("O"));
            BackfillCursor = cursor;

            if (backfillEvents.Count > 0)
                _logger.LogInformation("Backfill: {Count} events from {From} to {To}", backfillEvents.Count, chunkStart, chunkEnd);
            else
                _logger.LogDebug("Backfill: 0 events from {From} to {To}, cursor at {Cursor}", chunkStart, chunkEnd, cursor);
        }
        else
        {
            BackfillCursor = null; // Backfill complete
            _logger.LogDebug("Backfill complete - coverage back to retention limit ({Days}d)", _retentionDays);
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
                DateTime.UtcNow.AddHours(-1), DateTime.UtcNow, limit: 5000, cancellationToken: cancellationToken);
            var patterns = _patternAnalyzer.DetectPatterns(recentEvents);
            foreach (var pattern in patterns)
                await repository.SavePatternAsync(pattern, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pattern analysis failed");
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
                        ["kill_chain_stage"] = evt.KillChainStage.ToString(),
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

    private async Task<List<ThreatEvent>> CollectTrafficFlowsAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start,
        DateTimeOffset end,
        int maxPages,
        CancellationToken cancellationToken)
    {
        var events = new List<ThreatEvent>();

        try
        {
            var page = 0;

            while (page < maxPages)
            {
                var response = await apiClient.GetTrafficFlowsAsync(start, end, page, cancellationToken: cancellationToken);
                if (response.ValueKind == JsonValueKind.Undefined)
                    break;

                if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    break;

                var interestingFlows = new List<JsonElement>();
                foreach (var flow in data.EnumerateArray())
                {
                    if (Analysis.FlowInterestFilter.IsInteresting(flow))
                        interestingFlows.Add(flow);
                }

                if (interestingFlows.Count > 0)
                {
                    var filteredJson = JsonSerializer.Serialize(new { data = interestingFlows });
                    using var doc = JsonDocument.Parse(filteredJson);
                    var normalized = _normalizer.NormalizeFlowEvents(doc.RootElement);
                    events.AddRange(normalized);
                }

                var hasNext = response.TryGetProperty("has_next", out var hn) && hn.GetBoolean();
                if (!hasNext) break;
                page++;
            }

            if (events.Count > 0)
                _logger.LogDebug("Collected {Count} interesting flow events across {Pages} pages", events.Count, page + 1);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Traffic flows collection failed");
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

            var licenseKey = await settings.GetDecryptedSettingAsync("maxmind.license_key", cancellationToken);
            if (string.IsNullOrEmpty(licenseKey))
            {
                _logger.LogDebug("No MaxMind license key configured, skipping auto-download");
                return;
            }

            var dataPath = GetDataPath();
            using var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            var (success, message) = await _geoService.DownloadDatabasesAsync(licenseKey, dataPath, httpClient, cancellationToken);

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
