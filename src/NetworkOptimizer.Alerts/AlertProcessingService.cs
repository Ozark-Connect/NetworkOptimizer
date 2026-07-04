using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Background service that consumes alert events from the event bus,
/// evaluates them against configured rules, persists history,
/// correlates into incidents, and dispatches to delivery channels.
/// </summary>
public class AlertProcessingService : BackgroundService
{
    private readonly ILogger<AlertProcessingService> _logger;
    private readonly IAlertEventBus _eventBus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AlertRuleEvaluator _ruleEvaluator;
    private readonly AlertCorrelationService _correlationService;
    private readonly IEnumerable<IAlertDeliveryChannel> _deliveryChannels;
    private readonly AlertCooldownTracker _cooldownTracker;
    private readonly string? _appBaseUrl;

    // In-memory rule cache (refreshed periodically)
    // Enabled rules cached per originating site ("" = default/main). Site DBs have
    // independent rule id sequences, so caches must never be shared across sites.
    private readonly ConcurrentDictionary<string, (List<AlertRule> Rules, DateTime CachedAt)> _rulesBySite = new();
    private static readonly TimeSpan RuleCacheDuration = TimeSpan.FromSeconds(60);
    private DateTime _lastCooldownCleanup = DateTime.UtcNow;
    private static readonly TimeSpan CooldownCleanupInterval = TimeSpan.FromMinutes(30);

    public AlertProcessingService(
        ILogger<AlertProcessingService> logger,
        IAlertEventBus eventBus,
        IServiceScopeFactory scopeFactory,
        AlertRuleEvaluator ruleEvaluator,
        AlertCorrelationService correlationService,
        IEnumerable<IAlertDeliveryChannel> deliveryChannels,
        AlertCooldownTracker cooldownTracker,
        IConfiguration configuration)
    {
        _logger = logger;
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
        _ruleEvaluator = ruleEvaluator;
        _correlationService = correlationService;
        _deliveryChannels = deliveryChannels;
        _cooldownTracker = cooldownTracker;

        // Build base URL using same priority as canonical host redirect in Program.cs:
        // REVERSE_PROXIED_HOST_NAME (https) > HOST_NAME (http:8042) > HOST_IP (http:8042)
        var reverseProxy = configuration["REVERSE_PROXIED_HOST_NAME"];
        var hostName = configuration["HOST_NAME"];
        var hostIp = configuration["HOST_IP"];

        if (!string.IsNullOrEmpty(reverseProxy))
            _appBaseUrl = $"https://{reverseProxy}";
        else if (!string.IsNullOrEmpty(hostName))
            _appBaseUrl = $"http://{hostName}:8042";
        else if (!string.IsNullOrEmpty(hostIp))
            _appBaseUrl = $"http://{hostIp}:8042";
        else
        {
            var detectedIp = NetworkUtilities.DetectLocalIpFromInterfaces();
            if (!string.IsNullOrEmpty(detectedIp))
                _appBaseUrl = $"http://{detectedIp}:8042";
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Alert processing service started");

        try
        {
            await foreach (var alertEvent in _eventBus.ConsumeAsync(stoppingToken))
            {
                try
                {
                    await ProcessEventAsync(alertEvent, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process alert event {EventType}", alertEvent.EventType);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        _logger.LogInformation("Alert processing service stopped");
    }

    private async Task ProcessEventAsync(AlertEvent alertEvent, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        // Pin the scope to the site this alert came from BEFORE the repository resolves its
        // DbContext, so rules and history read/write that site's database. A null slug is
        // the default (main) site, so single-site installs are unaffected.
        scope.ServiceProvider.GetRequiredService<IAlertSiteScope>().UseSite(alertEvent.SiteSlug);
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var siteKey = alertEvent.SiteSlug ?? "";
        var rules = await GetRulesAsync(repository, siteKey, cancellationToken);

        // Periodic cooldown cleanup to prevent unbounded growth
        if ((DateTime.UtcNow - _lastCooldownCleanup) > CooldownCleanupInterval)
        {
            CleanupCooldowns();
            _lastCooldownCleanup = DateTime.UtcNow;
        }

        // Evaluate event against this site's rules
        var matchingRules = _ruleEvaluator.Evaluate(alertEvent, rules);
        if (matchingRules.Count == 0)
        {
            _logger.LogDebug("No matching rules for event {EventType} (site {Site})",
                alertEvent.EventType, siteKey.Length == 0 ? "main" : siteKey);
            return;
        }

        foreach (var rule in matchingRules)
        {
            try
            {
                _ruleEvaluator.RecordFired(rule, alertEvent);
                await ProcessRuleMatchAsync(alertEvent, rule, repository, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process rule {RuleId} for event {EventType}", rule.Id, alertEvent.EventType);
            }
        }
    }

    private async Task ProcessRuleMatchAsync(
        AlertEvent alertEvent,
        AlertRule rule,
        IAlertRepository repository,
        CancellationToken cancellationToken)
    {
        // Create history entry
        var historyEntry = new AlertHistoryEntry
        {
            EventType = alertEvent.EventType,
            Severity = alertEvent.Severity,
            Source = alertEvent.Source,
            Title = alertEvent.Title,
            Message = alertEvent.Message,
            DeviceId = alertEvent.DeviceId,
            DeviceName = alertEvent.DeviceName,
            DeviceIp = alertEvent.DeviceIp,
            SourceUrl = ResolveSourceUrl(alertEvent.SourceUrl),
            RuleId = rule.Id,
            TriggeredAt = DateTime.UtcNow,
            ContextJson = alertEvent.Context.Count > 0
                ? JsonSerializer.Serialize(alertEvent.Context)
                : null
        };

        await repository.SaveAlertAsync(historyEntry, cancellationToken);

        // Correlate into incidents
        await _correlationService.CorrelateAsync(alertEvent, historyEntry, repository, cancellationToken);

        // Persist incident correlation even for digest-only rules
        if (historyEntry.IncidentId.HasValue)
        {
            await repository.UpdateAlertAsync(historyEntry, cancellationToken);
        }

        // Skip delivery for digest-only rules
        if (rule.DigestOnly)
        {
            _logger.LogDebug("Rule {RuleId} is digest-only, skipping immediate delivery", rule.Id);
            return;
        }

        // Deliver to matching channels (use resolved absolute URL for delivery)
        var deliveryEvent = alertEvent with { SourceUrl = historyEntry.SourceUrl };
        await DeliverAsync(deliveryEvent, historyEntry, repository, cancellationToken);
    }

    private async Task DeliverAsync(
        AlertEvent alertEvent,
        AlertHistoryEntry historyEntry,
        IAlertRepository repository,
        CancellationToken cancellationToken)
    {
        // Deliver to this site's own channels plus the global (main-site) channels. For an
        // alert from the default site the two are the same set, so there's no extra query.
        var channels = await repository.GetEnabledChannelsAsync(cancellationToken);
        if (!string.IsNullOrEmpty(alertEvent.SiteSlug))
        {
            using var mainScope = _scopeFactory.CreateScope();
            mainScope.ServiceProvider.GetRequiredService<IAlertSiteScope>().UseSite(null);
            var mainRepo = mainScope.ServiceProvider.GetRequiredService<IAlertRepository>();
            var globalChannels = await mainRepo.GetEnabledChannelsAsync(cancellationToken);
            channels = channels.Concat(globalChannels).ToList();
        }

        var deliveredTo = new List<int>();
        var errors = new List<string>();

        foreach (var channel in channels)
        {
            // Skip channels with higher minimum severity than this alert
            if (alertEvent.Severity < channel.MinSeverity)
                continue;

            // Channels with digest enabled still get immediate alerts too
            // (digest is an additional summary, not a replacement for immediate delivery)

            var handler = _deliveryChannels.FirstOrDefault(d => d.ChannelType == channel.ChannelType);
            if (handler == null)
            {
                _logger.LogWarning("No delivery handler for channel type {Type}", channel.ChannelType);
                continue;
            }

            try
            {
                var success = await handler.SendAsync(alertEvent, historyEntry, channel, cancellationToken);
                if (success)
                {
                    deliveredTo.Add(channel.Id);
                }
                else
                {
                    errors.Add($"Channel {channel.Id} ({channel.Name}): delivery returned false");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver alert to channel {ChannelId} ({ChannelName})",
                    channel.Id, channel.Name);
                errors.Add($"Channel {channel.Id} ({channel.Name}): {ex.Message}");
            }
        }

        // Update history entry with delivery results
        historyEntry.DeliveredToChannels = deliveredTo.Count > 0
            ? string.Join(",", deliveredTo)
            : null;
        historyEntry.DeliverySucceeded = deliveredTo.Count > 0 && errors.Count == 0;
        historyEntry.DeliveryError = errors.Count > 0
            ? string.Join("; ", errors)
            : null;

        await repository.UpdateAlertAsync(historyEntry, cancellationToken);
    }

    private async Task<List<AlertRule>> GetRulesAsync(IAlertRepository repository, string siteKey, CancellationToken cancellationToken)
    {
        var hasCache = _rulesBySite.TryGetValue(siteKey, out var cached);
        if (hasCache && (DateTime.UtcNow - cached.CachedAt) < RuleCacheDuration)
            return cached.Rules;

        try
        {
            var rules = await repository.GetEnabledRulesAsync(cancellationToken);
            _rulesBySite[siteKey] = (rules, DateTime.UtcNow);
            return rules;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh alert rule cache for site {Site}",
                siteKey.Length == 0 ? "main" : siteKey);
            // Keep using stale cache rather than failing
            return hasCache ? cached.Rules : [];
        }
    }

    /// <summary>
    /// Resolves a relative SourceUrl (e.g., "/audit") to an absolute URL using the app's
    /// configured hostname. Falls back to the relative path if no hostname is configured.
    /// </summary>
    private string? ResolveSourceUrl(string? relativeUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl))
            return null;

        if (_appBaseUrl != null)
            return $"{_appBaseUrl}{relativeUrl}";

        return relativeUrl;
    }

    private void CleanupCooldowns()
    {
        _cooldownTracker.Cleanup(TimeSpan.FromHours(2));
    }
}
