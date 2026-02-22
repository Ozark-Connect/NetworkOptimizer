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

    // On-demand trigger: released by TriggerCollectionAsync(), waited on during poll sleep
    private readonly SemaphoreSlim _triggerSignal = new(0, 1);
    private readonly object _backfillLock = new();
    private bool _hasCollectedOnce;
    private DateTimeOffset? _backfillOverride;

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
    /// Request a backfill collection from a specific start time.
    /// The next collection cycle will use this as the start instead of last_sync_timestamp.
    /// Used when the dashboard switches to a wider time range that may not have data yet.
    /// </summary>
    public void RequestBackfill(DateTimeOffset from)
    {
        lock (_backfillLock) { _backfillOverride = from; }
        TriggerCollection();
    }

    /// <summary>
    /// Synchronously collect threat data for a specific time range.
    /// Awaitable - the caller blocks until collection completes.
    /// Used by the dashboard when the user switches to a wider time range.
    /// </summary>
    public async Task CollectForRangeAsync(DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IThreatRepository>();
        var settings = scope.ServiceProvider.GetRequiredService<IThreatSettingsAccessor>();

        var apiClient = _uniFiClientAccessor.Client;
        if (apiClient == null)
        {
            _logger.LogDebug("UniFi API client not available for on-demand collection");
            return;
        }

        var allEvents = new List<ThreatEvent>();

        var flowEvents = await CollectTrafficFlowsAsync(apiClient, from, to, cancellationToken);
        allEvents.AddRange(flowEvents);

        var ipsEvents = await CollectIpsEventsAsync(apiClient, from, to, cancellationToken);
        allEvents.AddRange(ipsEvents);

        if (allEvents.Count == 0)
        {
            _logger.LogDebug("On-demand collection for {From} to {To}: no events found", from, to);
            return;
        }

        _geoService.EnrichEvents(allEvents);

        foreach (var evt in allEvents)
            evt.KillChainStage = _classifier.Classify(evt);

        await repository.SaveEventsAsync(allEvents, cancellationToken);

        _logger.LogInformation("On-demand collection: {Count} events ({Flows} flows, {Ips} IPS) for {From} to {To}",
            allEvents.Count, flowEvents.Count, ipsEvents.Count, from, to);
    }

    /// <summary>
    /// Whether the service has completed at least one collection cycle.
    /// </summary>
    public bool HasCollectedOnce => _hasCollectedOnce;

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

        // Load config
        await LoadConfigAsync(settings, cancellationToken);

        // Check if collection is enabled (null = not set = enabled by default)
        var enabled = await settings.GetSettingAsync("threats.enabled", cancellationToken);
        if (string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("Threat collection disabled");
            return;
        }

        // Determine sync window
        DateTimeOffset? backfill;
        lock (_backfillLock) { backfill = _backfillOverride; _backfillOverride = null; }
        var lastSync = await settings.GetSettingAsync("threats.last_sync_timestamp", cancellationToken);
        DateTimeOffset start;

        if (backfill.HasValue)
        {
            // Dashboard requested a wider backfill (e.g., user switched to 30d view)
            // Use the earlier of backfill request or last sync
            start = lastSync != null
                ? DateTimeOffset.Parse(lastSync) < backfill.Value ? DateTimeOffset.Parse(lastSync) : backfill.Value
                : backfill.Value;
            _logger.LogInformation("Backfill requested from {From}", start);
        }
        else if (lastSync != null)
        {
            start = DateTimeOffset.Parse(lastSync);

            // If the stored cursor would create a window of less than 1 hour AND we haven't
            // successfully collected events yet, this is likely a stale cursor from a prior
            // deploy. Reset to 7 days so the first collection backfills historical data.
            if (!_hasCollectedOnce && DateTimeOffset.UtcNow - start < TimeSpan.FromHours(1))
            {
                var eventCount = await repository.GetThreatSummaryAsync(
                    DateTime.UtcNow.AddDays(-7), DateTime.UtcNow, cancellationToken);
                if (eventCount.TotalEvents == 0)
                {
                    _logger.LogInformation("Stale sync cursor detected with empty DB, resetting to 7-day backfill");
                    start = DateTimeOffset.UtcNow.AddDays(-7);
                }
            }
        }
        else
        {
            start = DateTimeOffset.UtcNow.AddDays(-7);
        }
        var end = DateTimeOffset.UtcNow;

        // Get the UniFi API client via the accessor (singleton, connected via UniFiConnectionService)
        var apiClient = _uniFiClientAccessor.Client;
        if (apiClient == null)
        {
            _logger.LogDebug("UniFi API client not available, skipping threat collection");
            return;
        }

        var allEvents = new List<ThreatEvent>();

        // 1. Collect traffic flows (PRIMARY source)
        var flowEvents = await CollectTrafficFlowsAsync(apiClient, start, end, cancellationToken);
        allEvents.AddRange(flowEvents);

        // 2. Collect IPS events (SECONDARY source)
        var ipsEvents = await CollectIpsEventsAsync(apiClient, start, end, cancellationToken);
        allEvents.AddRange(ipsEvents);

        if (allEvents.Count == 0)
        {
            _logger.LogDebug("No new threat events found");
            await settings.SaveSettingAsync("threats.last_sync_timestamp", end.ToString("O"));
            return;
        }

        // 3. Enrich with geo data (flow events with internal source -> enrich on dest IP)
        _geoService.EnrichEvents(allEvents);

        // 4. Classify kill chain stages
        foreach (var evt in allEvents)
        {
            evt.KillChainStage = _classifier.Classify(evt);
        }

        // 5. Save to database
        await repository.SaveEventsAsync(allEvents, cancellationToken);

        // Update sync cursor
        await settings.SaveSettingAsync("threats.last_sync_timestamp", end.ToString("O"));

        // 6. Run pattern analysis
        try
        {
            var analysisWindow = DateTime.UtcNow.AddHours(-1);
            var recentEvents = await repository.GetEventsAsync(analysisWindow, DateTime.UtcNow, limit: 5000, cancellationToken: cancellationToken);
            var patterns = _patternAnalyzer.DetectPatterns(recentEvents);
            foreach (var pattern in patterns)
            {
                await repository.SavePatternAsync(pattern, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Pattern analysis failed");
        }

        // 7. Publish high-severity events to alert bus
        var criticalEvents = allEvents.Where(e => e.Severity >= 4).ToList();
        foreach (var evt in criticalEvents)
        {
            try
            {
                var eventType = evt.EventSource == Models.EventSource.TrafficFlow
                    ? "threats.traffic_flow"
                    : "threats.ips_event";
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

        _logger.LogInformation("Processed {Total} threat events ({Flows} flows, {Ips} IPS, {Critical} critical)",
            allEvents.Count, flowEvents.Count, ipsEvents.Count, criticalEvents.Count);

        // Periodic cleanup (3 AM UTC)
        if (DateTime.UtcNow.Hour == 3 && DateTime.UtcNow.Minute < _pollIntervalMinutes)
        {
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            await repository.PurgeOldEventsAsync(cutoff, cancellationToken);
            await repository.PurgeCrowdSecCacheAsync(cancellationToken);
        }
    }

    private async Task<List<ThreatEvent>> CollectTrafficFlowsAsync(
        UniFi.UniFiApiClient apiClient,
        DateTimeOffset start,
        DateTimeOffset end,
        CancellationToken cancellationToken)
    {
        var events = new List<ThreatEvent>();

        try
        {
            const int maxPages = 50;
            var page = 0;

            while (page < maxPages)
            {
                var response = await apiClient.GetTrafficFlowsAsync(start, end, page, cancellationToken: cancellationToken);
                if (response.ValueKind == JsonValueKind.Undefined)
                    break;

                if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    break;

                // Filter interesting flows before normalization
                var interestingFlows = new List<JsonElement>();
                foreach (var flow in data.EnumerateArray())
                {
                    if (Analysis.FlowInterestFilter.IsInteresting(flow))
                        interestingFlows.Add(flow);
                }

                if (interestingFlows.Count > 0)
                {
                    // Build a filtered response for the normalizer
                    var filteredJson = JsonSerializer.Serialize(new { data = interestingFlows });
                    using var doc = JsonDocument.Parse(filteredJson);
                    var normalized = _normalizer.NormalizeFlowEvents(doc.RootElement);
                    events.AddRange(normalized);
                }

                // Check pagination
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
