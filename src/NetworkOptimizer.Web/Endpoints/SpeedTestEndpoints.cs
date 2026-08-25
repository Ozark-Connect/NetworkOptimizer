using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class SpeedTestEndpoints
{
    public static void MapSpeedTestEndpoints(this WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        var operate = app.MapGroup("").RequireAuthorization(Policies.RequireOperator);
        // IClientSpeedTestService gates deletion on the site in context; the group carries the
        // metadata only.
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        // --- LAN iperf3 Speed Test ---

        read.MapGet("/api/speedtest/devices", async (IIperf3SpeedTestService service) =>
        {
            var devices = await service.GetDevicesAsync();
            return Results.Ok(devices);
        });

        operate.MapPost("/api/speedtest/devices/{deviceId:int}/results", async (int deviceId, IIperf3SpeedTestService service) =>
        {
            var devices = await service.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null)
                return Results.NotFound(new { error = "Device not found" });

            var result = await service.RunSpeedTestAsync(device);
            return Results.Ok(result);
        });

        read.MapGet("/api/speedtest/results", async (IIperf3SpeedTestService service, string? deviceHost = null, int count = 50) =>
        {
            // Validate count parameter is within reasonable bounds
            if (count < 1) count = 1;
            if (count > 1000) count = 1000;

            // Filter by device host if provided
            if (!string.IsNullOrWhiteSpace(deviceHost))
            {
                // Validate deviceHost format (IP address or hostname, no path traversal)
                if (deviceHost.Contains("..") || deviceHost.Contains('/') || deviceHost.Contains('\\'))
                    return Results.BadRequest(new { error = "Invalid device host format" });

                return Results.Ok(await service.GetResultsForDeviceAsync(deviceHost, count));
            }

            var results = await service.GetRecentResultsAsync(count);
            return Results.Ok(results);
        });

        // --- Client Speed Test (OpenSpeedTest / WAN) ---

        // Public endpoint for external clients (OpenSpeedTest, iperf3) to submit results
        app.MapPost("/api/public/speedtest/results", async (HttpContext context,
            SpeedTestServiceRegistry speedTestRegistry,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
            ILoggerFactory loggerFactory) =>
        {
            // OpenSpeedTest sends data as URL query params: d, u, p, j, dd, ud, ua
            var query = context.Request.Query;

            // Also check form data for POST body
            IFormCollection? form = null;
            if (context.Request.HasFormContentType)
            {
                form = await context.Request.ReadFormAsync();
            }

            // Helper to get value from query or form
            string? GetValue(string key) =>
                query.TryGetValue(key, out var qv) ? qv.ToString() :
                form?.TryGetValue(key, out var fv) == true ? fv.ToString() : null;

            var downloadStr = GetValue("d");
            var uploadStr = GetValue("u");

            if (string.IsNullOrEmpty(downloadStr) || string.IsNullOrEmpty(uploadStr))
            {
                return Results.BadRequest(new { error = "Missing required parameters: d (download) and u (upload)" });
            }

            if (!double.TryParse(downloadStr, out var download) || !double.TryParse(uploadStr, out var upload))
            {
                return Results.BadRequest(new { error = "Invalid speed values" });
            }

            double? ping = double.TryParse(GetValue("p"), out var p) ? p : null;
            double? jitter = double.TryParse(GetValue("j"), out var j) ? j : null;
            double? downloadData = double.TryParse(GetValue("dd"), out var dd) ? dd : null;
            double? uploadData = double.TryParse(GetValue("ud"), out var ud) ? ud : null;
            var userAgent = GetValue("ua") ?? context.Request.Headers.UserAgent.ToString();

            // Geolocation (optional)
            double? latitude = double.TryParse(GetValue("lat"), out var lat) ? lat : null;
            double? longitude = double.TryParse(GetValue("lng"), out var lng) ? lng : null;
            int? locationAccuracy = int.TryParse(GetValue("acc"), out var acc) ? acc : null;

            // Test duration per direction (seconds)
            int? duration = int.TryParse(GetValue("dur"), out var dur) ? dur : null;

            // External server identifier (WAN speed tests from remote OpenSpeedTest servers)
            var externalServerId = GetValue("srv");

            // Optional site slug (multi-site: one WAN speed test server serving many sites, or an
            // agent relaying a LAN client's test). Cross-origin posts carry no site cookie, so the
            // slug rides as a parameter. An empty slug is the default site's own page. A non-empty
            // slug that does not resolve to a provisioned site (a removed/renamed site, or a
            // misconfigured/leftover relay) would silently pollute the main site if recorded there,
            // so log and drop it rather than falling back.
            var siteSlug = GetValue("site")?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(siteSlug))
            {
                if (!NetworkOptimizer.Core.Helpers.StringUtilities.IsSlug(siteSlug))
                {
                    loggerFactory.CreateLogger("SpeedTestRelay")
                        .LogWarning("Dropping relayed speed test result: '{Slug}' is not a valid site slug", siteSlug);
                    return Results.BadRequest(new { error = "Invalid site slug" });
                }
                if (!siteDbFactory.SiteDbExists(siteSlug))
                {
                    loggerFactory.CreateLogger("SpeedTestRelay")
                        .LogWarning("Dropping relayed speed test result: site '{Slug}' is not provisioned on this server", siteSlug);
                    return Results.NotFound(new { error = $"Site '{siteSlug}' is not provisioned" });
                }
            }

            // An agent relay posts on behalf of a LAN client and passes the real client
            // address as a param (a reverse-proxied / port-mapped central server rewrites
            // X-Forwarded-For to the site's public IP, so the header can't carry it).
            // Trust the param only for slug-tagged (agent-relayed) posts; direct clients
            // still use the connection/X-Forwarded-For address.
            var relayedClientIp = GetValue("client_ip");
            var clientIp = !string.IsNullOrEmpty(siteSlug) && !string.IsNullOrEmpty(relayedClientIp)
                ? relayedClientIp!
                : EndpointHelpers.GetClientIp(context);

            // The owning site's service instance stores to that site's database and
            // enriches against that site's console. Cross-origin posts carry no site
            // cookie, so the slug parameter picks the instance here.
            var service = speedTestRegistry
                .GetFor(string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug)
                .ClientSpeedTest;
            Iperf3Result result;
            try
            {
                result = await service.RecordOpenSpeedTestResultAsync(
                    clientIp, download, upload, ping, jitter, downloadData, uploadData, userAgent,
                    latitude, longitude, locationAccuracy, duration, externalServerId);
            }
            catch (NetworkOptimizer.Web.Services.Licensing.LicenseRestrictedException)
            {
                // Recording a new result is an operation, so a restricted site declines it. This is a
                // policy refusal, not a server fault: without the catch it surfaces as a 500 and reads
                // like the relay is broken.
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Check if this is a new high score for download speed on this device
            // The "d" param from JS is always the client's download. Due to server perspective swap:
            //   BrowserToServer: client download stored as UploadBitsPerSecond
            //   OpenSpeedTestWan: client download stored as DownloadBitsPerSecond
            // TODO: Also check if it's the highest score for any device on the same AP (isApHighScore).
            //       Requires AP MAC on the result, which is only available after background enrichment.
            var isHighScore = false;
            try
            {
                await using var db = siteSlug != null
                    ? siteDbFactory.CreateForSite(siteSlug)
                    : await dbFactory.CreateDbContextAsync();
                var direction = result.Direction;
                var deviceResults = db.Iperf3Results
                    .Where(r => r.DeviceHost == result.DeviceHost && r.Direction == direction && r.Success)
                    .ToList();

                if (deviceResults.Count >= 3)
                {
                    // Get client-perspective download speed for comparison
                    double GetClientDownload(Iperf3Result r) =>
                        r.Direction == SpeedTestDirection.BrowserToServer
                            ? r.UploadBitsPerSecond   // server's upload = client's download
                            : r.DownloadBitsPerSecond; // WAN: stored as client's download

                    var thisDownload = GetClientDownload(result);
                    var previousMax = deviceResults
                        .Where(r => r.Id != result.Id)
                        .Select(GetClientDownload)
                        .DefaultIfEmpty(0)
                        .Max();

                    isHighScore = thisDownload > previousMax && previousMax > 0;
                }
            }
            catch
            {
                // Non-critical feature - don't fail the response
            }

            return Results.Ok(new
            {
                success = true,
                id = result.Id,
                clientIp = result.DeviceHost,
                clientName = result.DeviceName,
                download = result.DownloadMbps,
                upload = result.UploadMbps,
                isHighScore
            });
        }).RequireCors("SpeedTestCors").RequireRateLimiting("PublicSpeedTest");

        // Public endpoint for an agent to relay a client-initiated iperf3 test its local iperf3 -s
        // captured (the agent parsed nothing - it forwards the raw -J JSON). The central iperf3
        // server records default-site tests directly; this lands a secondary site's tests in its own
        // database via the same shared recorder. Client IP, direction, and throughput all come from
        // the iperf3 JSON, so only the raw JSON + site slug are needed. Distinct from the
        // NO-initiated LAN test (Iperf3SpeedTestService), which the server orchestrates and stores
        // separately.
        app.MapPost("/api/public/speedtest/iperf3-results", async (HttpContext context,
            SpeedTestServiceRegistry speedTestRegistry,
            NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("Iperf3ClientRelay");

            // Site routing. This endpoint is used ONLY by on-site agents relaying a captured
            // iperf3 -s result - the central server records its own default-site tests directly,
            // never over HTTP. An empty slug is the default site's own relay and lands on the
            // default site. A NON-empty slug that does not resolve to a provisioned site means a
            // misconfigured, renamed, or removed agent site; recording it to the default site
            // would silently pollute the main site, so log and drop it rather than falling back.
            var siteSlug = context.Request.Query["site"].ToString().Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(siteSlug))
            {
                if (!NetworkOptimizer.Core.Helpers.StringUtilities.IsSlug(siteSlug))
                {
                    logger.LogWarning("Dropping relayed iperf3 result: '{Slug}' is not a valid site slug", siteSlug);
                    return Results.BadRequest(new { error = "Invalid site slug" });
                }
                if (!siteDbFactory.SiteDbExists(siteSlug))
                {
                    logger.LogWarning("Dropping relayed iperf3 result: site '{Slug}' is not provisioned on this server", siteSlug);
                    return Results.NotFound(new { error = $"Site '{siteSlug}' is not provisioned" });
                }
            }

            using var reader = new StreamReader(context.Request.Body);
            var json = await reader.ReadToEndAsync();
            if (string.IsNullOrWhiteSpace(json))
                return Results.BadRequest(new { error = "Missing iperf3 JSON body" });

            var clientSpeedTest = speedTestRegistry
                .GetFor(string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug)
                .ClientSpeedTest;
            try
            {
                await Iperf3ClientResultRecorder.RecordAsync(clientSpeedTest, json, logger);
            }
            catch (NetworkOptimizer.Web.Services.Licensing.LicenseRestrictedException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new { success = true });
        }).RequireCors("SpeedTestCors").RequireRateLimiting("PublicSpeedTest");

        // Public endpoint for capturing topology snapshots during speed tests
        // Called by OpenSpeedTest ~3 seconds into a test to capture wireless rates mid-test
        app.MapPost("/api/public/speedtest/topology-snapshots", (HttpContext context,
            SpeedTestServiceRegistry speedTestRegistry,
            NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
            NetworkOptimizer.Web.Services.Licensing.LicenseStateService licenseState,
            ILoggerFactory loggerFactory) =>
        {
            // Same optional site routing as the results endpoint: a slug-tagged test
            // captures the snapshot from that site's console.
            var siteSlug = context.Request.Query["site"].ToString().Trim().ToLowerInvariant();
            // A non-empty slug that does not resolve to a provisioned site is a stray/misconfigured
            // relay. Capturing against the default site's console would leave an orphan snapshot (the
            // paired result is dropped, see the results endpoint) and query the wrong console, so log
            // and no-op. Fire-and-forget, so the browser ignores this response anyway.
            if (!string.IsNullOrEmpty(siteSlug) &&
                (!NetworkOptimizer.Core.Helpers.StringUtilities.IsSlug(siteSlug) || !siteDbFactory.SiteDbExists(siteSlug)))
            {
                loggerFactory.CreateLogger("SpeedTestRelay")
                    .LogWarning("Skipping topology snapshot: site '{Slug}' is not provisioned on this server", siteSlug);
                return Results.Ok(new { success = false, skipped = "unprovisioned site" });
            }
            // Empty slug = the default site's own page; a non-empty slug here is provisioned.
            var relayed = !string.IsNullOrEmpty(siteSlug);
            if (!relayed)
            {
                siteSlug = SiteManagementService.DefaultSiteSlug;
            }

            // Relayed posts carry the real client IP as a param (see the results
            // endpoint); it MUST match the IP the result posts under so the mid-test
            // snapshot keys line up for the merge in AnalyzePathAsync.
            // Capturing a snapshot queries the site's console and writes to its database, so a
            // restricted site does neither. Declined the same way as an unprovisioned site rather
            // than by throwing: this is fire-and-forget and the browser ignores the response.
            if (!licenseState.IsSiteOperational(siteSlug))
            {
                loggerFactory.CreateLogger("SpeedTestRelay")
                    .LogDebug("Skipping topology snapshot: site '{Slug}' is not operational", siteSlug);
                return Results.Ok(new { success = false, skipped = "site not operational" });
            }

            var relayedClientIp = context.Request.Query["client_ip"].ToString();
            var clientIp = relayed && !string.IsNullOrEmpty(relayedClientIp)
                ? relayedClientIp
                : EndpointHelpers.GetClientIp(context);
            var snapshotService = speedTestRegistry.GetFor(siteSlug).Snapshots;

            // Fire-and-forget - capture snapshot asynchronously, don't block response
            _ = snapshotService.CaptureSnapshotAsync(siteSlug, clientIp);

            return Results.Ok(new { success = true });
        }).RequireCors("SpeedTestCors").RequireRateLimiting("PublicSpeedTest");

        // Authenticated endpoint for viewing client speed test results
        read.MapGet("/api/speedtest/client-results", async (IClientSpeedTestService service, string? ip = null, string? mac = null, int count = 50) =>
        {
            if (count < 1) count = 1;
            if (count > 1000) count = 1000;

            // Filter by IP if provided
            if (!string.IsNullOrWhiteSpace(ip))
                return Results.Ok(await service.GetResultsByIpAsync(ip, count));

            // Filter by MAC if provided
            if (!string.IsNullOrWhiteSpace(mac))
                return Results.Ok(await service.GetResultsByMacAsync(mac, count));

            // Return all results
            return Results.Ok(await service.GetResultsAsync(count));
        });

        // Authenticated endpoint for viewing WAN client speed test results (external OpenSpeedTest servers)
        read.MapGet("/api/speedtest/wan-client-results", async (IClientSpeedTestService service, int count = 50, int hours = 0) =>
        {
            if (count < 1) count = 1;
            if (count > 1000) count = 1000;

            return Results.Ok(await service.GetWanResultsAsync(count, hours));
        });

        // Authenticated endpoint for deleting a client speed test result
        admin.MapDelete("/api/speedtest/client-results/{id:int}", async (int id, IClientSpeedTestService service) =>
        {
            var deleted = await service.DeleteResultAsync(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
