using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class SfpChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/sfp-chart", async (
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
            var sfps = await db.MonitoredSfps.AsNoTracking()
                .Where(s => s.IsMonitoredOnt)
                .OrderBy(s => s.DeviceMac).ThenBy(s => s.PortName)
                .ToListAsync(ct);

            if (sfps.Count == 0)
                return Results.Ok(new { modules = Array.Empty<object>() });

            var modules = sfps.Select(s => (s.DeviceMac, s.PortName)).ToList();
            var data = await influx.QuerySfpByModulesAsync(modules, queryFrom, queryTo, ct: ct);

            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric)
                .Select(t => new { t.DeviceMac, t.Name })
                .ToListAsync(ct);
            var nameMap = targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .GroupBy(t => t.DeviceMac!.Replace("-", ":").ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.First().Name);

            var result = sfps.Select(s =>
            {
                var key = $"{s.DeviceMac.Replace("-", ":").ToLowerInvariant()}:{s.PortName}";
                data.TryGetValue(key, out var points);
                var pts = points ?? new List<MonitoringInfluxClient.SfpPoint>();
                var deviceName = nameMap.TryGetValue(
                    s.DeviceMac.Replace("-", ":").ToLowerInvariant(), out var n) ? n : s.DeviceMac;
                var label = !string.IsNullOrEmpty(s.FriendlyName)
                    ? s.FriendlyName
                    : $"{deviceName} port {s.PortName}";

                return new
                {
                    id = key,
                    label,
                    isPon = s.IsPon,
                    sfpPart = s.SfpPart,
                    data = pts.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        rx = p.RxPowerDbm,
                        tx = p.TxPowerDbm,
                        temp = p.TemperatureC,
                        voltage = p.VoltageV
                    })
                };
            });

            return Results.Ok(new { modules = result });
        });
    }
}
