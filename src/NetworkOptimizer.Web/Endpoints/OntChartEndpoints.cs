using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoint for external ONT time-series data.
/// Returns RX/TX power, temperature, voltage - same shape as SFP DDM.
/// </summary>
public static class OntChartEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/ont-chart", async (
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

            // Standalone configs only: an attached config writes to the sfp
            // measurement, not ont, and must never appear as a standalone ONT series.
            var configs = await ontService.GetStandaloneConfigsAsync();
            var nameMap = configs.ToDictionary(c => c.Id.ToString(), c => c.Name);

            // Only surface ONTs that still have a config. Deleting an ONT config
            // leaves its historical series in InfluxDB; without this filter those
            // orphaned ont_ids show up as phantom "ONT {id}" entries on the chart.
            var result = data
                .Where(kvp => nameMap.ContainsKey(kvp.Key))
                .Select(kvp =>
            {
                var name = nameMap[kvp.Key];

                // FEC/BIP counters are cumulative; the chart wants per-interval deltas
                // (CM Stats style). A negative step is a device counter reset - null
                // (a gap), not a bogus spike.
                var pts = kvp.Value.OrderBy(p => p.Time).ToList();
                var items = new List<object>(pts.Count);
                MonitoringInfluxClient.OntPoint? prev = null;
                foreach (var p in pts)
                {
                    items.Add(new
                    {
                        time = p.Time.ToString("o"),
                        rx = p.RxPowerDbm,
                        tx = p.TxPowerDbm,
                        temp = p.TemperatureC,
                        voltage = p.VoltageV,
                        bias = p.BiasMa,
                        fec = Delta(p.FecErrors, prev?.FecErrors),
                        bip = Delta(p.BipErrors, prev?.BipErrors),
                    });
                    prev = p;
                }

                return new
                {
                    id = kvp.Key,
                    label = name,
                    data = items,
                };
            });

            return Results.Ok(new { devices = result });
        });
    }

    private static long? Delta(long? cur, long? prev) =>
        cur is long c && prev is long p && c >= p ? c - p : null;
}
