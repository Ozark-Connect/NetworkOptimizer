using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using NetworkOptimizer.Monitoring.Models;
using NetworkOptimizer.Monitoring.Providers;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Services.CellularModemProviders;

/// <summary>
/// Cellular modem provider for Inseego FX-series 5G gateways (FX4100 and similar).
///
/// Polls the ubus JSON-RPC interface the web UI uses, at <c>POST https://host/ubus</c>.
/// Signal data needs a signed-in session: the admin password is exchanged for a session
/// token, which is cached per modem and renewed when the gateway refuses it. Request
/// building and parsing live in <see cref="InseegoUbusParser"/>.
/// </summary>
public sealed class InseegoFxProvider : ICellularModemProvider, IDisposable
{
    /// <inheritdoc/>
    public string ProviderKey => "inseego-fx";

    /// <inheritdoc/>
    public string DisplayName => "Inseego FX gateway (HTTPS)";

    private const int DefaultTimeoutSeconds = 15;

    private readonly ILogger<InseegoFxProvider> _logger;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    // The gateway counts failed sign-ins (retry_count), so a rejected password is not retried by
    // the poll loop and cannot run the count down. Only a changed password or a Probe tries again.
    private readonly ConcurrentDictionary<string, string> _rejectedPasswords = new(StringComparer.OrdinalIgnoreCase);

    public InseegoFxProvider(ILogger<InseegoFxProvider> logger)
        : this(logger, new HttpClientHandler
        {
            // The gateway serves a self-signed certificate.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        })
    {
    }

    /// <summary>Test seam: supply the transport.</summary>
    internal InseegoFxProvider(ILogger<InseegoFxProvider> logger, HttpMessageHandler handler)
    {
        _logger = logger;
        _client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds),
        };
    }

    /// <inheritdoc/>
    public async Task<PollResult<CellularModemStats>> PollAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (stats, failure) = await FetchStatsAsync(context, isProbe: false, cancellationToken);
        return stats != null
            ? PollResult<CellularModemStats>.Ok(stats)
            : PollResult<CellularModemStats>.Failed(failure!);
    }

    /// <inheritdoc/>
    public async Task<(bool success, string message)> TestConnectionAsync(
        ModemPollContext context,
        CancellationToken cancellationToken = default)
    {
        var (stats, failure) = await FetchStatsAsync(context, isProbe: true, cancellationToken);
        if (stats == null)
            return (false, failure!);

        var carrier = string.IsNullOrEmpty(stats.Carrier) ? "" : $" on {stats.Carrier}";
        return (true, $"Detected Inseego {stats.ModemModel}{carrier}");
    }

    /// <summary>
    /// Sign in if needed, run the poll batch, and parse it. A refused session is renewed
    /// once per call. Returns stats, or the reason there are none.
    /// </summary>
    private async Task<(CellularModemStats? stats, string? failure)> FetchStatsAsync(
        ModemPollContext context,
        bool isProbe,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(context.Host))
            return (null, "No address is configured for this modem.");

        var host = context.ConfiguredHost ?? context.Host;
        if (string.IsNullOrEmpty(context.Password))
            return (null, "An admin password is required. The gateway serves signal data only to a signed-in session.");

        if (isProbe)
        {
            _rejectedPasswords.TryRemove(context.CacheKey, out _);
            _tokens.TryRemove(context.CacheKey, out _);
        }
        else if (_rejectedPasswords.TryGetValue(context.CacheKey, out var rejected) &&
                 string.Equals(rejected, context.Password, StringComparison.Ordinal))
        {
            return (null, RejectedMessage(host));
        }

        try
        {
            var url = BuildUrl(context);

            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!_tokens.TryGetValue(context.CacheKey, out var token))
                {
                    token = await LoginAsync(url, context.Password, cancellationToken);
                    if (token == null)
                    {
                        _rejectedPasswords[context.CacheKey] = context.Password;
                        _logger.LogWarning("Inseego gateway {Host} rejected the admin password", host);
                        return (null, RejectedMessage(host));
                    }
                    _tokens[context.CacheKey] = token;
                }

                var body = InseegoUbusParser.BuildBatchRequest(token, InseegoUbusParser.PollCalls);
                using var doc = await PostAsync(url, body, cancellationToken);
                var results = InseegoUbusParser.ParseBatchResponse(doc.RootElement, out var accessDenied);

                if (accessDenied)
                {
                    _logger.LogInformation("Inseego session for {Host} expired, signing in again", host);
                    _tokens.TryRemove(context.CacheKey, out _);
                    continue;
                }

                var stats = InseegoUbusParser.Parse(results, context);
                return stats == null
                    ? (null, $"{host} answered but returned no signal data.")
                    : (stats, null);
            }

            return (null, $"{host} signed in, then refused the session. Try Probe & Detect.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Inseego gateway {Host} returned a non-JSON ubus response", host);
            return (null, HttpFailureSummary.ForResponse(null, host));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Error polling Inseego gateway {Name} at {Host}", context.Name, host);
            return (null, HttpFailureSummary.Describe(ex, host));
        }
    }

    /// <summary>Exchange the admin password for a session token. Null when it is rejected.</summary>
    private async Task<string?> LoginAsync(string url, string password, CancellationToken cancellationToken)
    {
        using var doc = await PostAsync(url, InseegoUbusParser.BuildLoginRequest(password), cancellationToken);
        return InseegoUbusParser.ParseLoginToken(doc.RootElement);
    }

    private async Task<JsonDocument> PostAsync(string url, string body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonDocument.Parse(text);
    }

    private static string BuildUrl(ModemPollContext context)
    {
        var port = context.Port is > 0 and not 80 ? context.Port : 443;
        var portSuffix = port == 443 ? "" : $":{port}";
        return $"https://{context.Host}{portSuffix}/ubus";
    }

    private static string RejectedMessage(string host) =>
        $"{host} rejected the admin password. Polling pauses until the password changes or Probe & Detect runs, so the gateway does not lock out sign-in.";

    public void Dispose() => _client.Dispose();
}
