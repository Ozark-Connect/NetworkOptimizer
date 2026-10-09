using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NetworkOptimizer.Threats.Waf;

/// <summary>
/// Reads events from netopt-waf (NetworkOptimizer-Proxy) over its token-protected API.
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
    public async Task<WafEventPage> GetEventsAsync(string baseUrl, string token, ulong since,
        CancellationToken cancellationToken, int limit = PageSize)
    {
        if (!Uri.TryCreate(baseUrl.TrimEnd('/') + $"/api/events?since={since}&limit={limit}",
                UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new WafClientException("The WAF URL must be an http:// or https:// address.");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
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
            if (!response.IsSuccessStatusCode)
                throw new WafClientException($"The WAF answered HTTP {(int)response.StatusCode}.");
            try
            {
                return await response.Content.ReadFromJsonAsync<WafEventPage>(cts.Token)
                    ?? throw new WafClientException("The WAF returned an empty response.");
            }
            catch (System.Text.Json.JsonException)
            {
                throw new WafClientException("The address answered, but not with netopt-waf events. Check the URL.");
            }
        }
    }
}

/// <summary>A WAF API failure with a message fit to show the user.</summary>
public sealed class WafClientException(string message) : Exception(message);
