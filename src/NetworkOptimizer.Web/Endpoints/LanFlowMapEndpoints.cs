using System.Text.Json;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.LanFlowMap;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class LanFlowMapEndpoints
{
    private record DevicePlacementRequest(string Mac, double Latitude, double Longitude, int? Floor, double? HeightM);

    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): every endpoint is mapped onto a group that carries its
        // authorization policy, which is what architecture test A1 checks. Reads are any
        // authenticated user, running a test is Operator, and changes are Admin.
        var read = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);
        var admin = app.MapGroup("").RequireAuthorization(Policies.RequireAdmin);

        read.MapGet("/api/monitoring/lan-flow-map/snapshot",
            async (LanFlowMapService svc, ILogger<LanFlowMapService> logger, CancellationToken ct) =>
            {
                try
                {
                    var snap = await svc.BuildSnapshotAsync(ct);
                    return Results.Ok(snap);
                }
                catch (Exception ex) when (IsConsoleUnavailable(ex))
                {
                    return ConsoleUnavailable(logger, "snapshot", ex);
                }
            });

        read.MapGet("/api/monitoring/lan-flow-map/live",
            async (LanFlowMapService svc, ILogger<LanFlowMapService> logger, CancellationToken ct) =>
            {
                try
                {
                    var update = await svc.GetLiveUpdateAsync(ct);
                    return Results.Ok(update);
                }
                catch (Exception ex) when (IsConsoleUnavailable(ex))
                {
                    return ConsoleUnavailable(logger, "live", ex);
                }
            });

        read.MapGet("/api/monitoring/lan-flow-map/history",
            async (LanFlowMapService svc, ILogger<LanFlowMapService> logger, DateTime at, CancellationToken ct) =>
            {
                try
                {
                    var update = await svc.GetHistoricUpdateAsync(at, ct);
                    return Results.Ok(update);
                }
                catch (Exception ex) when (IsConsoleUnavailable(ex))
                {
                    return ConsoleUnavailable(logger, "history", ex);
                }
            });

        read.MapGet("/api/monitoring/lan-flow-map/history/range",
            async (LanFlowMapService svc, CancellationToken ct) =>
            {
                var earliest = await svc.GetHistoryStartAsync(ct);
                return Results.Ok(new { earliest });
            });

        read.MapGet("/api/monitoring/lan-flow-map/speed-tests",
            async (LanFlowMapService svc, DateTime? since, DateTime? until, CancellationToken ct) =>
            {
                var fromT = since ?? DateTime.UtcNow.AddDays(-30);
                var toT = until ?? DateTime.UtcNow;
                var items = await svc.BuildSpeedTestOverlayAsync(fromT, toT, limitPerKind: 10, ct: ct);
                return Results.Ok(items);
            });

        // Placing a device is the same act as placing an AP, and IApMapAdminService already says
        // who may: Site Operator on the site in context. Going through the interface rather than
        // the class puts this endpoint on that gate instead of demanding install-wide Admin, which
        // no Site Admin or Operator could satisfy.
        read.MapPost("/api/monitoring/lan-flow-map/device-placement",
            async (DevicePlacementRequest req, IApMapAdminService apMap, LanFlowMapService svc) =>
            {
                if (string.IsNullOrWhiteSpace(req.Mac))
                    return Results.BadRequest("mac is required");
                await apMap.SaveApLocationAsync(req.Mac, req.Latitude, req.Longitude, req.Floor, req.HeightM);
                svc.InvalidateCache();
                return Results.Ok();
            });
    }

    /// <summary>
    /// True for failures that mean the UniFi Console couldn't serve the request right now:
    /// non-JSON bodies (login/error page during reboot or firmware upgrade) or transport
    /// errors. These are transient, so the map endpoints answer 503 and the JS poller
    /// quietly retries on its next tick instead of surfacing an unhandled 500.
    /// </summary>
    private static bool IsConsoleUnavailable(Exception ex) =>
        ex is JsonException or HttpRequestException;

    private static IResult ConsoleUnavailable(ILogger logger, string endpoint, Exception ex)
    {
        logger.LogDebug(ex, "LAN flow map {Endpoint} unavailable - UniFi Console not serving data", endpoint);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}
