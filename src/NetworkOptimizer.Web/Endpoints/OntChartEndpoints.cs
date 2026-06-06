using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoint for external ONT time-series data.
/// Returns RX/TX power, temperature, voltage - same shape as SFP DDM.
/// </summary>
public static class OntChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/ont-chart", async (
            MonitoringInfluxClient influx,
            OntMonitorService ontService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? ontId,
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

            var data = await influx.QueryOntAsync(queryFrom, queryTo, ontId, ct: ct);

            var configs = await ontService.GetConfigsAsync();
            var nameMap = configs.ToDictionary(c => c.Id.ToString(), c => c.Name);

            var result = data.Select(kvp =>
            {
                nameMap.TryGetValue(kvp.Key, out var name);
                name ??= $"ONT {kvp.Key}";

                return new
                {
                    id = kvp.Key,
                    label = name,
                    data = kvp.Value.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        rx = p.RxPowerDbm,
                        tx = p.TxPowerDbm,
                        temp = p.TemperatureC,
                        voltage = p.VoltageV,
                        bias = p.BiasMa,
                    })
                };
            });

            return Results.Ok(new { devices = result });
        });
    }
}
