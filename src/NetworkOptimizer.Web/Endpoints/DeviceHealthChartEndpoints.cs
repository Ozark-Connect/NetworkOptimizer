using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
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
                return Results.Ok(new { devices = Array.Empty<object>(), customFields = Array.Empty<object>() });

            var deviceMacs = targets
                .Where(t => !string.IsNullOrEmpty(t.DeviceMac))
                .Select(t => t.DeviceMac!)
                .ToList();

            var customOidConfigs = await db.CustomOidConfigurations
                .Where(c => c.Enabled && c.Scope == Storage.Models.CustomOidScope.DeviceLevel
                    && deviceMacs.Contains(c.DeviceMac))
                .ToListAsync(ct);

            var customFieldDefs = customOidConfigs
                .GroupBy(c => c.FieldName)
                .Select(g => new { fieldName = g.Key, description = g.First().Description ?? g.Key })
                .ToList();

            var customFieldNames = customFieldDefs.Select(f => f.fieldName).ToList();

            var result = new List<object>();
            foreach (var t in targets)
            {
                if (string.IsNullOrEmpty(t.DeviceMac)) continue;
                var points = await influx.QueryDeviceHealthAsync(t.DeviceMac, queryFrom, queryTo, ct: ct);

                Dictionary<string, List<(DateTime Time, double Value)>>? customData = null;
                var deviceCustomFields = customOidConfigs
                    .Where(c => c.DeviceMac == t.DeviceMac)
                    .Select(c => c.FieldName)
                    .Distinct()
                    .ToList();
                if (deviceCustomFields.Count > 0)
                    customData = await influx.QueryCustomOidFieldsAsync(
                        t.DeviceMac, deviceCustomFields, queryFrom, queryTo, ct: ct);

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
                    }),
                    custom = customData?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.Select(v => new { time = v.Time.ToString("o"), value = v.Value }))
                });
            }

            return Results.Ok(new { devices = result, customFields = customFieldDefs });
        });
    }
}
