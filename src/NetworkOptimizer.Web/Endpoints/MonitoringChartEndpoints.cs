using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class MonitoringChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/chart-data", async (
            MonitoringInfluxClient influx,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            string? category,
            int? rangeHours,
            CancellationToken ct) =>
        {
            var targetType = category switch
            {
                "AccessIsp" => MonitoringTargetType.AccessIsp,
                "Transit" => MonitoringTargetType.Transit,
                "InternetService" => MonitoringTargetType.InternetService,
                _ => MonitoringTargetType.Fabric
            };
            var hours = rangeHours ?? 1;
            var now = DateTime.UtcNow;
            var from = hours == 0 ? now.AddMinutes(-15) : now.AddHours(-hours);

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == targetType && t.Enabled)
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { targets = Array.Empty<object>() });

            var ids = targets.Select(t => t.TargetId);
            var data = await influx.QueryLatencyMultiTargetAsync(ids, from, now, ct: ct);

            var result = targets.Select(t =>
            {
                data.TryGetValue(t.TargetId, out var points);
                var pts = points ?? new List<MonitoringInfluxClient.LatencyPoint>();
                return new
                {
                    targetId = t.TargetId,
                    name = t.Name,
                    rtt = pts.Select(p => new { time = p.Time.ToString("o"), value = p.RttAvgMs }),
                    loss = pts.Select(p => new { time = p.Time.ToString("o"), value = p.LossPercent }),
                };
            });

            return Results.Ok(new { targets = result });
        });
    }
}
