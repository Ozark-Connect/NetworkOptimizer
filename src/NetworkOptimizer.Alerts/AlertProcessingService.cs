using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

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

    // In-memory rule cache (refreshed periodically)
    private List<AlertRule> _cachedRules = [];
    private DateTime _rulesCachedAt = DateTime.MinValue;
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
        AlertCooldownTracker cooldownTracker)
    {
        _logger = logger;
        _eventBus = eventBus;
        _scopeFactory = scopeFactory;
        _ruleEvaluator = ruleEvaluator;
        _correlationService = correlationService;
        _deliveryChannels = deliveryChannels;
        _cooldownTracker = cooldownTracker;
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
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        // Refresh rule cache if stale
        await RefreshRuleCacheAsync(repository, cancellationToken);

        // Periodic cooldown cleanup to prevent unbounded growth
        if ((DateTime.UtcNow - _lastCooldownCleanup) > CooldownCleanupInterval)
        {
            CleanupCooldowns();
            _lastCooldownCleanup = DateTime.UtcNow;
        }

        // Evaluate event against rules
        var matchingRules = _ruleEvaluator.Evaluate(alertEvent, _cachedRules);
        if (matchingRules.Count == 0)
        {
            _logger.LogDebug("No matching rules for event {EventType}", alertEvent.EventType);
            return;
        }

        foreach (var rule in matchingRules)
        {
            try
            {
                await ProcessRuleMatchAsync(alertEvent, rule, repository, cancellationToken);
                _ruleEvaluator.RecordFired(rule, alertEvent);
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

        // Deliver to matching channels
        await DeliverAsync(alertEvent, historyEntry, repository, cancellationToken);
    }

    private async Task DeliverAsync(
        AlertEvent alertEvent,
        AlertHistoryEntry historyEntry,
        IAlertRepository repository,
        CancellationToken cancellationToken)
    {
        var channels = await repository.GetEnabledChannelsAsync(cancellationToken);
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

    private async Task RefreshRuleCacheAsync(IAlertRepository repository, CancellationToken cancellationToken)
    {
        if ((DateTime.UtcNow - _rulesCachedAt) < RuleCacheDuration)
            return;

        try
        {
            _cachedRules = await repository.GetEnabledRulesAsync(cancellationToken);
            _rulesCachedAt = DateTime.UtcNow;
            _logger.LogDebug("Refreshed alert rule cache ({Count} enabled rules)", _cachedRules.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh alert rule cache");
            // Keep using stale cache rather than failing
        }
    }

    private void CleanupCooldowns()
    {
        _cooldownTracker.Cleanup(TimeSpan.FromHours(2));
    }
}
