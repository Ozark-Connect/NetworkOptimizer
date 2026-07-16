using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoints for Starlink terminal monitoring data: time-series metrics
/// for charting, the cached obstruction sky map, and live cached stats.
/// Mirrors the cellular/CM chart endpoint pattern.
/// </summary>
public static class StarlinkChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/starlink-chart", async (
            MonitoringInfluxClient influx,
            StarlinkMonitorService starlinkService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? starlinkId,
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

            var data = await influx.QueryStarlinkAsync(queryFrom, queryTo, starlinkId, ct: ct);

            var configs = await starlinkService.GetConfigsAsync();
            var nameMap = configs.ToDictionary(c => c.Id.ToString(), c => c.Name);

            // Only surface terminals that still have a config. Deleting a config
            // leaves its historical series in InfluxDB; without this filter those
            // orphaned starlink_ids show up as phantom entries on the chart.
            var result = data
                .Where(kvp => nameMap.ContainsKey(kvp.Key))
                .Select(kvp =>
            {
                var name = nameMap[kvp.Key];

                return new
                {
                    id = kvp.Key,
                    label = name,
                    data = kvp.Value.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        powerAvg = p.PowerInAvgW,
                        powerMax = p.PowerInMaxW,
                        dropAvg = p.PingDropRateAvg,
                        dropMax = p.PingDropRateMax,
                        obstructed = p.FractionObstructed,
                        outageSeconds = p.OutageSecondsDelta,
                        outageCount = p.OutageCountDelta,
                        gpsSats = p.GpsSats,
                        alignment = p.AlignmentOffsetDeg,
                        ethSpeed = p.EthSpeedMbps,
                        uptime = p.UptimeS,
                        alerts = p.AlertCount,
                    })
                };
            });

            return Results.Ok(new { devices = result });
        });

        app.MapGet("/api/monitoring/starlink/{id:int}/obstruction-map", (
            StarlinkMonitorService starlinkService,
            int id) =>
        {
            var map = starlinkService.GetCachedObstructionMap(id);
            if (map == null)
                return Results.NotFound();

            return Results.Ok(new
            {
                numRows = map.NumRows,
                numCols = map.NumCols,
                maxThetaDeg = map.MaxThetaDeg,
                snr = map.Snr,
                timestamp = map.Timestamp.ToString("o"),
            });
        });

        app.MapGet("/api/monitoring/starlink/stats", async (
            StarlinkMonitorService starlinkService) =>
        {
            var configs = await starlinkService.GetConfigsAsync();
            var cached = starlinkService.GetAllCachedStats();

            var terminals = configs.Select(c =>
            {
                cached.TryGetValue(c.Id, out var s);
                return new
                {
                    id = c.Id,
                    name = c.Name,
                    enabled = c.Enabled,
                    lastPolled = c.LastPolled?.ToString("o"),
                    lastError = c.LastError,
                    stats = s == null ? null : new
                    {
                        timestamp = s.Timestamp.ToString("o"),
                        uptimeSeconds = s.UptimeSeconds,
                        softwareVersion = s.SoftwareVersion,
                        hardwareVersion = s.HardwareVersion,
                        ethSpeedMbps = s.EthSpeedMbps,
                        powerInWatts = s.PowerInWatts,
                        powerInAvgWatts = s.PowerInAvgWatts,
                        powerInMaxWatts = s.PowerInMaxWatts,
                        fractionObstructed = s.FractionObstructed,
                        currentlyObstructed = s.CurrentlyObstructed,
                        pingDropRateAvg = s.PingDropRateAvg,
                        pingDropRateMax = s.PingDropRateMax,
                        gpsValid = s.GpsValid,
                        gpsSatellites = s.GpsSatellites,
                        alignmentOffsetDeg = StarlinkMonitorService.ComputeAlignmentOffsetDeg(s),
                        outageCountDelta = s.OutageCountDelta,
                        outageSecondsDelta = s.OutageSecondsDelta,
                        activeAlerts = s.ActiveAlerts,
                    },
                };
            });

            return Results.Ok(new { terminals });
        });
    }
}
