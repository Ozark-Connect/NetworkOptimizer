using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;
using Scriban;

namespace NetworkOptimizer.Alerts.Delivery;

public class WebhookDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<WebhookDeliveryChannel> _logger;
    private readonly HttpClient _httpClient;
    private readonly ISecretDecryptor _secretDecryptor;

    public DeliveryChannelType ChannelType => DeliveryChannelType.Webhook;

    public WebhookDeliveryChannel(ILogger<WebhookDeliveryChannel> logger, HttpClient httpClient, ISecretDecryptor secretDecryptor)
    {
        _logger = logger;
        _httpClient = httpClient;
        _secretDecryptor = secretDecryptor;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<WebhookChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.Url)) return false;

        string payload;
        if (!string.IsNullOrEmpty(config.PayloadTemplate))
        {
            var template = Template.Parse(config.PayloadTemplate);
            payload = await template.RenderAsync(BuildTemplateModel(alertEvent));
        }
        else
        {
            payload = JsonSerializer.Serialize(new
            {
                event_type = alertEvent.EventType,
                severity = alertEvent.Severity.ToString().ToLowerInvariant(),
                source = alertEvent.Source,
                title = alertEvent.Title,
                message = alertEvent.Message,
                device_id = alertEvent.DeviceId,
                device_name = alertEvent.DeviceName,
                device_ip = alertEvent.DeviceIp,
                metric_value = alertEvent.MetricValue,
                threshold_value = alertEvent.ThresholdValue,
                context = alertEvent.Context,
                tags = alertEvent.Tags,
                timestamp = alertEvent.Timestamp.ToString("o"),
                alert_id = historyEntry.Id
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }

        return await PostWithRetryAsync(config, payload, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<WebhookChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.Url)) return false;

        var payload = JsonSerializer.Serialize(new
        {
            type = "digest",
            total_count = alerts.Count,
            critical_count = alerts.Count(a => a.Severity == AlertSeverity.Critical),
            error_count = alerts.Count(a => a.Severity == AlertSeverity.Error),
            warning_count = alerts.Count(a => a.Severity == AlertSeverity.Warning),
            alerts = alerts.Select(a => new
            {
                title = a.Title,
                severity = a.Severity.ToString().ToLowerInvariant(),
                source = a.Source,
                triggered_at = a.TriggeredAt.ToString("o")
            }),
            generated_at = DateTime.UtcNow.ToString("o")
        }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });

        return await PostWithRetryAsync(config, payload, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<WebhookChannelConfig>(channel.ConfigJson);
            if (config == null || string.IsNullOrEmpty(config.Url))
                return (false, "Invalid channel configuration");

            var payload = JsonSerializer.Serialize(new
            {
                type = "test",
                title = "Network Optimizer - Test Alert",
                message = "This is a test webhook from the Network Optimizer alert system.",
                timestamp = DateTime.UtcNow.ToString("o")
            });

            var success = await PostWithRetryAsync(config, payload, cancellationToken);
            return success ? (true, null) : (false, "Webhook POST failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> PostWithRetryAsync(WebhookChannelConfig config, string payload, CancellationToken cancellationToken)
    {
        const int maxRetries = 2;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, config.Url);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                // Add custom headers
                foreach (var header in config.Headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                // Add HMAC signature if secret is configured
                if (!string.IsNullOrEmpty(config.Secret))
                {
                    var secret = _secretDecryptor.Decrypt(config.Secret);
                    var signature = ComputeHmacSha256(payload, secret);
                    request.Headers.TryAddWithoutValidation("X-Signature-256", $"sha256={signature}");
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Webhook delivered to {Url}", config.Url);
                    return true;
                }

                _logger.LogWarning("Webhook POST to {Url} returned {StatusCode}", config.Url, response.StatusCode);
                if (attempt < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
                }
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("Webhook attempt {Attempt} failed, retrying: {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver webhook after {MaxRetries} retries", maxRetries + 1);
                return false;
            }
        }

        return false;
    }

    internal static string ComputeHmacSha256(string payload, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexStringLower(hash);
    }

    private static object BuildTemplateModel(AlertEvent alertEvent) => new
    {
        event_type = alertEvent.EventType,
        severity = alertEvent.Severity.ToString(),
        source = alertEvent.Source,
        title = alertEvent.Title,
        message = alertEvent.Message,
        device_id = alertEvent.DeviceId,
        device_name = alertEvent.DeviceName,
        device_ip = alertEvent.DeviceIp,
        metric_value = alertEvent.MetricValue,
        threshold_value = alertEvent.ThresholdValue,
        context = alertEvent.Context,
        tags = alertEvent.Tags,
        timestamp = alertEvent.Timestamp.ToString("o")
    };
}
