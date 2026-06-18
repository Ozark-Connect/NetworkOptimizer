using System.Diagnostics;
using NetworkOptimizer.Web.Services.Monitoring;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Flaky-target detector (#849) result, with a server-side timing field so we can measure the cost
/// of the 10-min-bin / 48 h loss sweep on real data. The Blazor banner + panel call
/// <see cref="FlakyTargetService"/> directly; this endpoint exists for timing/validation and as a
/// JS-hittable view of the result. Authenticated like the rest of /api/monitoring.
/// </summary>
public static class FlakyTargetEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/flaky-targets", async (FlakyTargetService svc, CancellationToken ct) =>
        {
            var sw = Stopwatch.StartNew();
            var flaky = await svc.DetectAsync(ct);
            sw.Stop();
            return Results.Ok(new
            {
                elapsedMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
                count = flaky.Count,
                targets = flaky.Select(f => new
                {
                    f.Name,
                    type = f.Type.ToString(),
                    lossPct = Math.Round(f.LossPct, 2),
                    baselinePct = Math.Round(f.BaselinePct, 2),
                    f.OverBins,
                    f.TotalBins,
                    evidence = f.Evidence
                })
            });
        });
    }
}
