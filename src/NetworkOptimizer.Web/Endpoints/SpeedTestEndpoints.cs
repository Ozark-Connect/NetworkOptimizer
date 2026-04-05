using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class SpeedTestEndpoints
{
    public static void MapSpeedTestEndpoints(this WebApplication app)
    {
        // --- LAN iperf3 Speed Test ---

        app.MapGet("/api/speedtest/devices", async (Iperf3SpeedTestService service) =>
        {
            var devices = await service.GetDevicesAsync();
            return Results.Ok(devices);
        });

        app.MapPost("/api/speedtest/devices/{deviceId:int}/results", async (int deviceId, Iperf3SpeedTestService service) =>
        {
            var devices = await service.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.Id == deviceId);
            if (device == null)
                return Results.NotFound(new { error = "Device not found" });

            var result = await service.RunSpeedTestAsync(device);
            return Results.Ok(result);
        });

        app.MapGet("/api/speedtest/results", async (Iperf3SpeedTestService service, string? deviceHost = null, int count = 50) =>
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
        app.MapPost("/api/public/speedtest/results", async (HttpContext context, ClientSpeedTestService service,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory) =>
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

            var clientIp = EndpointHelpers.GetClientIp(context);

            var result = await service.RecordOpenSpeedTestResultAsync(
                clientIp, download, upload, ping, jitter, downloadData, uploadData, userAgent,
                latitude, longitude, locationAccuracy, duration, externalServerId);

            // Check if this is a new high score for download speed on this device
            // The "d" param from JS is always the client's download. Due to server perspective swap:
            //   BrowserToServer: client download stored as UploadBitsPerSecond
            //   OpenSpeedTestWan: client download stored as DownloadBitsPerSecond
            // TODO: Also check if it's the highest score for any device on the same AP (isApHighScore).
            //       Requires AP MAC on the result, which is only available after background enrichment.
            var isHighScore = false;
            try
            {
                await using var db = await dbFactory.CreateDbContextAsync();
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
        }).RequireCors("SpeedTestCors");

        // Public endpoint for capturing topology snapshots during speed tests
        // Called by OpenSpeedTest ~3 seconds into a test to capture wireless rates mid-test
        app.MapPost("/api/public/speedtest/topology-snapshots", async (HttpContext context, ITopologySnapshotService snapshotService) =>
        {
            var clientIp = EndpointHelpers.GetClientIp(context);

            // Fire-and-forget - capture snapshot asynchronously, don't block response
            _ = snapshotService.CaptureSnapshotAsync(clientIp);

            return Results.Ok(new { success = true });
        }).RequireCors("SpeedTestCors");

        // Authenticated endpoint for viewing client speed test results
        app.MapGet("/api/speedtest/client-results", async (ClientSpeedTestService service, string? ip = null, string? mac = null, int count = 50) =>
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
        app.MapGet("/api/speedtest/wan-client-results", async (ClientSpeedTestService service, int count = 50, int hours = 0) =>
        {
            if (count < 1) count = 1;
            if (count > 1000) count = 1000;

            return Results.Ok(await service.GetWanResultsAsync(count, hours));
        });

        // Authenticated endpoint for deleting a client speed test result
        app.MapDelete("/api/speedtest/client-results/{id:int}", async (int id, ClientSpeedTestService service) =>
        {
            var deleted = await service.DeleteResultAsync(id);
            return deleted ? Results.NoContent() : Results.NotFound();
        });
    }
}
