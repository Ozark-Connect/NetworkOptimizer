using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
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
            IOntMonitorService ontService,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
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
            }).ToList();

            // Only the ONTs that actually became a series can be marked. An event keyed to
            // anything else would pass the chart's visibility filter, which treats an unknown key
            // as visible, and draw a mark belonging to no line on the plot.
            var chartedOnts = data.Keys
                .Where(nameMap.ContainsKey)
                .ToDictionary(id => id, id => nameMap[id]);

            var events = await BuildOntEventsAsync(siteDbFactory, siteContext, chartedOnts, queryFrom, queryTo, ct);

            return Results.Ok(new { devices = result, events });
        });
    }

    /// <summary>
    /// Marks for the charted ONTs over the same window as the series.
    ///
    /// ONT alerts carry their config id in the event context, which is the same id the series is
    /// keyed by, so the match needs nothing else. Attached ONTs drop out for free: this tab only
    /// charts standalone configs, so an attached one matches no series and its alerts stay on SFP
    /// Stats where its charts are.
    /// </summary>
    private static async Task<List<object>> BuildOntEventsAsync(
        SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        Dictionary<string, string> chartedOnts,
        DateTime from,
        DateTime to,
        CancellationToken ct)
    {
        if (chartedOnts.Count == 0) return new List<object>();

        await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);

        var alerts = await db.AlertHistory.AsNoTracking()
            .Where(a => a.ContextJson != null && a.TriggeredAt >= from && a.TriggeredAt <= to
                && ChartEventMarks.OntEventTypes.Contains(a.EventType))
            .Select(a => new { a.EventType, a.Severity, a.Title, a.Message, a.DeviceName, a.ContextJson, a.TriggeredAt })
            .ToListAsync(ct);

        var events = new List<(DateTime At, object Payload)>();
        foreach (var alert in alerts)
        {
            string? ontId = null;
            try
            {
                var context = JsonSerializer.Deserialize<Dictionary<string, string>>(alert.ContextJson!);
                context?.TryGetValue("ont_id", out ontId);
            }
            catch (JsonException)
            {
                // A context we cannot read costs one mark, not the whole tab.
                continue;
            }

            if (string.IsNullOrEmpty(ontId) || !chartedOnts.TryGetValue(ontId, out var ontName)) continue;

            // Written as UtcNow but read back from SQLite as Unspecified, and "o" on an
            // Unspecified value emits no zone - which the browser then reads as local time.
            var triggeredAtUtc = DateTime.SpecifyKind(alert.TriggeredAt, DateTimeKind.Utc);

            events.Add((triggeredAtUtc, new
            {
                key = ontId,
                device = ontName,
                time = triggeredAtUtc.ToString("o"),
                kind = "alert",
                severity = ChartEventMarks.Severity(alert.Severity),
                title = ChartEventMarks.OntEventLabel(alert.Title, alert.DeviceName),
                detail = alert.Message,
            }));
        }

        // The mark layer folds by proximity, which assumes time order.
        return events.OrderBy(e => e.At).Select(e => e.Payload).ToList();
    }

    private static long? Delta(long? cur, long? prev) =>
        cur is long c && prev is long p && c >= p ? c - p : null;
}
