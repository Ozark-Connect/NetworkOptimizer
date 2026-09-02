using System.Text.Json;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>What one access point had to say about one client.</summary>
public enum ApAgentClientLookupStatus
{
    /// <summary>The client is associated to this access point.</summary>
    Found,

    /// <summary>The agent answered and the client is not on it. This is the roam signal.</summary>
    NotOnAp,

    /// <summary>The agent could not be reached or did not answer usefully.</summary>
    Unreachable,
}

/// <summary>One /client/&lt;mac&gt; lookup. Absent and unreachable are different answers: the first
/// means the client moved, the second means this access point cannot say.</summary>
/// <param name="Status">Which of the three answers came back.</param>
/// <param name="Payload">The reply, present only when <see cref="ApAgentClientLookupStatus.Found"/>.</param>
public sealed record ApAgentClientLookup(ApAgentClientLookupStatus Status, ApAgentClientPayload? Payload)
{
    /// <summary>The client, or null when it was not found.</summary>
    public ApAgentClient? Client => Payload?.Client;
}

/// <summary>
/// Reads telemetry from one access point's AP Agent. Reach is the shared
/// <see cref="ApAgentHttpTransport"/>, so a home site dials the AP and an agent site goes through
/// the site's tunnel without this knowing which it is.
/// </summary>
public sealed class ApAgentTelemetryClient
{
    /// <summary>A client list is small even on a busy AP; a bigger body is a fault, not data.</summary>
    private const long MaxClientsBytes = 4 * 1024 * 1024;

    /// <summary>One client's record, with its links. Kilobytes, not megabytes.</summary>
    private const long MaxClientBytes = 512 * 1024;

    /// <summary>The event ring holds at most 2048 entries, each a short line.</summary>
    private const long MaxEventsBytes = 2 * 1024 * 1024;

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

    /// <summary>
    /// Asks one access point about one client. A 404 is the AP saying the client is not on it,
    /// which is what the roam follow needs, so it is reported rather than folded into failure.
    /// </summary>
    public async Task<ApAgentClientLookup> GetClientAsync(
        string siteSlug, string apHost, string? token, string clientMac, TimeSpan timeout, CancellationToken ct = default)
    {
        var mac = Uri.EscapeDataString(clientMac.Trim());
        if (mac.Length == 0) return new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null);

        try
        {
            var (host, port) = await _transport.RouteAsync(siteSlug, apHost);
            var result = await _transport.SendAsync(host, port, token, $"/clients/{mac}", timeout, MaxClientBytes, ct);

            if (result.Status == 404)
                return new ApAgentClientLookup(ApAgentClientLookupStatus.NotOnAp, null);
            if (!result.IsUsable)
                return new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null);

            var payload = JsonSerializer.Deserialize<ApAgentClientPayload>(result.Body, JsonOptions);
            return payload?.Client == null
                ? new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null)
                : new ApAgentClientLookup(ApAgentClientLookupStatus.Found, payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent /client from {Host} failed", apHost);
            return new ApAgentClientLookup(ApAgentClientLookupStatus.Unreachable, null);
        }
    }

    /// <summary>
    /// Fetches one access point's membership events since a point in time. RFC 3339 rather than a
    /// sequence number, because the follow starts mid-stream and holds no sequence to resume from.
    /// </summary>
    public Task<ApAgentEventsPayload?> GetEventsAsync(
        string siteSlug, string apHost, string? token, DateTime sinceUtc, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentEventsPayload>(siteSlug, apHost, token,
            "/events?since=" + Uri.EscapeDataString(sinceUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")),
            timeout, MaxEventsBytes, ct);

    /// <summary>Fetches the AP's radio table. Returns null on any failure.</summary>
    public Task<ApAgentRadiosPayload?> GetRadiosAsync(
        string siteSlug, string apHost, string? token, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentRadiosPayload>(siteSlug, apHost, token, "/radios", timeout, MaxRadiosBytes, ct);

    /// <summary>A few dozen neighbors and a channel list per radio; bounded well above that.</summary>
    private const long MaxScanBytes = 2 * 1024 * 1024;

    /// <summary>Fetches what the AP's radios hear (neighbors and spectrum). Returns null on any failure.</summary>
    public Task<ApAgentScanPayload?> GetScanAsync(
        string siteSlug, string apHost, string? token, TimeSpan timeout, CancellationToken ct = default)
        => GetAsync<ApAgentScanPayload>(siteSlug, apHost, token, "/scan", timeout, MaxScanBytes, ct);

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
