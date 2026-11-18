using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkOptimizer.Web.Services.Tailscale;

/// <summary>A device list could not be read; the message says why, for the person setting it up.</summary>
public sealed class TailnetDirectoryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>One device as a control server's API lists it, before any local status is added.</summary>
/// <param name="Name">The machine name the admin console shows (the first MagicDNS label).</param>
/// <param name="Routes">Approved subnet routes, exit-node defaults included.</param>
/// <param name="Endpoints">The device's UDP endpoints (ip:port), where the API reports them.</param>
public sealed record TailnetDevice(
    string Name,
    string? Hostname,
    string? Os,
    string? User,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Routes,
    IReadOnlyList<string> Endpoints,
    bool? Online);

/// <summary>
/// Reads a tailnet's devices from the Tailscale API (v2) with an OAuth client. Read-only: the token
/// request and the device list are the only calls. The token lives an hour and is reused until near
/// its end.
/// </summary>
public sealed class TailscaleApiClient
{
    private const string ApiBase = "https://api.tailscale.com/api/v2";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly string _clientId;
    private readonly string _clientSecret;
   private string? _token;
    private DateTime _tokenExpires;

    public TailscaleApiClient(HttpClient http, string clientId, string clientSecret)
    {
        _http = http;
        _clientId = clientId.Trim();
        _clientSecret = clientSecret.Trim();
   }

    /// <summary>Every device in the tailnet, with routes and endpoints (<c>fields=all</c>).</summary>
    public async Task<IReadOnlyList<TailnetDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{ApiBase}/tailnet/-/devices?fields=all");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await TailnetHttp.SendAsync(_http, request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            _token = null;
            throw new TailnetDirectoryException("The Tailscale API refused the device list. The OAuth client needs the Devices Core read scope (devices:core:read).");
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new TailnetDirectoryException("The Tailscale API found no tailnet for this OAuth credential.");
        TailnetHttp.EnsureOk(response, "The Tailscale API");
        var body = await response.Content.ReadFromJsonAsync<DeviceList>(Json, ct);
        return (body?.Devices ?? []).Select(Map).ToList();
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token != null && DateTime.UtcNow < _tokenExpires) return _token;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["client_secret"] = _clientSecret,
                ["grant_type"] = "client_credentials",
            }),
        };
        using var response = await TailnetHttp.SendAsync(_http, request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
            throw new TailnetDirectoryException("Tailscale did not accept the OAuth client ID and secret.");
        TailnetHttp.EnsureOk(response, "The Tailscale token endpoint");
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(Json, ct);
        if (string.IsNullOrEmpty(body?.AccessToken))
            throw new TailnetDirectoryException("The Tailscale token endpoint returned no access token.");
        _token = body.AccessToken;
        _tokenExpires = DateTime.UtcNow.AddSeconds(Math.Max(60, (body.ExpiresIn ?? 3600) - 300));
        return _token;
    }

    private static TailnetDevice Map(ApiDevice d)
    {
        // Enabled routes are the ones the tailnet actually uses; a listing without them falls back to advertised.
        var routes = d.EnabledRoutes ?? d.AdvertisedRoutes ?? [];
        return new TailnetDevice(
            TailnetNames.MachineName(d.Name) ?? d.Hostname ?? d.Addresses?.FirstOrDefault() ?? "Tailscale device",
            d.Hostname, d.Os, d.User,
            d.Addresses ?? [], d.Tags ?? [], routes,
            d.ClientConnectivity?.Endpoints ?? [],
            d.ConnectedToControl);
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
    }

    private sealed class DeviceList
    {
        public List<ApiDevice>? Devices { get; set; }
    }

    private sealed class ApiDevice
    {
        public List<string>? Addresses { get; set; }
        public string? Name { get; set; }
        public string? Hostname { get; set; }
        public string? Os { get; set; }
        public string? User { get; set; }
        public List<string>? Tags { get; set; }
        public bool? ConnectedToControl { get; set; }
        public List<string>? AdvertisedRoutes { get; set; }
        public List<string>? EnabledRoutes { get; set; }
        public Connectivity? ClientConnectivity { get; set; }
    }

    private sealed class Connectivity
    {
        public List<string>? Endpoints { get; set; }
    }
}

/// <summary>Reads a Headscale control server's nodes (<c>GET /api/v1/node</c>) with an API key.</summary>
public sealed class HeadscaleApiClient(HttpClient http, string baseUrl, string apiKey)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A problem with the URL as typed, or null when it can be used.</summary>
    public static string? UrlProblem(string url) =>
        !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp)
            ? "Enter the Headscale server's URL, such as https://headscale.example.com."
            : null;

    /// <summary>Every node the server knows.</summary>
    public async Task<IReadOnlyList<TailnetDevice>> GetDevicesAsync(CancellationToken ct)
    {
        if (UrlProblem(baseUrl) is { } problem) throw new TailnetDirectoryException(problem);
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl.Trim().TrimEnd('/') + "/api/v1/node");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        using var response = await TailnetHttp.SendAsync(http, request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new TailnetDirectoryException("Headscale refused the API key. Create one with headscale apikeys create.");
        TailnetHttp.EnsureOk(response, "Headscale");
        NodeList? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<NodeList>(Json, ct);
        }
        catch (JsonException ex)
        {
            throw new TailnetDirectoryException("That URL answered, but not with Headscale's node list.", ex);
        }
        return (body?.Nodes ?? []).Select(Map).ToList();
    }

    private static TailnetDevice Map(Node n)
    {
        // subnetRoutes are the approved routes the node announces, so the ones in use.
        var routes = n.SubnetRoutes is { Count: > 0 } served ? served : n.ApprovedRoutes ?? [];
        var tags = (n.Tags ?? []).Concat(n.ValidTags ?? []).Concat(n.ForcedTags ?? []).Distinct().ToList();
        return new TailnetDevice(
            Blank(n.GivenName) ?? Blank(n.Name) ?? n.IpAddresses?.FirstOrDefault() ?? "Headscale node",
            Blank(n.Name), null,
            Blank(n.User?.DisplayName) ?? Blank(n.User?.Name),
            n.IpAddresses ?? [], tags, routes, [], n.Online);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class NodeList
    {
        public List<Node>? Nodes { get; set; }
    }

    private sealed class Node
    {
        public string? Name { get; set; }
        public string? GivenName { get; set; }
        public List<string>? IpAddresses { get; set; }
        public HsUser? User { get; set; }
        public bool? Online { get; set; }
        public List<string>? ApprovedRoutes { get; set; }
        public List<string>? SubnetRoutes { get; set; }
        public List<string>? Tags { get; set; }
        public List<string>? ValidTags { get; set; }
        public List<string>? ForcedTags { get; set; }
    }

    private sealed class HsUser
    {
        public string? Name { get; set; }
        public string? DisplayName { get; set; }
    }
}

/// <summary>The request and error handling both directory clients share.</summary>
internal static class TailnetHttp
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Timeout);
        try
        {
            return await http.SendAsync(request, deadline.Token);
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TailnetDirectoryException($"{request.RequestUri?.Host} did not answer within {Timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new TailnetDirectoryException($"Could not reach {request.RequestUri?.Host}: {ex.Message}", ex);
        }
    }

    public static void EnsureOk(HttpResponseMessage response, string what)
    {
        if (!response.IsSuccessStatusCode)
            throw new TailnetDirectoryException($"{what} answered HTTP {(int)response.StatusCode}.");
    }
}

/// <summary>One DERP relay region as Tailscale publishes it: its code ("dfw") and name ("Dallas").</summary>
public sealed record DerpRegion(string Code, string Name);

/// <summary>
/// Tailscale's public DERP map (https://login.tailscale.com/derpmap/default, no credentials): every relay
/// server's addresses with its region. It names relay traffic in Live Traffic by address, and region
/// codes by name. Read-only and public; a failed read leaves relays matched by hostname only.
/// </summary>
public static class DerpMapClient
{
    public const string Url = "https://login.tailscale.com/derpmap/default";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Region by relay address, and region by code.</summary>
    public sealed record DerpMap(IReadOnlyDictionary<string, DerpRegion> ByAddress, IReadOnlyDictionary<string, DerpRegion> ByCode);

    public static async Task<DerpMap> GetAsync(HttpClient http, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        using var response = await TailnetHttp.SendAsync(http, request, ct);
        TailnetHttp.EnsureOk(response, "Tailscale's DERP map");
        var body = await response.Content.ReadFromJsonAsync<Map>(Json, ct);
        var byAddress = new Dictionary<string, DerpRegion>(StringComparer.OrdinalIgnoreCase);
        var byCode = new Dictionary<string, DerpRegion>(StringComparer.OrdinalIgnoreCase);
        foreach (var region in (body?.Regions ?? []).Values)
        {
            if (string.IsNullOrWhiteSpace(region.RegionCode)) continue;
            var r = new DerpRegion(region.RegionCode, string.IsNullOrWhiteSpace(region.RegionName) ? region.RegionCode : region.RegionName);
            byCode.TryAdd(r.Code, r);
            foreach (var node in region.Nodes ?? [])
                foreach (var ip in new[] { node.IPv4, node.IPv6 })
                    if (System.Net.IPAddress.TryParse(ip, out var parsed)) byAddress.TryAdd(parsed.ToString(), r);
        }
        return new DerpMap(byAddress, byCode);
    }

    private sealed class Map
    {
        public Dictionary<string, Region>? Regions { get; set; }
    }

    private sealed class Region
    {
        public string? RegionCode { get; set; }
        public string? RegionName { get; set; }
        public List<Node>? Nodes { get; set; }
    }

    private sealed class Node
    {
        public string? IPv4 { get; set; }
        public string? IPv6 { get; set; }
    }
}

/// <summary>Display names for tailnet nodes.</summary>
public static class TailnetNames
{
    /// <summary>The first label of a MagicDNS name ("laptop" from "laptop.tail1234.ts.net."); null for none.</summary>
    public static string? MachineName(string? dnsName)
    {
        if (string.IsNullOrWhiteSpace(dnsName)) return null;
        var trimmed = dnsName.Trim().TrimEnd('.');
        var dot = trimmed.IndexOf('.');
        return dot > 0 ? trimmed[..dot] : trimmed;
    }
}
