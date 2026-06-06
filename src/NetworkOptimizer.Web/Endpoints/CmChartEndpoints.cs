using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoint for cable modem aggregate time-series data.
/// Returns DS power, DS SNR, US power, and error counter deltas.
/// </summary>
public static class CmChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/cm-chart", async (
            MonitoringInfluxClient influx,
            CableModemMonitorService cmService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? cmId,
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

            var data = await influx.QueryCableModemAsync(queryFrom, queryTo, cmId, ct: ct);

            var configs = await cmService.GetConfigsAsync();
            var nameMap = configs.ToDictionary(c => c.Id.ToString(), c => c.Name);

            var result = data.Select(kvp =>
            {
                nameMap.TryGetValue(kvp.Key, out var name);
                name ??= $"CM {kvp.Key}";

                return new
                {
                    id = kvp.Key,
                    label = name,
                    data = kvp.Value.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        dsPower = p.DsPowerAvgDbmv,
                        dsSnr = p.DsSnrAvgDb,
                        usPower = p.UsPowerAvgDbmv,
                        lockedDs = p.LockedDsChannels,
                        lockedUs = p.LockedUsChannels,
                        corrDelta = p.CorrDelta,
                        uncorrDelta = p.UncorrDelta,
                    })
                };
            });

            return Results.Ok(new { devices = result });
        });
    }
}
