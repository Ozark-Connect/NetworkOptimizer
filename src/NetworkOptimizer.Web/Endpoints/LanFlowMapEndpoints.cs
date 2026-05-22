using NetworkOptimizer.Web.Services.LanFlowMap;

namespace NetworkOptimizer.Web.Endpoints;

public static class LanFlowMapEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/lan-flow-map/snapshot",
            async (LanFlowMapService svc, CancellationToken ct) =>
            {
                var snap = await svc.BuildSnapshotAsync(ct);
                return Results.Ok(snap);
            });

        app.MapGet("/api/monitoring/lan-flow-map/live",
            async (LanFlowMapService svc, CancellationToken ct) =>
            {
                var update = await svc.GetLiveUpdateAsync(ct);
                return Results.Ok(update);
            });

        app.MapGet("/api/monitoring/lan-flow-map/history",
            async (LanFlowMapService svc, DateTime at, CancellationToken ct) =>
            {
                var update = await svc.GetHistoricUpdateAsync(at, ct);
                return Results.Ok(update);
            });

        app.MapGet("/api/monitoring/lan-flow-map/speed-tests",
            async (LanFlowMapService svc, DateTime? since, DateTime? until, CancellationToken ct) =>
            {
                var fromT = since ?? DateTime.UtcNow.AddHours(-24);
                var toT = until ?? DateTime.UtcNow;
                var items = await svc.BuildSpeedTestOverlayAsync(fromT, toT, limitPerKind: 10, ct: ct);
                return Results.Ok(items);
            });
    }
}
