using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Interfaces;
using NetworkOptimizer.Alerts.Models;

namespace NetworkOptimizer.Alerts;

/// <summary>
/// Background service that sends periodic digest summaries of alerts
/// to channels with digest enabled.
/// </summary>
public class DigestService : BackgroundService
{
    private readonly ILogger<DigestService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<IAlertDeliveryChannel> _deliveryChannels;
    private readonly IScheduleSiteContext? _siteContext;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    // In-memory cache backed by persistent store via IDigestStateStore. Keyed by site + channel
    // ("{siteKey}:{channelId}") because channel IDs are per-site-DB and would otherwise collide.
    private readonly Dictionary<string, DateTime> _lastDigestSent = new();
    private readonly HashSet<string> _stateLoadedSites = new();

    /// <summary>
    /// Maximum number of individually listed alerts per source group before collapsing.
    /// </summary>
    private const int CollapseThreshold = 10;

    public DigestService(
        ILogger<DigestService> logger,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAlertDeliveryChannel> deliveryChannels,
        IScheduleSiteContext? siteContext = null)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _deliveryChannels = deliveryChannels;
        _siteContext = siteContext;
    }

    private string DefaultSiteKey => _siteContext?.DefaultKey ?? "";

    private static string CacheKey(string siteKey, int channelId) => $"{siteKey}:{channelId}";

    private async Task<IReadOnlyList<string>> GetSiteKeysAsync(CancellationToken ct)
    {
        if (_siteContext == null)
            return new[] { DefaultSiteKey };
        try
        {
            var keys = await _siteContext.GetSiteKeysAsync(ct);
            return keys.Count > 0 ? keys : new[] { DefaultSiteKey };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Site enumeration failed; sending the default site's digests only");
            return new[] { DefaultSiteKey };
        }
    }

    private IServiceScope CreatePinnedScope(string siteKey)
    {
        var scope = _scopeFactory.CreateScope();
        _siteContext?.PinScope(scope, siteKey);
        return scope;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Digest service started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                await CheckAndSendDigestsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in digest check cycle");
            }
        }

        _logger.LogInformation("Digest service stopped");
    }

    private async Task CheckAndSendDigestsAsync(CancellationToken cancellationToken)
    {
        // Each site's digest goes to that site's own digest channels. Immediate alerts also
        // reach the global (main) channels, but a digest summarizes the alerts of the site its
        // channel belongs to, so it stays site-local (and channel IDs are per-site anyway).
        foreach (var siteKey in await GetSiteKeysAsync(cancellationToken))
        {
            if (cancellationToken.IsCancellationRequested) break;
            try
            {
                await CheckSiteDigestsAsync(siteKey, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process digests for site {Site}", siteKey.Length == 0 ? "main" : siteKey);
            }
        }
    }

    private async Task CheckSiteDigestsAsync(string siteKey, CancellationToken cancellationToken)
    {
        using var scope = CreatePinnedScope(siteKey);
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();
        var stateStore = scope.ServiceProvider.GetRequiredService<IDigestStateStore>();

        var channels = await repository.GetEnabledChannelsAsync(cancellationToken);

        // Load persisted state for this site once (survives restarts)
        if (_stateLoadedSites.Add(siteKey))
            await LoadPersistedStateAsync(siteKey, channels, stateStore, cancellationToken);

        var digestChannels = channels.Where(c => c.DigestEnabled && !string.IsNullOrEmpty(c.DigestSchedule)).ToList();

        foreach (var channel in digestChannels)
        {
            try
            {
                if (!IsDue(siteKey, channel))
                    continue;

                var since = GetDigestWindowStart(channel);
                var alerts = await repository.GetAlertsForDigestAsync(since, cancellationToken);

                if (alerts.Count == 0)
                {
                    _logger.LogDebug("No alerts for digest on channel {ChannelId} (site {Site})", channel.Id, siteKey.Length == 0 ? "main" : siteKey);
                    await MarkSentAsync(stateStore, siteKey, channel.Id, cancellationToken);
                    continue;
                }

                var handler = _deliveryChannels.FirstOrDefault(d => d.ChannelType == channel.ChannelType);
                if (handler == null) continue;

                // Compute summary from original alerts, then collapse for display
                var summary = DigestSummary.FromAlerts(alerts);
                var collapsedAlerts = CollapseAlerts(alerts);

                var success = await handler.SendDigestAsync(collapsedAlerts, channel, summary, cancellationToken);
                if (success)
                {
                    await MarkSentAsync(stateStore, siteKey, channel.Id, cancellationToken);
                    _logger.LogInformation("Sent digest with {Count} alerts ({Collapsed} after collapsing) to channel {ChannelId} ({Name}) for site {Site}",
                        alerts.Count, collapsedAlerts.Count, channel.Id, channel.Name, siteKey.Length == 0 ? "main" : siteKey);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send digest to channel {ChannelId}", channel.Id);
            }
        }
    }

    /// <summary>
    /// Load last-sent timestamps from the persistent store into the in-memory cache for one site.
    /// </summary>
    private async Task LoadPersistedStateAsync(string siteKey, IReadOnlyList<Models.DeliveryChannel> channels, IDigestStateStore stateStore, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = 0;
            foreach (var channel in channels.Where(c => c.DigestEnabled))
            {
                var lastSent = await stateStore.GetLastSentAsync(channel.Id, cancellationToken);
                if (lastSent.HasValue)
                {
                    _lastDigestSent[CacheKey(siteKey, channel.Id)] = lastSent.Value;
                    loaded++;
                }
            }
            _logger.LogDebug("Loaded persisted digest state for {Count} channels (site {Site})", loaded, siteKey.Length == 0 ? "main" : siteKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load persisted digest state, will use defaults");
        }
    }

    private async Task MarkSentAsync(IDigestStateStore stateStore, string siteKey, int channelId, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _lastDigestSent[CacheKey(siteKey, channelId)] = now;
        try
        {
            await stateStore.SetLastSentAsync(channelId, now, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist digest sent time for channel {ChannelId}", channelId);
        }
    }

    private bool IsDue(string siteKey, Models.DeliveryChannel channel)
    {
        var schedule = channel.DigestSchedule ?? "daily:08:00";
        var parts = schedule.Split(':');
        var now = DateTime.UtcNow;

        _lastDigestSent.TryGetValue(CacheKey(siteKey, channel.Id), out var lastSent);

        if (parts[0] == "daily")
        {
            // "daily:HH:MM" - send once per day at specified hour
            if (parts.Length >= 3 && int.TryParse(parts[1], out var hour) && int.TryParse(parts[2], out var minute))
            {
                var targetToday = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0, DateTimeKind.Utc);
                return now >= targetToday && lastSent < targetToday;
            }
            // Fallback: daily at 08:00 UTC
            var defaultTarget = new DateTime(now.Year, now.Month, now.Day, 8, 0, 0, DateTimeKind.Utc);
            return now >= defaultTarget && lastSent < defaultTarget;
        }

        if (parts[0] == "weekly")
        {
            // "weekly:dayofweek:HH:MM" - e.g., "weekly:monday:08:00"
            if (parts.Length >= 4 &&
                Enum.TryParse<DayOfWeek>(parts[1], true, out var day) &&
                int.TryParse(parts[2], out var hour) &&
                int.TryParse(parts[3], out var minute))
            {
                // Find the most recent occurrence of the target day
                var daysSinceTarget = ((int)now.DayOfWeek - (int)day + 7) % 7;
                var targetDate = now.Date.AddDays(-daysSinceTarget);
                var targetTime = new DateTime(targetDate.Year, targetDate.Month, targetDate.Day, hour, minute, 0, DateTimeKind.Utc);

                if (now.DayOfWeek == day && now >= targetTime && lastSent < targetTime)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Collapse duplicate alerts to keep digests concise.
    /// - For source groups with more than CollapseThreshold entries: collapse identical
    ///   alerts (same title + severity) into a single entry with a count suffix.
    /// - For Info severity: collapse by EventType regardless of title/device differences,
    ///   so noisy low-severity alerts don't dominate the digest.
    /// </summary>
    private static IReadOnlyList<AlertHistoryEntry> CollapseAlerts(List<AlertHistoryEntry> alerts)
    {
        var result = new List<AlertHistoryEntry>();

        foreach (var sourceGroup in alerts.GroupBy(a => a.Source))
        {
            // Split into Info (always collapse aggressively) and non-Info (collapse only when large)
            var infoAlerts = sourceGroup.Where(a => a.Severity == Core.Enums.AlertSeverity.Info).ToList();
            var nonInfoAlerts = sourceGroup.Where(a => a.Severity != Core.Enums.AlertSeverity.Info).ToList();

            // Info alerts: always collapse by EventType (ignoring title/device differences)
            foreach (var eventGroup in infoAlerts.GroupBy(a => a.EventType))
            {
                var count = eventGroup.Count();
                if (count <= 1)
                {
                    result.Add(eventGroup.First());
                }
                else
                {
                    var representative = eventGroup.OrderByDescending(a => a.TriggeredAt).First();
                    result.Add(CreateCollapsed(representative, count));
                }
            }

            // Non-Info alerts: collapse by title+severity only when group is large
            if (nonInfoAlerts.Count <= CollapseThreshold)
            {
                result.AddRange(nonInfoAlerts);
            }
            else
            {
                foreach (var titleGroup in nonInfoAlerts.GroupBy(a => (a.Title, a.Severity)))
                {
                    var count = titleGroup.Count();
                    if (count <= 1)
                    {
                        result.Add(titleGroup.First());
                    }
                    else
                    {
                        var representative = titleGroup.OrderByDescending(a => a.TriggeredAt).First();
                        result.Add(CreateCollapsed(representative, count));
                    }
                }
            }
        }

        return result;
    }

    private static AlertHistoryEntry CreateCollapsed(AlertHistoryEntry representative, int count)
    {
        return new AlertHistoryEntry
        {
            Id = representative.Id,
            EventType = representative.EventType,
            Severity = representative.Severity,
            Status = representative.Status,
            Source = representative.Source,
            Title = $"{representative.Title} ({count}x)",
            Message = representative.Message,
            TriggeredAt = representative.TriggeredAt,
            DeviceId = representative.DeviceId,
            DeviceName = representative.DeviceName,
            DeviceIp = representative.DeviceIp,
            RuleId = representative.RuleId,
            IncidentId = representative.IncidentId,
            ContextJson = representative.ContextJson
        };
    }

    private DateTime GetDigestWindowStart(Models.DeliveryChannel channel)
    {
        var schedule = channel.DigestSchedule ?? "daily:08:00";
        return schedule.StartsWith("weekly")
            ? DateTime.UtcNow.AddDays(-7)
            : DateTime.UtcNow.AddDays(-1);
    }
}
