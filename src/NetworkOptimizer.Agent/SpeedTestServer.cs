using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

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

    private readonly WebApplication _app;
    private readonly HttpClient _relay;
    private Task? _runTask;

    private SpeedTestServer(WebApplication app, HttpClient relay)
    {
        _app = app;
        _relay = relay;
    }

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

        // The "view results" link on the page lives on the central server.
        app.MapGet("/client-speedtest", () => Results.Redirect(serverUrl.TrimEnd('/') + "/client-speedtest"));

        return new SpeedTestServer(app, relay);
    }

    public void Start() => _runTask = _app.RunAsync();

    private static async Task<IResult> RelayAsync(HttpClient relay, HttpContext context, string path, string siteSlug)
    {
        try
        {
            // The real client address rides as a query param (client_ip), NOT a header:
            // the POST to the central server crosses a reverse proxy / port-map that
            // rewrites X-Forwarded-For to this site's public IP, so the header can't
            // carry it across. nginx put the real client in X-Forwarded-For for us here
            // (the direct hop to this relay is always loopback); fall back to the
            // connection IP. The central endpoint reads client_ip for slug-tagged posts.
            var clientIp = context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString();
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
