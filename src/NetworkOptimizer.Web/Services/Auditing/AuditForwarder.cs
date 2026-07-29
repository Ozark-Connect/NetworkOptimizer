using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Services.Auditing;

/// <summary>Forwards persisted audit events off the box to a SIEM. Best-effort; failures are logged, never fatal.</summary>
public interface IAuditForwarder
{
    /// <summary>Forwards a batch to whichever sinks are enabled, filtered by per-category toggles.</summary>
    Task ForwardAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// Audit forwarding (design doc 05): the real tamper-resistance story is getting events off the box.
/// Supports syslog (RFC 5424 over TCP, optionally TLS) and an HMAC-signed JSON webhook, each with
/// per-category toggles. Configuration lives in main-DB global settings; the webhook secret is stored
/// Data-Protection-encrypted and never logged. Best-effort: a sink failure is logged and skipped so a
/// down SIEM never blocks or crashes the audit writer.
/// </summary>
public sealed class AuditForwarder : IAuditForwarder
{
    private readonly IAuditForwardingConfig _config;
    private readonly ILogger<AuditForwarder> _logger;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public AuditForwarder(IAuditForwardingConfig config, ILogger<AuditForwarder> logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ForwardAsync(IReadOnlyList<AuditEvent> events, CancellationToken cancellationToken)
    {
        var settings = await _config.GetAsync();
        if (!settings.SyslogEnabled && !settings.WebhookEnabled)
            return;

        var selected = settings.Categories.Count == 0
            ? events
            : events.Where(e => settings.Categories.Contains(e.Category)).ToList();
        if (selected.Count == 0)
            return;

        if (settings.SyslogEnabled)
            await SafeAsync("syslog", () => SendSyslogAsync(selected, settings, cancellationToken));
        if (settings.WebhookEnabled)
            await SafeAsync("webhook", () => SendWebhookAsync(selected, settings, cancellationToken));
    }

    private async Task SendSyslogAsync(IReadOnlyList<AuditEvent> events, AuditForwardingSettings s, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(s.SyslogHost!, s.SyslogPort, ct);
        Stream stream = client.GetStream();
        if (s.SyslogUseTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(s.SyslogHost!);
            stream = ssl;
        }

        await using (stream)
        {
            foreach (var e in events)
            {
                var frame = Encoding.UTF8.GetBytes(FormatRfc5424(e));
                // Octet-counting framing (RFC 6587) for TCP syslog.
                var prefix = Encoding.ASCII.GetBytes($"{frame.Length} ");
                await stream.WriteAsync(prefix, ct);
                await stream.WriteAsync(frame, ct);
            }
            await stream.FlushAsync(ct);
        }
    }

    /// <summary>RFC 5424: <c>&lt;PRI&gt;1 TIMESTAMP HOST APP PROCID MSGID SD MSG</c>. Facility 13 (log audit), severity 6 (info).</summary>
    private static string FormatRfc5424(AuditEvent e)
    {
        const int pri = 13 * 8 + 6;
        var ts = e.TimestampUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var msg = JsonSerializer.Serialize(new
        {
            e.Id, e.Category, e.Action, e.Outcome, e.ActorName, e.ActorAuthMethod,
            e.SourceIp, e.TargetType, e.TargetId, e.SiteSlug, e.CorrelationId,
        });
        return $"<{pri}>1 {ts} network-optimizer netopt-audit - {e.Action} - {msg}";
    }

    private async Task SendWebhookAsync(IReadOnlyList<AuditEvent> events, AuditForwardingSettings s, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(events.Select(e => new
        {
            e.Id, e.TimestampUtc, e.Category, e.Action, e.Outcome, e.ActorUserId, e.ActorName,
            e.ActorAuthMethod, e.SourceIp, e.UserAgent, e.TargetType, e.TargetId, e.TargetName,
            e.SiteSlug, e.CorrelationId, e.DetailsJson,
        }));

        using var request = new HttpRequestMessage(HttpMethod.Post, s.WebhookUrl)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrEmpty(s.WebhookSecret))
        {
            var sig = Convert.ToHexString(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(s.WebhookSecret), Encoding.UTF8.GetBytes(payload)));
            request.Headers.TryAddWithoutValidation("X-NetOpt-Signature", $"sha256={sig.ToLowerInvariant()}");
        }

        var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task SafeAsync(string sink, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Audit forwarding to {Sink} failed; events were not forwarded.", sink);
        }
    }
}

/// <summary>Resolved audit-forwarding settings snapshot.</summary>
public sealed record AuditForwardingSettings
{
    public bool SyslogEnabled { get; init; }
    public string? SyslogHost { get; init; }
    public int SyslogPort { get; init; } = 6514;
    public bool SyslogUseTls { get; init; } = true;
    public bool WebhookEnabled { get; init; }
    public string? WebhookUrl { get; init; }
    public string? WebhookSecret { get; init; }
    public IReadOnlySet<string> Categories { get; init; } = new HashSet<string>();
}

/// <summary>Reads/writes audit-forwarding config from main-DB global settings (webhook secret Data-Protection-encrypted).</summary>
public interface IAuditForwardingConfig
{
    Task<AuditForwardingSettings> GetAsync();
    Task SaveAsync(AuditForwardingSettings settings);
}

/// <inheritdoc />
public sealed class AuditForwardingConfig : IAuditForwardingConfig
{
    private const string Prefix = "audit.forward.";
    private readonly ISystemSettingsService _settings;
    /// <summary>
    /// The product's one credential protection, as used by the SSH passwords, the console password
    /// and the notification-channel secrets - not a second key store of its own.
    /// </summary>
    private readonly NetworkOptimizer.Storage.Services.ICredentialProtectionService _secrets;

    public AuditForwardingConfig(
        ISystemSettingsService settings,
        NetworkOptimizer.Storage.Services.ICredentialProtectionService secrets)
    {
        _settings = settings;
        _secrets = secrets;
    }

    public async Task<AuditForwardingSettings> GetAsync()
    {
        var categories = (await _settings.GetGlobalAsync(Prefix + "categories") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AuditForwardingSettings
        {
            SyslogEnabled = await Bool(Prefix + "syslog.enabled"),
            SyslogHost = await _settings.GetGlobalAsync(Prefix + "syslog.host"),
            SyslogPort = int.TryParse(await _settings.GetGlobalAsync(Prefix + "syslog.port"), out var p) ? p : 6514,
            SyslogUseTls = await Bool(Prefix + "syslog.tls", defaultValue: true),
            WebhookEnabled = await Bool(Prefix + "webhook.enabled"),
            WebhookUrl = await _settings.GetGlobalAsync(Prefix + "webhook.url"),
            WebhookSecret = Unprotect(await _settings.GetGlobalAsync(Prefix + "webhook.secret")),
            Categories = categories,
        };
    }

    public async Task SaveAsync(AuditForwardingSettings s)
    {
        await _settings.SetGlobalAsync(Prefix + "syslog.enabled", s.SyslogEnabled ? "true" : "false");
        await _settings.SetGlobalAsync(Prefix + "syslog.host", s.SyslogHost);
        await _settings.SetGlobalAsync(Prefix + "syslog.port", s.SyslogPort.ToString());
        await _settings.SetGlobalAsync(Prefix + "syslog.tls", s.SyslogUseTls ? "true" : "false");
        await _settings.SetGlobalAsync(Prefix + "webhook.enabled", s.WebhookEnabled ? "true" : "false");
        await _settings.SetGlobalAsync(Prefix + "webhook.url", s.WebhookUrl);
        if (!string.IsNullOrEmpty(s.WebhookSecret))
            await _settings.SetGlobalAsync(Prefix + "webhook.secret", _secrets.Encrypt(s.WebhookSecret));
        await _settings.SetGlobalAsync(Prefix + "categories", string.Join(",", s.Categories));
    }

    private async Task<bool> Bool(string key, bool defaultValue = false)
    {
        var v = await _settings.GetGlobalAsync(key);
        return v is null ? defaultValue : string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    }

    private string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try { return _secrets.Decrypt(protectedValue); }
        catch { return null; }
    }
}
