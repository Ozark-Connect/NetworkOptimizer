using System.Reflection;
using System.Text.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Alerts.Models;
using Scriban;

namespace NetworkOptimizer.Alerts.Delivery;

public class EmailDeliveryChannel : IAlertDeliveryChannel
{
    private readonly ILogger<EmailDeliveryChannel> _logger;
    private readonly ISecretDecryptor _secretDecryptor;
    private static readonly Lazy<string> AlertTemplate = new(LoadTemplate("alert-email.html"));
    private static readonly Lazy<string> DigestTemplate = new(LoadTemplate("digest-email.html"));

    public DeliveryChannelType ChannelType => DeliveryChannelType.Email;

    public EmailDeliveryChannel(ILogger<EmailDeliveryChannel> logger, ISecretDecryptor secretDecryptor)
    {
        _logger = logger;
        _secretDecryptor = secretDecryptor;
    }

    public async Task<bool> SendAsync(AlertEvent alertEvent, AlertHistoryEntry historyEntry, DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<EmailChannelConfig>(channel.ConfigJson);
        if (config == null) return false;

        var template = Template.Parse(AlertTemplate.Value);
        var body = await template.RenderAsync(new
        {
            title = alertEvent.Title,
            message = alertEvent.Message,
            severity = alertEvent.Severity.ToString(),
            severity_color = GetSeverityColor(alertEvent.Severity),
            source = alertEvent.Source,
            event_type = alertEvent.EventType,
            device_name = alertEvent.DeviceName,
            device_ip = alertEvent.DeviceIp,
            timestamp = alertEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC"),
            context = alertEvent.Context,
            metric_value = alertEvent.MetricValue,
            threshold_value = alertEvent.ThresholdValue
        });

        return await SendEmailAsync(config, $"[{alertEvent.Severity}] {alertEvent.Title}", body, cancellationToken);
    }

    public async Task<bool> SendDigestAsync(IReadOnlyList<AlertHistoryEntry> alerts, DeliveryChannel channel, DigestSummary summary, CancellationToken cancellationToken = default)
    {
        var config = JsonSerializer.Deserialize<EmailChannelConfig>(channel.ConfigJson);
        if (config == null) return false;

        var grouped = alerts.GroupBy(a => a.Source).Select(g => new
        {
            source = g.Key,
            count = summary.SourceCounts.GetValueOrDefault(g.Key, g.Count()),
            alerts = g.OrderByDescending(a => a.Severity).ThenByDescending(a => a.TriggeredAt).Select(a => new
            {
                title = a.Title,
                severity = a.Severity.ToString(),
                severity_color = GetSeverityColor(a.Severity),
                triggered_at = a.TriggeredAt.ToString("yyyy-MM-dd HH:mm:ss UTC")
            }).ToList()
        }).ToList();

        var template = Template.Parse(DigestTemplate.Value);
        var body = await template.RenderAsync(new
        {
            total_count = summary.TotalCount,
            critical_count = summary.CriticalCount,
            error_count = summary.ErrorCount,
            warning_count = summary.WarningCount,
            info_count = summary.InfoCount,
            groups = grouped,
            generated_at = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC")
        });

        return await SendEmailAsync(config, $"Alert Digest - {summary.TotalCount} alerts", body, cancellationToken);
    }

    public async Task<(bool Success, string? Error)> TestAsync(DeliveryChannel channel, CancellationToken cancellationToken = default)
    {
        try
        {
            var config = JsonSerializer.Deserialize<EmailChannelConfig>(channel.ConfigJson);
            if (config == null) return (false, "Invalid channel configuration");

            if (string.IsNullOrWhiteSpace(config.SmtpHost))
                return (false, "SMTP host is not configured");
            if (string.IsNullOrWhiteSpace(config.FromAddress) || string.IsNullOrWhiteSpace(config.ToAddresses))
                return (false, "From and To addresses are required");
            if (!string.IsNullOrEmpty(config.Username) && string.IsNullOrWhiteSpace(_secretDecryptor.Decrypt(config.Password)))
                return (false, "SMTP username is configured but password is empty. Either set a password or clear the username for unauthenticated relay.");

            var body = "<html><body style='background:#1a2029;color:#f1f5f9;padding:24px;font-family:sans-serif;'>" +
                       "<h2>Network Optimizer Alert Test</h2>" +
                       "<p>This is a test message from the Network Optimizer alert system.</p>" +
                       "<p>If you received this email, your alert channel is configured correctly.</p>" +
                       "</body></html>";

            // Use a short timeout for test - no retries, fail fast
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var (success, error) = await SendEmailCoreAsync(config, "Network Optimizer - Test Alert", body, cts.Token);
            return (success, error);
        }
        catch (OperationCanceledException)
        {
            return (false, "Connection timed out after 15 seconds. Check SMTP host, port, and SSL settings.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private async Task<bool> SendEmailAsync(EmailChannelConfig config, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        const int maxRetries = 2;
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                var (success, error) = await SendEmailCoreAsync(config, subject, htmlBody, cancellationToken);
                if (success) return true;

                if (attempt < maxRetries)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    _logger.LogWarning("Email send attempt {Attempt} failed, retrying in {Delay}s: {Error}",
                        attempt + 1, delay.TotalSeconds, error);
                    await Task.Delay(delay, cancellationToken);
                }
                else
                {
                    _logger.LogError("Failed to send email after {MaxRetries} retries: {Error}", maxRetries + 1, error);
                }
            }
            catch (AuthenticationException ex)
            {
                // Don't retry auth failures - retrying bad credentials just gets you fail2banned
                _logger.LogError("SMTP authentication failed (not retrying): {Error}", ex.Message);
                return false;
            }
            catch (Exception ex) when (attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _logger.LogWarning("Email send attempt {Attempt} failed, retrying in {Delay}s: {Error}",
                    attempt + 1, delay.TotalSeconds, ex.Message);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send email after {MaxRetries} retries", maxRetries + 1);
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Single attempt to send an email. Returns (success, errorMessage).
    /// </summary>
    private async Task<(bool Success, string? Error)> SendEmailCoreAsync(EmailChannelConfig config, string subject, string htmlBody, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.FromAddress) || string.IsNullOrWhiteSpace(config.ToAddresses))
            return (false, "Missing from/to address configuration");

        // If username is set but password is empty, that's a misconfiguration - not an
        // intentional no-auth relay. Fail before connecting to avoid hammering the server
        // with unauthenticated attempts (which can trigger fail2ban).
        if (!string.IsNullOrEmpty(config.Username) && string.IsNullOrWhiteSpace(_secretDecryptor.Decrypt(config.Password)))
            return (false, "SMTP username is configured but password is empty. Either set a password or clear the username for unauthenticated relay.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(config.FromName, config.FromAddress));

        foreach (var addr in config.ToAddresses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            message.To.Add(MailboxAddress.Parse(addr));
        }

        message.Subject = subject;
        message.Body = new TextPart("html") { Text = htmlBody };

        using var client = new SmtpClient();
        client.Timeout = 30_000; // 30 second timeout for connect/send operations

        // Determine SSL mode based on port and UseSsl setting
        var sslOptions = GetSecureSocketOptions(config);
        await client.ConnectAsync(config.SmtpHost, config.SmtpPort, sslOptions, cancellationToken);

        if (!string.IsNullOrEmpty(config.Username))
        {
            var password = _secretDecryptor.Decrypt(config.Password);
            await client.AuthenticateAsync(config.Username, password, cancellationToken);
        }

        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);

        _logger.LogDebug("Email sent: {Subject}", subject);
        return (true, null);
    }

    private static SecureSocketOptions GetSecureSocketOptions(EmailChannelConfig config)
    {
        if (!config.UseSsl) return SecureSocketOptions.None;         // Port 25
        if (config.UseStartTls) return SecureSocketOptions.StartTls; // Port 587
        return SecureSocketOptions.SslOnConnect;                      // Port 465
    }

    private static string GetSeverityColor(Core.Enums.AlertSeverity severity) => severity switch
    {
        Core.Enums.AlertSeverity.Critical => "#ef4444",
        Core.Enums.AlertSeverity.Error => "#ee6368",
        Core.Enums.AlertSeverity.Warning => "#e79613",
        Core.Enums.AlertSeverity.Info => "#4797ff",
        _ => "#64748b"
    };

    private static string LoadTemplate(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"NetworkOptimizer.Alerts.Templates.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return $"<html><body><p>Template '{name}' not found</p></body></html>";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
