using System.Text.Json;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Reads telemetry from one access point's AP Agent. Reach is the shared
/// <see cref="ApAgentHttpTransport"/>, so a home site dials the AP and an agent site goes through
/// the site's tunnel without this knowing which it is.
/// </summary>
public sealed class ApAgentTelemetryClient
{
    /// <summary>A client list is small even on a busy AP; a bigger body is a fault, not data.</summary>
    private const long MaxClientsBytes = 4 * 1024 * 1024;

    /// <summary>
    /// /radios is around 80 KB on a four-radio AP because it unions every counter both tools
    /// report. The cap is generous enough for that and still bounded.
    /// </summary>
    private const long MaxRadiosBytes = 8 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ApAgentHttpTransport _transport;
    private readonly ILogger<ApAgentTelemetryClient> _logger;

    /// <summary>Creates the telemetry client.</summary>
    public ApAgentTelemetryClient(ApAgentHttpTransport transport, ILogger<ApAgentTelemetryClient> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Fetches the AP's client table. Returns null on any failure, which is the signal for the
    /// caller to hand this access point back to the console path rather than leave a gap.
    /// </summary>
    public Task<ApAgentClientsPayload?> GetClientsAsync(
        string siteSlug, string apHost, string? token, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentClientsPayload>(siteSlug, apHost, token, "/clients", timeout, MaxClientsBytes, ct);

    /// <summary>Fetches the AP's radio table. Returns null on any failure.</summary>
    public Task<ApAgentRadiosPayload?> GetRadiosAsync(
        string siteSlug, string apHost, string? token, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentRadiosPayload>(siteSlug, apHost, token, "/radios", timeout, MaxRadiosBytes, ct);

    private async Task<T?> GetAsync<T>(
        string siteSlug, string apHost, string? token, string path, TimeSpan timeout, long maxBytes, CancellationToken ct)
        where T : class
    {
        try
        {
            var (host, port) = await _transport.RouteAsync(siteSlug, apHost);
            var result = await _transport.SendAsync(host, port, token, path, timeout, maxBytes, ct);

            if (result.Truncated)
            {
                _logger.LogWarning("AP Agent {Path} from {Host} exceeded {Cap} bytes and was discarded",
                    path, apHost, maxBytes);
                return null;
            }
            if (!result.IsUsable)
            {
                _logger.LogDebug("AP Agent {Path} from {Host} answered {Status}", path, apHost, result.Status);
                return null;
            }

            return JsonSerializer.Deserialize<T>(result.Body, JsonOptions);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent {Path} from {Host} failed", path, apHost);
            return null;
        }
    }
}
