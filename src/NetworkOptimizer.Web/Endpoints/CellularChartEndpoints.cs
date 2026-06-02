using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// REST endpoint for cellular modem signal time-series data.
/// Returns per-band series (e.g. separate LTE and NR5G lines for NSA modems).
/// Mirrors the SFP chart endpoint pattern.
/// </summary>
public static class CellularChartEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/cellular-chart", async (
            MonitoringInfluxClient influx,
            CellularModemService modemService,
            int? rangeHours,
            DateTime? from,
            DateTime? to,
            string? modemId,
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

            var data = await influx.QueryCellularAsync(queryFrom, queryTo, modemId, ct: ct);

            var modems = await modemService.GetModemsAsync();
            var nameMap = modems.ToDictionary(m => m.Id.ToString(), m => m.Name);

            // Keys are "modemId" or "modemId:mode" (e.g. "3:LTE", "3:5G NSA")
            var result = data.Select(kvp =>
            {
                var parts = kvp.Key.Split(':', 2);
                var rawModemId = parts[0];
                var mode = parts.Length > 1 ? parts[1] : null;
                nameMap.TryGetValue(rawModemId, out var modemName);
                modemName ??= $"Modem {rawModemId}";

                var label = !string.IsNullOrEmpty(mode)
                    ? $"{modemName} ({mode})"
                    : modemName;

                return new
                {
                    id = kvp.Key,
                    modemId = rawModemId,
                    label,
                    mode,
                    data = kvp.Value.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        rsrp = p.Rsrp,
                        rsrq = p.Rsrq,
                        snr = p.Snr,
                        rssi = p.Rssi,
                        quality = p.SignalQuality,
                        band = p.Band,
                        carrier = p.Carrier,
                    })
                };
            });

            return Results.Ok(new { modems = result });
        });
    }
}
