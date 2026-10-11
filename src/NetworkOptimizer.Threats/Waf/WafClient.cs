using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// Talks to netopt-waf (NetworkOptimizer-Proxy) over its token-protected API: reads events and
/// keeps its ban list.
/// </summary>
public sealed class WafClient
{
    /// <summary>Largest page netopt-waf serves.</summary>
    public const int PageSize = 500;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly HttpClient _http;

    public WafClient(HttpClient http) => _http = http;

    /// <summary>
    /// Fetches events with sequence number at or after <paramref name="since"/>.
    /// Throws <see cref="WafClientException"/> with a user-readable message on any failure.
    /// </summary>
    public Task<WafEventPage> GetEventsAsync(string baseUrl, string token, ulong since,
        CancellationToken cancellationToken, int limit = PageSize) =>
        SendAsync<WafEventPage>(HttpMethod.Get, baseUrl, $"/api/events?since={since}&limit={limit}", token, null,
            "The address answered, but not with netopt-waf events. Check the URL.", cancellationToken);

    /// <summary>
    /// Replaces the WAF's ban list with <paramref name="bans"/> and returns how many it holds.
    /// Throws <see cref="WafClientException"/> with a user-readable message on any failure.
    /// </summary>
    public async Task<int> PutBansAsync(string baseUrl, string token, IReadOnlyList<WafBan> bans,
        CancellationToken cancellationToken)
    {
        var page = await SendAsync<WafBanPage>(HttpMethod.Put, baseUrl, "/api/bans", token,
            JsonContent.Create(new { bans }),
            "The WAF does not support a ban list. Update NetworkOptimizer-Proxy.", cancellationToken);
        return page.Count;
    }

    /// <summary>
    /// Replaces one of the WAF's allow lists (routes opt in with <c>/check?allow=name</c>) and
    /// returns how many entries it holds. Throws <see cref="WafClientException"/> on any failure.
    /// </summary>
    public async Task<int> PutAllowListAsync(string baseUrl, string token, string name, IReadOnlyList<string> entries,
        CancellationToken cancellationToken)
    {
        var lists = await SendAsync<List<WafAllowList>>(HttpMethod.Put, baseUrl, $"/api/allowlists/{Uri.EscapeDataString(name)}", token,
            JsonContent.Create(new { entries }),
            "The WAF does not support allow lists. Update NetworkOptimizer-Proxy.", cancellationToken);
        return lists.FirstOrDefault(l => l.Name == name)?.Entries.Count ?? 0;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string baseUrl, string pathAndQuery, string token,
        HttpContent? content, string wrongShapeMessage, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + pathAndQuery, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WafClientException("The WAF URL must be an http:// or https:// address.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WafClientException($"No answer from {uri.GetLeftPart(UriPartial.Authority)} within {Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new WafClientException($"Could not reach {uri.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new WafClientException("The WAF rejected the API token. It must match WAF_API_TOKEN in the proxy's .env.");
            // An older netopt-waf has no /api/bans: its mux answers 404 or 405.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new WafClientException(wrongShapeMessage);
            if (!response.IsSuccessStatusCode)
                throw new WafClientException($"The WAF answered HTTP {(int)response.StatusCode}.");
            try
            {
                return await response.Content.ReadFromJsonAsync<T>(cts.Token)
                    ?? throw new WafClientException("The WAF returned an empty response.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new WafClientException(wrongShapeMessage);
            }
        }
    }
}

/// <summary>One entry of netopt-waf's ban list.</summary>
public sealed record WafBan(
    [property: JsonPropertyName("ip")] string Ip,
    [property: JsonPropertyName("expires")] DateTimeOffset Expires);

/// <summary>One allow list in netopt-waf's GET/PUT /api/allowlists response.</summary>
public sealed class WafAllowList
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("entries")] public List<string> Entries { get; set; } = [];
}

/// <summary>netopt-waf's GET/PUT /api/bans response.</summary>
public sealed class WafBanPage
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("bans")] public List<WafBan> Bans { get; set; } = [];
}

/// <summary>A WAF API failure with a message fit to show the user.</summary>
public sealed class WafClientException(string message) : Exception(message);
