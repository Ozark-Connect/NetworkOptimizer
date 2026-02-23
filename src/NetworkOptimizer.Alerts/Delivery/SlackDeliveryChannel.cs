using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Slack delivery via incoming webhook with Block Kit formatting.
/// </summary>
public class SlackDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<SlackDeliveryChannel> _logger;
    private readonly HttpClient _httpClient;

    public DeliveryChannelType ChannelType => DeliveryChannelType.Slack;

    public SlackDeliveryChannel(ILogger<SlackDeliveryChannel> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<SlackChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var emoji = GetSeverityEmoji(alertEvent.Severity);
        var color = GetSeverityColor(alertEvent.Severity);

        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = $"{emoji} {alertEvent.Title}" } },
            new { type = "section", text = new { type = "mrkdwn", text = FormatMessage(alertEvent) } }
        };

        // Context block with metadata
        var contextElements = new List<object>
        {
            new { type = "mrkdwn", text = $"*Source:* {alertEvent.Source}" },
            new { type = "mrkdwn", text = $"*Severity:* {alertEvent.Severity}" }
        };

        if (!string.IsNullOrEmpty(alertEvent.DeviceName))
            contextElements.Add(new { type = "mrkdwn", text = $"*Device:* {alertEvent.DeviceName}" });

        blocks.Add(new { type = "context", elements = contextElements });

        var payload = JsonSerializer.Serialize(new
        {
            blocks,
            attachments = new[]
            {
                new { color, blocks = Array.Empty<object>() }
            }
        });

        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<SlackChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var criticalCount = alerts.Count(a => a.Severity == AlertSeverity.Critical);
        var errorCount = alerts.Count(a => a.Severity == AlertSeverity.Error);
        var warningCount = alerts.Count(a => a.Severity == AlertSeverity.Warning);

        var summary = new StringBuilder($"*{alerts.Count} alerts* in this period");
        if (criticalCount > 0) summary.Append($" | :red_circle: {criticalCount} critical");
        if (errorCount > 0) summary.Append($" | :large_orange_circle: {errorCount} error");
        if (warningCount > 0) summary.Append($" | :warning: {warningCount} warning");

        var blocks = new List<object>
        {
            new { type = "header", text = new { type = "plain_text", text = "Alert Digest" } },
            new { type = "section", text = new { type = "mrkdwn", text = summary.ToString() } },
            new { type = "divider" }
        };

        // Top alerts
        foreach (var alert in alerts.OrderByDescending(a => a.Severity).Take(10))
        {
            var emoji = GetSeverityEmoji(alert.Severity);
            blocks.Add(new
            {
                type = "section",
                text = new { type = "mrkdwn", text = $"{emoji} *{alert.Title}*\n_{alert.Source}_ - {alert.TriggeredAt:HH:mm UTC}" }
            });
        }

        if (alerts.Count > 10)
        {
            blocks.Add(new { type = "context", elements = new[] { new { type = "mrkdwn", text = $"_...and {alerts.Count - 10} more alerts_" } } });
        }

        var payload = JsonSerializer.Serialize(new { blocks });
        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<SlackChannelConfig>(channel.ConfigJson);
            if (config == null || string.IsNullOrEmpty(config.WebhookUrl))
                return (false, "Invalid channel configuration");

            var payload = JsonSerializer.Serialize(new
            {
                blocks = new object[]
                {
                    new { type = "section", text = new { type = "mrkdwn", text = ":white_check_mark: *Network Optimizer* - Alert channel test successful" } }
                }
            });

            var success = await PostAsync(config.WebhookUrl, payload, cancellationToken);
            return success ? (true, null) : (false, "Slack webhook POST failed");
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
                    _logger.LogDebug("Slack message delivered");
                    return true;
                }

                _logger.LogWarning("Slack webhook returned {StatusCode}", response.StatusCode);
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("Slack attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver to Slack");
                return false;
            }
        }

        return false;
    }

    private static string FormatMessage(AlertEvent alertEvent)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(alertEvent.Message))
            sb.AppendLine(alertEvent.Message);

        if (alertEvent.MetricValue.HasValue)
            sb.AppendLine($"*Value:* {alertEvent.MetricValue}{(alertEvent.ThresholdValue.HasValue ? $" (threshold: {alertEvent.ThresholdValue})" : "")}");

        if (!string.IsNullOrEmpty(alertEvent.DeviceIp))
            sb.AppendLine($"*IP:* `{alertEvent.DeviceIp}`");

        foreach (var ctx in alertEvent.Context)
            sb.AppendLine($"*{ctx.Key}:* {ctx.Value}");

        return sb.Length > 0 ? sb.ToString().TrimEnd() : alertEvent.EventType;
    }

    private static string GetSeverityEmoji(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => ":rotating_light:",
        AlertSeverity.Error => ":red_circle:",
        AlertSeverity.Warning => ":warning:",
        _ => ":information_source:"
    };

    private static string GetSeverityColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "#ef4444",
        AlertSeverity.Error => "#ee6368",
        AlertSeverity.Warning => "#e79613",
        _ => "#4797ff"
    };
}
