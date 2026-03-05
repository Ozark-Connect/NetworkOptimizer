using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using NetworkOptimizer.Core.Enums;

namespace NetworkOptimizer.Alerts.Delivery;

/// <summary>
/// Delivery channel for ntfy.sh push notifications using the JSON publishing API.
/// Supports both public ntfy.sh and self-hosted instances.
/// </summary>
public class NtfyDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<NtfyDeliveryChannel> _logger;
    private readonly HttpClient _httpClient;
    private readonly ISecretDecryptor _secretDecryptor;

    public DeliveryChannelType ChannelType => DeliveryChannelType.Ntfy;

    public NtfyDeliveryChannel(ILogger<NtfyDeliveryChannel> logger, HttpClient httpClient, ISecretDecryptor secretDecryptor)
    {
        _logger = logger;
        _httpClient = httpClient;
        _secretDecryptor = secretDecryptor;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<NtfyChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.Topic)) return false;

        var message = FormatMessage(alertEvent);

        var payload = JsonSerializer.Serialize(new
        {
            topic = config.Topic,
            title = alertEvent.Title,
            message,
            priority = MapPriority(alertEvent.Severity),
            tags = new[] { MapTag(alertEvent.Severity) },
            markdown = true
        });

        return await PostAsync(config, payload, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, DigestSummary summary, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<NtfyChannelConfig>(channel.ConfigJson);
        if (config == null || string.IsNullOrEmpty(config.Topic)) return false;

        var sb = new StringBuilder();
        sb.AppendLine($"**{summary.TotalCount} alerts** in this period");
        if (summary.CriticalCount > 0) sb.AppendLine($"- Critical: {summary.CriticalCount}");
        if (summary.ErrorCount > 0) sb.AppendLine($"- Error: {summary.ErrorCount}");
        if (summary.WarningCount > 0) sb.AppendLine($"- Warning: {summary.WarningCount}");
        if (summary.InfoCount > 0) sb.AppendLine($"- Info: {summary.InfoCount}");

        sb.AppendLine();

        foreach (var alert in alerts.OrderByDescending(a => a.Severity).Take(10))
        {
            sb.AppendLine($"- **{alert.Title}** - {alert.Source} ({TimestampFormatter.FormatLocalShort(alert.TriggeredAt)})");
        }

        if (alerts.Count > 10)
            sb.AppendLine($"\n...and {alerts.Count - 10} more alerts");

        // Use highest severity for priority
        var maxSeverity = alerts.Max(a => a.Severity);

        var payload = JsonSerializer.Serialize(new
        {
            topic = config.Topic,
            title = "Alert Digest",
            message = sb.ToString().TrimEnd(),
            priority = MapPriority(maxSeverity),
            tags = new[] { "bell" },
            markdown = true
        });

        return await PostAsync(config, payload, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<NtfyChannelConfig>(channel.ConfigJson);
            if (config == null || string.IsNullOrEmpty(config.Topic))
                return (false, "Invalid channel configuration");

            var payload = JsonSerializer.Serialize(new
            {
                topic = config.Topic,
                title = "Network Optimizer - Test",
                message = "Alert channel test successful.",
                priority = 3,
                tags = new[] { "white_check_mark" },
                markdown = true
            });

            var success = await PostAsync(config, payload, cancellationToken);
            return success ? (true, null) : (false, "ntfy POST failed");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> PostAsync(NtfyChannelConfig config, string payload, CancellationToken cancellationToken)
    {
        var url = $"{config.ServerUrl.TrimEnd('/')}";

        const int maxRetries = 2;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                // Add auth header if configured
                if (!string.IsNullOrEmpty(config.AccessToken))
                {
                    var token = _secretDecryptor.Decrypt(config.AccessToken);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                else if (!string.IsNullOrEmpty(config.Username) && !string.IsNullOrEmpty(config.Password))
                {
                    var password = _secretDecryptor.Decrypt(config.Password);
                    var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{password}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("ntfy message delivered to {Topic}", config.Topic);
                    return true;
                }

                _logger.LogWarning("ntfy POST returned {StatusCode}", response.StatusCode);
                if (attempt < maxRetries)
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                _logger.LogWarning("ntfy attempt {Attempt} failed: {Error}", attempt + 1, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deliver to ntfy");
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
            sb.AppendLine($"**Value:** {alertEvent.MetricValue}{(alertEvent.ThresholdValue.HasValue ? $" (threshold: {alertEvent.ThresholdValue})" : "")}");

        if (!string.IsNullOrEmpty(alertEvent.DeviceName))
            sb.AppendLine($"**Device:** {alertEvent.DeviceName}");

        if (!string.IsNullOrEmpty(alertEvent.DeviceIp))
            sb.AppendLine($"**IP:** {alertEvent.DeviceIp}");

        sb.AppendLine($"**Source:** {alertEvent.Source}");
        sb.AppendLine($"**Severity:** {alertEvent.Severity}");

        foreach (var ctx in alertEvent.Context)
            sb.AppendLine($"**{ctx.Key}:** {ctx.Value}");

        if (!string.IsNullOrEmpty(alertEvent.SourceUrl))
            sb.AppendLine($"**View:** {alertEvent.SourceUrl}");

        return sb.Length > 0 ? sb.ToString().TrimEnd() : alertEvent.EventType;
    }

    /// <summary>
    /// Map AlertSeverity to ntfy priority (1-5).
    /// 5=max, 4=high, 3=default, 2=low, 1=min.
    /// </summary>
    internal static int MapPriority(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => 5,
        AlertSeverity.Error => 4,
        AlertSeverity.Warning => 3,
        _ => 2
    };

    /// <summary>
    /// Map AlertSeverity to ntfy emoji shortcode tag.
    /// </summary>
    internal static string MapTag(AlertSeverity severity) => severity switch
    {
        AlertSeverity.Critical => "rotating_light",
        AlertSeverity.Error => "red_circle",
        AlertSeverity.Warning => "warning",
        _ => "information_source"
    };
}
