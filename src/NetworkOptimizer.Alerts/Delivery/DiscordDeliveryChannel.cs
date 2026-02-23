using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Discord delivery via webhook with embed formatting and severity colors.
/// </summary>
public class DiscordDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<DiscordDeliveryChannel> _logger;
    private readonly HttpClient _httpClient;

    public DeliveryChannelType ChannelType => DeliveryChannelType.Discord;

    public DiscordDeliveryChannel(ILogger<DiscordDeliveryChannel> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<DiscordChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var fields = new List<object>
        {
            new { name = "Source", value = alertEvent.Source, inline = true },
            new { name = "Severity", value = alertEvent.Severity.ToString(), inline = true }
        };

        if (!string.IsNullOrEmpty(alertEvent.DeviceName))
            fields.Add(new { name = "Device", value = $"{alertEvent.DeviceName}{(alertEvent.DeviceIp != null ? $" ({alertEvent.DeviceIp})" : "")}", inline = true });

        if (alertEvent.MetricValue.HasValue)
            fields.Add(new { name = "Value", value = $"{alertEvent.MetricValue}{(alertEvent.ThresholdValue.HasValue ? $" / {alertEvent.ThresholdValue}" : "")}", inline = true });

        foreach (var ctx in alertEvent.Context)
            fields.Add(new { name = ctx.Key, value = ctx.Value, inline = true });

        var payload = JsonSerializer.Serialize(new
        {
            embeds = new[]
            {
                new
                {
                    title = alertEvent.Title,
                    description = alertEvent.Message,
                    color = GetSeverityColorInt(alertEvent.Severity),
                    fields,
                    timestamp = alertEvent.Timestamp.ToString("o"),
                    footer = new { text = "Network Optimizer" }
                }
            }
        });

        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<DiscordChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var description = new StringBuilder();
        description.AppendLine($"**{alerts.Count}** alerts in this period");
        description.AppendLine();

        var criticalCount = alerts.Count(a => a.Severity == AlertSeverity.Critical);
        var errorCount = alerts.Count(a => a.Severity == AlertSeverity.Error);
        if (criticalCount > 0) description.AppendLine($":red_circle: **{criticalCount}** critical");
        if (errorCount > 0) description.AppendLine($":orange_circle: **{errorCount}** error");
        description.AppendLine();

        foreach (var alert in alerts.OrderByDescending(a => a.Severity).Take(10))
        {
            description.AppendLine($"- **{alert.Title}** ({alert.Source}) - {alert.TriggeredAt:HH:mm UTC}");
        }

        if (alerts.Count > 10)
            description.AppendLine($"_...and {alerts.Count - 10} more_");

        var payload = JsonSerializer.Serialize(new
        {
            embeds = new[]
            {
                new
                {
                    title = "Alert Digest",
                    description = description.ToString(),
                    color = 0x0559C9,
                    timestamp = DateTime.UtcNow.ToString("o"),
                    footer = new { text = "Network Optimizer" }
                }
            }
        });

        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<DiscordChannelConfig>(channel.ConfigJson);
            if (config == null || string.IsNullOrEmpty(config.WebhookUrl))
                return (false, "Invalid channel configuration");

            var payload = JsonSerializer.Serialize(new
            {
                embeds = new[]
                {
                    new
                    {
                        title = "Network Optimizer - Test Alert",
                        description = "Alert channel test successful. You will receive notifications here.",
                        color = 0x24bc70,
                        timestamp = DateTime.UtcNow.ToString("o"),
                        footer = new { text = "Network Optimizer" }
                    }
                }
            });

            var success = await PostAsync(config.WebhookUrl, payload, cancellationToken);
            return success ? (true, null) : (false, "Discord webhook POST failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> PostAsync(string url, string payload, CancellationToken cancellationToken)
    {
        const int maxRetries = 2;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var response = await _httpClient.PostAsync(url,
                    new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Discord message delivered");
                    return true;
                }

                _logger.LogWarning("Discord webhook returned {StatusCode}", response.StatusCode);
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("Discord attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver to Discord");
                return false;
            }
        }

        return false;
    }

    private static int GetSeverityColorInt(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => 0xef4444,
        AlertSeverity.Error => 0xee6368,
        AlertSeverity.Warning => 0xe79613,
        _ => 0x4797ff
    };
}
