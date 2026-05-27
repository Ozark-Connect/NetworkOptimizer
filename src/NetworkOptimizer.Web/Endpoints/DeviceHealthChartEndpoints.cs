using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class DeviceHealthChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/device-health-chart", async (
            MonitoringInfluxClient influx,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            DateTime queryFrom, queryTo;
            if (from.HasValue && to.HasValue)
            {
                queryFrom = from.Value.ToUniversalTime();
                queryTo = to.Value.ToUniversalTime();
            }
            else
            {
                var hours = rangeHours ?? 1;
                queryTo = DateTime.UtcNow;
                queryFrom = hours == 0 ? queryTo.AddMinutes(-15) : queryTo.AddHours(-hours);
            }

            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric)
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name, t.DeviceMac })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { devices = Array.Empty<object>() });

            var result = new List<object>();
            foreach (var t in targets)
            {
                if (string.IsNullOrEmpty(t.DeviceMac)) continue;
                var points = await influx.QueryDeviceHealthAsync(t.DeviceMac, queryFrom, queryTo, ct: ct);
                result.Add(new
                {
                    name = t.Name,
                    mac = t.DeviceMac,
                    data = points.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        cpu = p.CpuPercent,
                        mem = p.MemoryUsedPercent,
                        temp = p.TemperatureC
                    })
                });
            }

            return Results.Ok(new { devices = result });
        });
    }
}
