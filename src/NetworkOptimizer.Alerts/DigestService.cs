using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Delivery;
using NetworkOptimizer.Alerts.Interfaces;

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
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    // Track last digest sent per channel (in-memory; also persisted via SystemSettings for durability)
    private readonly Dictionary<int, DateTime> _lastDigestSent = new();

    public DigestService(
        ILogger<DigestService> logger,
        IServiceScopeFactory scopeFactory,
        IEnumerable<IAlertDeliveryChannel> deliveryChannels)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _deliveryChannels = deliveryChannels;
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
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAlertRepository>();

        var channels = await repository.GetEnabledChannelsAsync(cancellationToken);
        var digestChannels = channels.Where(c => c.DigestEnabled && !string.IsNullOrEmpty(c.DigestSchedule)).ToList();

        foreach (var channel in digestChannels)
        {
            try
            {
                if (!IsDue(channel))
                    continue;

                var since = GetDigestWindowStart(channel);
                var alerts = await repository.GetAlertsForDigestAsync(since, cancellationToken);

                if (alerts.Count == 0)
                {
                    _logger.LogDebug("No alerts for digest on channel {ChannelId}", channel.Id);
                    _lastDigestSent[channel.Id] = DateTime.UtcNow;
                    continue;
                }

                var handler = _deliveryChannels.FirstOrDefault(d => d.ChannelType == channel.ChannelType);
                if (handler == null) continue;

                var success = await handler.SendDigestAsync(alerts, channel, cancellationToken);
                if (success)
                {
                    _lastDigestSent[channel.Id] = DateTime.UtcNow;
                    _logger.LogInformation("Sent digest with {Count} alerts to channel {ChannelId} ({Name})",
                        alerts.Count, channel.Id, channel.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send digest to channel {ChannelId}", channel.Id);
            }
        }
    }

    private bool IsDue(Models.DeliveryChannel channel)
    {
        var schedule = channel.DigestSchedule ?? "daily:08:00";
        var parts = schedule.Split(':');
        var now = DateTime.UtcNow;

        _lastDigestSent.TryGetValue(channel.Id, out var lastSent);

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
                // Find the most recent target day
                var daysUntilTarget = ((int)day - (int)now.DayOfWeek + 7) % 7;
                var targetDate = now.Date.AddDays(-daysUntilTarget);
                var targetTime = new DateTime(targetDate.Year, targetDate.Month, targetDate.Day, hour, minute, 0, DateTimeKind.Utc);

                if (now.DayOfWeek == day && now >= targetTime && lastSent < targetTime)
                    return true;
            }
        }

        return false;
    }

    private DateTime GetDigestWindowStart(Models.DeliveryChannel channel)
    {
        var schedule = channel.DigestSchedule ?? "daily:08:00";
        return schedule.StartsWith("weekly")
            ? DateTime.UtcNow.AddDays(-7)
            : DateTime.UtcNow.AddDays(-1);
    }
}
