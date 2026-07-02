using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Serves the embedded OpenSpeedTest page at the site so clients can run LAN
/// speed tests against the agent. The download endpoint streams a generated
/// payload, the upload endpoint drains, and result posts are relayed
/// server-side to the central Network Optimizer with the site slug appended
/// and the original client IP forwarded - which keeps the browser same-origin
/// (no CORS setup on the central server) and lands results in the site's own
/// database.
/// </summary>
public sealed class SpeedTestServer : IAsyncDisposable
{
    private static readonly byte[] DownloadPayload = CreatePayload();

    private readonly WebApplication _app;
    private readonly HttpClient _relay;
    private Task? _runTask;

    private SpeedTestServer(WebApplication app, HttpClient relay)
    {
        _app = app;
        _relay = relay;
    }

    private static byte[] CreatePayload()
    {
        // Random so nothing in the path can transparently compress it. Matches
        // the 30 MiB `downloading` blob the nginx deployments serve.
        var payload = new byte[30 * 1024 * 1024];
        Random.Shared.NextBytes(payload);
        return payload;
    }

    public static SpeedTestServer Create(string serverUrl, string siteSlug, int port, bool ignoreSslErrors)
    {
        var handler = new HttpClientHandler();
        if (ignoreSslErrors)
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        var relay = new HttpClient(handler) { BaseAddress = new Uri(serverUrl.TrimEnd('/') + "/") };

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://*:{port}");
        var app = builder.Build();

        var assets = new ManifestEmbeddedFileProvider(typeof(SpeedTestServer).Assembly, "SpeedTestSite");

        // Same shape the deployment scripts inject into config.js, except the
        // save URL points back at this agent, which relays to the server.
        var configJs = """
            var saveData = true;
            var saveDataURL = window.location.protocol + "//" + window.location.host + "/api/public/speedtest/results";
            var apiPath = "/api/public/speedtest/results";
            var externalServerId = "";
            var clientResultsUrl = saveDataURL.split("/api")[0] + "/client-speedtest";
            var OpenSpeedTestdb = "";
            """;

        app.MapGet("/", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Stream(assets.GetFileInfo("index.html").CreateReadStream(), "text/html");
        });

        app.MapGet("/assets/js/config.js", () => Results.Text(configJs, "application/javascript"));

        app.MapGet("/downloading", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Bytes(DownloadPayload, "application/octet-stream");
        });

        app.MapPost("/upload", async (HttpContext context) =>
        {
            await context.Request.Body.CopyToAsync(Stream.Null, context.RequestAborted);
            return Results.Ok();
        });

        app.MapPost("/api/public/speedtest/results",
            (Delegate)((HttpContext context) => RelayAsync(relay, context, "api/public/speedtest/results", siteSlug)));
        app.MapPost("/api/public/speedtest/topology-snapshots",
            (Delegate)((HttpContext context) => RelayAsync(relay, context, "api/public/speedtest/topology-snapshots", siteSlug)));

        // The results link on the page lives on the central server.
        app.MapGet("/client-speedtest", () => Results.Redirect(serverUrl.TrimEnd('/') + "/client-speedtest"));

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = assets,
            ServeUnknownFileTypes = true,
        });

        return new SpeedTestServer(app, relay);
    }

    public void Start() => _runTask = _app.RunAsync();

    private static async Task<IResult> RelayAsync(HttpClient relay, HttpContext context, string path, string siteSlug)
    {
        try
        {
            var query = context.Request.QueryString.Add("site", siteSlug);
            using var request = new HttpRequestMessage(HttpMethod.Post, path + query.ToUriComponent());

            var body = await new StreamReader(context.Request.Body).ReadToEndAsync(context.RequestAborted);
            if (body.Length > 0)
            {
                var mediaType = context.Request.ContentType?.Split(';')[0].Trim();
                request.Content = new StringContent(body, Encoding.UTF8,
                    string.IsNullOrEmpty(mediaType) ? "application/x-www-form-urlencoded" : mediaType);
            }

            // The server's GetClientIp honors X-Forwarded-For, so results keep
            // the real client address instead of the agent's.
            var clientIp = context.Connection.RemoteIpAddress?.ToString();
            if (!string.IsNullOrEmpty(clientIp))
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);

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
