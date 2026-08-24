using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>
/// Calls an AP Agent's GET /health and turns the outcome into an observation the classifier can act
/// on. This is the reactive, authoritative trigger: the reboot signal can silently fail to arrive
/// (an SNMP gap, an AP offline across the polling window, the server restarting through it, a
/// firmware upgrade that does not present as a reboot, an agent-covered site whose collection path
/// differs), so nothing may depend on it alone.
/// </summary>
public sealed class ApAgentHealthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SiteTunnelRouting _tunnelRouting;
    private readonly AgentTunnelProxyService? _tunnelProxy;
    private readonly ILogger<ApAgentHealthClient> _logger;

    /// <summary>Creates the health client.</summary>
    public ApAgentHealthClient(
        IHttpClientFactory httpClientFactory,
        SiteTunnelRouting tunnelRouting,
        ILogger<ApAgentHealthClient> logger,
        AgentTunnelProxyService? tunnelProxy = null)
    {
        _httpClientFactory = httpClientFactory;
        _tunnelRouting = tunnelRouting;
        _logger = logger;
        _tunnelProxy = tunnelProxy;
    }

    /// <summary>
    /// Probes one AP's agent. Home sites dial the AP directly; agent sites go through the site's
    /// tunnel proxy, which is the same machinery already carrying SSH and the console API.
    /// </summary>
    /// <param name="siteSlug">Site the AP belongs to.</param>
    /// <param name="apHost">The AP's management address.</param>
    /// <param name="token">Bearer token the AP's agent was deployed with.</param>
    /// <param name="deviceOnline">Whether the console reports the AP as connected.</param>
    /// <param name="supportedArchitecture">Whether an AP Agent build exists for this hardware.</param>
    /// <param name="expectedBinaryVersion">Contract version this server ships.</param>
    /// <param name="timeout">How long to wait before calling it a timeout.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<ApAgentObservation> ProbeAsync(
        string siteSlug,
        string apHost,
        string? token,
        bool deviceOnline,
        bool supportedArchitecture,
        int expectedBinaryVersion,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        if (!supportedArchitecture || !deviceOnline)
        {
            return new ApAgentObservation(ApAgentReach.NotAttempted,
                DeviceOnline: deviceOnline,
                SupportedArchitecture: supportedArchitecture,
                ExpectedBinaryVersion: expectedBinaryVersion);
        }

        var (host, port) = await _tunnelRouting.RouteAsync(siteSlug, apHost, ApAgentPaths.AgentPort);

        using var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}/health");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var status = (int)response.StatusCode;

            ApAgentHealthPayload? payload = null;
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                payload = ParseHealth(body);
            }

            return new ApAgentObservation(ApAgentReach.Answered, status,
                DeviceOnline: true,
                SupportedArchitecture: true,
                Health: payload,
                ExpectedBinaryVersion: expectedBinaryVersion);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var (reach, detail) = ClassifyFailure(ex, port);
            _logger.LogDebug(ex, "AP Agent health probe to {Host}:{Port} failed: {Reach}", apHost, port, reach);
            return new ApAgentObservation(reach,
                DeviceOnline: true,
                SupportedArchitecture: true,
                ExpectedBinaryVersion: expectedBinaryVersion,
                Detail: detail);
        }
    }

    /// <summary>Reads the /health fields the server acts on. Returns null when the body is not one.</summary>
    public static ApAgentHealthPayload? ParseHealth(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // binary_version is the field the redeploy decision turns on, so a body without it is
            // not an AP Agent health response, whatever else it contains.
            if (!root.TryGetProperty("binary_version", out var binaryVersion)) return null;

            return new ApAgentHealthPayload(
                root.TryGetProperty("version", out var v) ? v.GetString() : null,
                binaryVersion.TryGetInt32(out var bv) ? bv : 0,
                ReadUtc(root, "last_probe_run"),
                ReadUtc(root, "collected_at"),
                root.TryGetProperty("degraded", out var d) && d.ValueKind == JsonValueKind.True,
                ReadStrings(root, "unavailable"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTime ReadUtc(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.TryGetDateTime(out var value)
            ? value.ToUniversalTime()
            : default;

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.GetString() is { } s) values.Add(s);
        }
        return values;
    }

    /// <summary>
    /// The whole diagnostic: a refusal means the packet reached the AP, a timeout means it did not.
    /// An agent-routed AP is dialed on loopback, where the local socket says nothing about the far
    /// side, so the site's agent's own reason for the failed open replaces it when there is one.
    /// </summary>
    private (ApAgentReach Reach, string? Detail) ClassifyFailure(Exception ex, int port)
    {
        if (_tunnelProxy?.RecentOpenFailure(port) is { } tunnelReason)
            return (ApAgentHealthClassifier.ReachFromTunnelFailure(tunnelReason), $"the site's agent could not reach the access point: {tunnelReason}");

        if (ex is TaskCanceledException or TimeoutException)
            return (ApAgentReach.TimedOut, null);

        var socket = FindSocketException(ex);
        return socket?.SocketErrorCode switch
        {
            SocketError.ConnectionRefused or SocketError.ConnectionReset => (ApAgentReach.Refused, null),
            SocketError.TimedOut => (ApAgentReach.TimedOut, null),
            SocketError.HostUnreachable or SocketError.NetworkUnreachable or SocketError.HostNotFound
                => (ApAgentReach.Unreachable, null),
            _ => (ApAgentReach.Unknown, ex.Message),
        };
    }

    private static SocketException? FindSocketException(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is SocketException socket) return socket;
            ex = ex.InnerException;
        }
        return null;
    }
}
