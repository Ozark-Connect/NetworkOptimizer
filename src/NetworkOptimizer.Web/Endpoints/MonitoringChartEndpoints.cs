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
            DateTime? from,
            DateTime? to,
            CancellationToken ct) =>
        {
            var targetType = category switch
            {
                "AccessIsp" => MonitoringTargetType.AccessIsp,
                "Transit" => MonitoringTargetType.Transit,
                "InternetService" => MonitoringTargetType.InternetService,
                _ => MonitoringTargetType.Fabric
            };
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

            // Target names come from SQLite; time-series data from InfluxDB via
            // the target_type tag (indexed, ~10ms) instead of contains() on
            // target_id set (full scan, ~400ms+).
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == targetType && t.Enabled)
                .OrderBy(t => t.Name)
                .Select(t => new { t.TargetId, t.Name })
                .ToListAsync(ct);

            if (targets.Count == 0)
                return Results.Ok(new { targets = Array.Empty<object>() });

            var targetLookup = targets.ToDictionary(t => t.TargetId, t => t.Name);
            var data = await influx.QueryLatencyByTargetTypeAsync(targetType, queryFrom, queryTo, ct: ct);

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

        app.MapGet("/api/monitoring/wan-rate-chart", async (
            MonitoringInfluxClient influx,
            UniFiConnectionService connectionService,
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

            string? gatewayMac = null;
            List<string>? wanIfNames = null;
            try
            {
                var devices = await connectionService.GetDiscoveredDevicesAsync(ct);
                var gw = devices?.FirstOrDefault(d =>
                    d.Type == NetworkOptimizer.Core.Enums.DeviceType.Gateway
                    || d.HardwareType == NetworkOptimizer.Core.Enums.DeviceType.Gateway);
                gatewayMac = gw?.Mac;
                wanIfNames = gw?.WanInterfaceNames;
            }
            catch { }

            if (string.IsNullOrEmpty(gatewayMac) || wanIfNames == null || wanIfNames.Count == 0)
                return Results.Ok(new { download = Array.Empty<object>(), upload = Array.Empty<object>() });

            var data = await influx.QueryGatewayWanRatesAsync(gatewayMac, wanIfNames, queryFrom, queryTo, ct: ct);

            return Results.Ok(new
            {
                download = data.Select(p => new { time = p.Time.ToString("o"), value = p.DownloadBps }),
                upload = data.Select(p => new { time = p.Time.ToString("o"), value = p.UploadBps })
            });
        });
    }
}
