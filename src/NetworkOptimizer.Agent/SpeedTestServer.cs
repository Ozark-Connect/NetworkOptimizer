using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Core.Helpers;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The agent's speed-test results relay. nginx fronts the OpenSpeedTest page and
/// the throughput-critical <c>/downloading</c> and <c>/upload</c> legs (sendfile,
/// so it saturates 10 GbE on modest hardware where Kestrel would go CPU-bound),
/// and proxies the result POSTs to this loopback listener. The relay forwards them
/// to the central Network Optimizer with the site slug appended and the original
/// client IP preserved (from nginx's X-Forwarded-For) - keeping the browser
/// same-origin (no CORS on the central server) and landing results in the site's
/// own database.
/// </summary>
public sealed class SpeedTestServer : IAsyncDisposable
{
    /// <summary>Loopback port the relay binds; nginx proxies /api/public/speedtest/* here.</summary>
    public const int RelayPort = 3001;

    /// <summary>One WAN speed-test server the /wan/ router can redirect to.</summary>
    public sealed record WanServerEntry(string ServerId, string Url);

    private sealed record WanRouting(IReadOnlyList<WanServerEntry> Servers, string DefaultServerId);

    private readonly WebApplication _app;
    private readonly HttpClient _relay;
    private readonly string _siteSlug;
    private Task? _runTask;

    // Server-pushed WAN server list (WanSpeedTestConfig over the tunnel); swapped
    // atomically. Empty until the first push - /wan/ answers 503 with a hint then.
    private volatile WanRouting _wanRouting = new(Array.Empty<WanServerEntry>(), "");

    private SpeedTestServer(WebApplication app, HttpClient relay, string siteSlug)
    {
        _app = app;
        _relay = relay;
        _siteSlug = siteSlug;
    }

    /// <summary>Replaces the WAN speed-test server list the /wan/ router redirects to.</summary>
    public void UpdateWanServers(IReadOnlyList<WanServerEntry> servers, string defaultServerId) =>
        _wanRouting = new WanRouting(servers, defaultServerId);

    /// <summary>Binds the results relay on loopback (<see cref="RelayPort"/>).</summary>
    public static SpeedTestServer Create(string serverUrl, string siteSlug, bool ignoreSslErrors)
    {
        var handler = new HttpClientHandler();
        if (ignoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var relay = new HttpClient(handler) { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{RelayPort}");
        var app = builder.Build();

        app.MapPost("/api/public/speedtest/results",
            (Delegate)((HttpContext context) => RelayAsync(relay, context, "api/public/speedtest/results", siteSlug)));
        app.MapPost("/api/public/speedtest/topology-snapshots",
            (Delegate)((HttpContext context) => RelayAsync(relay, context, "api/public/speedtest/topology-snapshots", siteSlug)));

        // A WAN test post-back arrives cross-origin (the external server's page
        // posting to this agent's origin via document.referrer). Same-origin LAN
        // posts ignore these headers; the endpoints are public either way.
        app.MapMethods("/api/public/speedtest/{*rest}", new[] { "OPTIONS" }, (HttpContext context) =>
        {
            AddCorsHeaders(context);
            return Results.NoContent();
        });

        // The "view results" link on the page lives on the central server.
        app.MapGet("/client-speedtest", () => Results.Redirect(serverUrl.TrimEnd('/') + "/client-speedtest"));

        var server = new SpeedTestServer(app, relay, siteSlug);

        // WAN speed test router: /wan/ goes to the default external WAN test
        // server, /wan/<server-ID>/ to that mapped server, forwarding query params
        // (?Run, duration, ...). This must be a tiny interstitial page that
        // navigates client-side, NOT an HTTP 302: a redirect never makes the
        // redirecting host the referrer (the Referer belongs to the document that
        // initiated the navigation and just rides through the hop), while a
        // document-initiated navigation from this page does. With the agent as
        // the referrer origin, the browser posts results back to this origin and
        // the relay above stamps the site slug + real client LAN IP - no ?site=
        // in any user URL. The referrer meta pins the policy to origin-only.
        app.MapGet("/wan/{serverId?}", (string? serverId, HttpContext context) =>
            server.RedirectToWanServer(serverId, context));

        return server;
    }

    private IResult RedirectToWanServer(string? serverId, HttpContext context)
    {
        var routing = _wanRouting;
        if (routing.Servers.Count == 0)
            return Results.Content(
                "No external WAN speed test server is configured on the central Network Optimizer yet " +
                "(Settings -> External Speed Test Servers), or this agent hasn't received the list yet.",
                "text/plain", statusCode: StatusCodes.Status503ServiceUnavailable);

        WanServerEntry? target;
        if (string.IsNullOrEmpty(serverId))
        {
            target = routing.Servers.FirstOrDefault(s => s.ServerId == routing.DefaultServerId)
                ?? routing.Servers[0];
        }
        else
        {
            target = routing.Servers.FirstOrDefault(s =>
                string.Equals(s.ServerId, serverId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
                return Results.Content($"Unknown WAN speed test server '{serverId}'.",
                    "text/plain", statusCode: StatusCodes.Status404NotFound);
        }

        // pbref=1 marks "this visit came through the agent's /wan/ hop": the test
        // page posts results back to the referrer origin ONLY when it sees this
        // marker (plus a private-address referrer), so an ordinary cross-origin
        // link to a speed test page can never divert results to its referrer.
        var query = context.Request.QueryString.Value ?? "";
        query = query.Length > 0 ? query + "&pbref=1" : "?pbref=1";
        var targetUrl = target.Url.TrimEnd('/') + "/" + query;
        // JSON-encode for a safe JS string literal; HTML-encode for the noscript href.
        var jsTarget = System.Text.Json.JsonSerializer.Serialize(targetUrl);
        var htmlTarget = System.Net.WebUtility.HtmlEncode(targetUrl);
        return Results.Content($$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta name="referrer" content="strict-origin">
            <title>Starting WAN speed test...</title></head>
            <body><script>location.replace({{jsTarget}});</script>
            <noscript><a href="{{htmlTarget}}">Continue to the WAN speed test</a></noscript></body></html>
            """, "text/html");
    }

    private static void AddCorsHeaders(HttpContext context)
    {
        context.Response.Headers.AccessControlAllowOrigin = "*";
        context.Response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
        context.Response.Headers.AccessControlAllowHeaders = "Content-Type";
    }

    public void Start() => _runTask = _app.RunAsync();

    /// <summary>
    /// Relays a client-initiated iperf3 result (raw <c>-J</c> JSON captured by the local
    /// <c>iperf3 -s</c>) to the central server, tagged with this site's slug so it lands in the
    /// site's database - the iperf3 analog of the OpenSpeedTest result relay above. The client IP,
    /// direction and throughput all live in the JSON, so nothing else needs to ride along.
    /// </summary>
    public async Task PostIperf3ResultAsync(string json, CancellationToken ct)
    {
        try
        {
            var query = string.IsNullOrEmpty(_siteSlug) ? "" : $"?site={Uri.EscapeDataString(_siteSlug)}";
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            using var resp = await _relay.PostAsync("api/public/speedtest/iperf3-results" + query, content, ct);
            if (!resp.IsSuccessStatusCode)
                Console.Error.WriteLine($"iperf3 result relay returned {(int)resp.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to relay iperf3 result: {ex.Message}");
        }
    }

    private static async Task<IResult> RelayAsync(HttpClient relay, HttpContext context, string path, string siteSlug)
    {
        AddCorsHeaders(context);
        try
        {
            // The real client address rides as a query param (client_ip), NOT a header:
            // the POST to the central server crosses a reverse proxy / port-map that
            // rewrites X-Forwarded-For to this site's public IP, so the header can't
            // carry it across. nginx put the real client in X-Forwarded-For for us here
            // (the direct hop to this relay is always loopback); fall back to the
            // connection IP. The central endpoint reads client_ip for slug-tagged posts.
            var clientIp = NetworkUtilities.NormalizeToIPv4String(
                context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString());
            var query = context.Request.QueryString.Add("site", siteSlug);
            if (!string.IsNullOrEmpty(clientIp))
                query = query.Add("client_ip", clientIp);
            using var request = new HttpRequestMessage(HttpMethod.Post, path + query.ToUriComponent());

            var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            if (body.Length > 0)
            {
                var mediaType = context.Request.ContentType?.Split(';')[0].Trim();
                request.Content = new StringContent(body, Encoding.UTF8,
                    string.IsNullOrEmpty(mediaType) ? "application/x-www-form-urlencoded" : mediaType);
            }

            using var response = await relay.SendAsync(request, context.RequestAborted);
            var responseBody = await response.Content.ReadAsStringAsync(context.RequestAborted);
            return Results.Content(responseBody,
                response.Content.Headers.ContentType?.MediaType ?? "application/json",
                statusCode: (int)response.StatusCode);
        }
        catch (OperationCanceledException)
        {
            return Results.StatusCode(StatusCodes.Status499ClientClosedRequest);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Speed test result relay failed: {ex.Message}");
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _app.StopAsync(stopTimeout.Token); } catch { }
        if (_runTask != null)
        {
            try { await _runTask; } catch { }
        }
        await _app.DisposeAsync();
        _relay.Dispose();
    }
}
