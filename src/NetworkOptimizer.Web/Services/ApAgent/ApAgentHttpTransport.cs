using System.Net.Http.Headers;

namespace NetworkOptimizer.Web.Services.ApAgent;

/// <summary>One AP Agent HTTP reply, read under a byte cap.</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="Body">Response body, truncated at the cap.</param>
/// <param name="Truncated">True when the cap cut the body short, so it must not be parsed.</param>
public sealed record ApAgentHttpResult(int Status, string Body, bool Truncated)
{
    /// <summary>Whether the reply is a complete 2xx that is safe to parse.</summary>
    public bool IsUsable => Status is >= 200 and < 300 && !Truncated;
}

/// <summary>
/// The one way the server reaches an AP Agent. Home sites dial the access point directly; agent
/// sites go through the site's tunnel proxy, which is the same machinery already carrying SSH and
/// the console API. Every AP Agent caller routes through here so there is a single reach path.
/// </summary>
public sealed class ApAgentHttpTransport
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SiteTunnelRouting _tunnelRouting;

    /// <summary>Creates the transport.</summary>
    public ApAgentHttpTransport(IHttpClientFactory httpClientFactory, SiteTunnelRouting tunnelRouting)
    {
        _httpClientFactory = httpClientFactory;
        _tunnelRouting = tunnelRouting;
    }

    /// <summary>Resolves the host and port to dial for one access point on one site.</summary>
    public Task<(string Host, int Port)> RouteAsync(string siteSlug, string apHost)
        => _tunnelRouting.RouteAsync(siteSlug, apHost, ApAgentPaths.AgentPort);

    /// <summary>
    /// Issues one request and reads at most <paramref name="maxBytes"/> of the reply. The cap is
    /// load-bearing: /radios is tens of kilobytes on a healthy AP and nothing bounds what a broken
    /// one returns.
    /// </summary>
    public async Task<ApAgentHttpResult> SendAsync(
        string host,
        int port,
        string? token,
        string path,
        TimeSpan timeout,
        long maxBytes,
        CancellationToken ct = default)
    {
        using var client = _httpClientFactory.CreateClient();
        client.Timeout = timeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{port}{path}");
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var (body, truncated) = await ReadBoundedAsync(response, maxBytes, ct);
        return new ApAgentHttpResult((int)response.StatusCode, body, truncated);
    }

    private static async Task<(string Body, bool Truncated)> ReadBoundedAsync(
        HttpResponseMessage response, long maxBytes, CancellationToken ct)
    {
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffered = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;
            if (buffered.Length + read > maxBytes)
                return (string.Empty, true);
            buffered.Write(chunk, 0, read);
        }

        return (System.Text.Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length), false);
    }
}
