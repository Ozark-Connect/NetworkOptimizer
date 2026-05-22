using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Endpoints;

public static class WifiInterestEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/monitoring/wifi-interest", (
            WifiClientInterestTracker tracker,
            WifiInterestRequest request) =>
        {
            if (request.ClientMacs is { Count: > 0 })
                tracker.Heartbeat(request.ClientMacs);
            return Results.Ok();
        });
    }

    public record WifiInterestRequest(List<string>? ClientMacs);
}
