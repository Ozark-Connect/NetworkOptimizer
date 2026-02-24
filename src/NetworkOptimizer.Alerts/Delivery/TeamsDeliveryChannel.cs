using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Microsoft Teams delivery via webhook with Adaptive Card formatting.
/// </summary>
public class TeamsDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<TeamsDeliveryChannel> _logger;
    private readonly HttpClient _httpClient;

    public DeliveryChannelType ChannelType => DeliveryChannelType.Teams;

    public TeamsDeliveryChannel(ILogger<TeamsDeliveryChannel> logger, HttpClient httpClient)
    {
        _logger = logger;
        _httpClient = httpClient;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<TeamsChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var facts = new List<object>
        {
            new { title = "Source", value = alertEvent.Source },
            new { title = "Severity", value = alertEvent.Severity.ToString() },
            new { title = "Event Type", value = alertEvent.EventType }
        };

        if (!string.IsNullOrEmpty(alertEvent.DeviceName))
            facts.Add(new { title = "Device", value = $"{alertEvent.DeviceName}{(alertEvent.DeviceIp != null ? $" ({alertEvent.DeviceIp})" : "")}" });

        if (alertEvent.MetricValue.HasValue)
            facts.Add(new { title = "Value", value = $"{alertEvent.MetricValue}{(alertEvent.ThresholdValue.HasValue ? $" / threshold: {alertEvent.ThresholdValue}" : "")}" });

        foreach (var ctx in alertEvent.Context)
            facts.Add(new { title = ctx.Key, value = ctx.Value });

        var cardBody = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = alertEvent.Title,
                weight = "Bolder",
                size = "Large",
                color = GetAdaptiveCardColor(alertEvent.Severity)
            }
        };

        if (!string.IsNullOrEmpty(alertEvent.Message))
        {
            cardBody.Add(new
            {
                type = "TextBlock",
                text = alertEvent.Message,
                wrap = true
            });
        }

        cardBody.Add(new
        {
            type = "FactSet",
            facts
        });

        var payload = BuildAdaptiveCardPayload(cardBody);
        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, DigestSummary summary, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<TeamsChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.WebhookUrl)) return false;

        var summaryParts = new List<string>();
        if (summary.CriticalCount > 0) summaryParts.Add($"**{summary.CriticalCount}** critical");
        if (summary.ErrorCount > 0) summaryParts.Add($"**{summary.ErrorCount}** error");
        if (summary.WarningCount > 0) summaryParts.Add($"**{summary.WarningCount}** warning");

        var cardBody = new List<object>
        {
            new { type = "TextBlock", text = "Alert Digest", weight = "Bolder", size = "Large" },
            new { type = "TextBlock", text = $"**{summary.TotalCount}** alerts: {string.Join(", ", summaryParts)}", wrap = true }
        };

        foreach (var alert in alerts.OrderByDescending(a => a.Severity).Take(10))
        {
            cardBody.Add(new
            {
                type = "TextBlock",
                text = $"- **{alert.Title}** ({alert.Source}) - {TimestampFormatter.FormatLocalShort(alert.TriggeredAt)}",
                wrap = true,
                spacing = "Small"
            });
        }

        if (alerts.Count > 10)
        {
            cardBody.Add(new { type = "TextBlock", text = $"_...and {alerts.Count - 10} more alerts_", isSubtle = true });
        }

        var payload = BuildAdaptiveCardPayload(cardBody);
        return await PostAsync(config.WebhookUrl, payload, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<TeamsChannelConfig>(channel.ConfigJson);
            if (config == null || string.IsNullOrEmpty(config.WebhookUrl))
                return (false, "Invalid channel configuration");

            var cardBody = new List<object>
            {
                new { type = "TextBlock", text = "Network Optimizer - Test Alert", weight = "Bolder", size = "Large", color = "Good" },
                new { type = "TextBlock", text = "Alert channel test successful. You will receive notifications here.", wrap = true }
            };

            var payload = BuildAdaptiveCardPayload(cardBody);
            var success = await PostAsync(config.WebhookUrl, payload, cancellationToken);
            return success ? (true, null) : (false, "Teams webhook POST failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string BuildAdaptiveCardPayload(List<object> cardBody)
    {
        return JsonSerializer.Serialize(new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = cardBody
                    }
                }
            }
        });
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
                    _logger.LogDebug("Teams message delivered");
                    return true;
                }

                _logger.LogWarning("Teams webhook returned {StatusCode}", response.StatusCode);
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("Teams attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver to Teams");
                return false;
            }
        }

        return false;
    }

    private static string GetAdaptiveCardColor(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "Attention",
        AlertSeverity.Error => "Attention",
        AlertSeverity.Warning => "Warning",
        _ => "Accent"
    };
}
