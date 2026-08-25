using System.Text.Json;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Reads the AP Agent's event ring and VAP table. Reach is the shared
/// <see cref="ApAgentHttpTransport"/>, so a home site dials the access point and an agent site goes
/// through the site's tunnel without this knowing which it is.
/// </summary>
public sealed class ApAgentEventsClient
{
    /// <summary>The ring holds 1024 events and one reply is capped at 2048; a bigger body is a fault.</summary>
    private const long MaxEventsBytes = 4 * 1024 * 1024;

    /// <summary>A VAP table is a handful of entries even on a four-radio access point.</summary>
    private const long MaxVapsBytes = 1 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly ApAgentHttpTransport _transport;
    private readonly ILogger<ApAgentEventsClient> _logger;

    /// <summary>Creates the events client.</summary>
    public ApAgentEventsClient(ApAgentHttpTransport transport, ILogger<ApAgentEventsClient> logger)
    {
        _transport = transport;
        _logger = logger;
    }

    /// <summary>
    /// Fetches every event after <paramref name="sinceSeq"/>. Returns null on any failure, which
    /// leaves the stored cursor untouched so the next pass asks for the same window again.
    /// </summary>
    public Task<ApAgentEventsPayload?> GetEventsAsync(
        string siteSlug, string apHost, string? token, long sinceSeq, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentEventsPayload>(
            siteSlug, apHost, token,
            sinceSeq > 0 ? $"/events?since={sinceSeq}" : "/events",
            timeout, MaxEventsBytes, ct);

    /// <summary>Fetches the AP's VAP table, which is what resolves an event's VAP to a BSSID.</summary>
    public Task<ApAgentVapsPayload?> GetVapsAsync(
        string siteSlug, string apHost, string? token, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentVapsPayload>(siteSlug, apHost, token, "/vaps", timeout, MaxVapsBytes, ct);

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
