using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;
using NetworkOptimizer.Web.Services.Authorization;

namespace NetworkOptimizer.Web.Endpoints;

public static class SfpChartEndpoints
{
    public static void Map(WebApplication app)
    {
        // Gate 2 (design doc 06): the whole group carries authorization metadata, which is what
        // architecture test A1 checks. The policy short-circuits when the install has
        // authentication disabled (GlobalRoleHandler).
        var group = app.MapGroup("").RequireAuthorization(Policies.RequireViewer);

        group.MapGet("/api/monitoring/sfp-chart", async (
            MonitoringInfluxClient influx,
            SiteDbContextFactory siteDbFactory,
            SiteContextService siteContext,
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

            await using var db = siteDbFactory.CreateForSite(siteContext.Slug, siteContext.IsDefault);
            var sfps = await db.MonitoredSfps.AsNoTracking()
                .Where(s => s.IsMonitoredOnt)
                .OrderBy(s => s.DeviceMac).ThenBy(s => s.PortName)
                .ToListAsync(ct);

            if (sfps.Count == 0)
                return Results.Ok(new { modules = Array.Empty<object>() });

            var modules = sfps.Select(s => (s.DeviceMac, s.PortName)).ToList();
            var data = await influx.QuerySfpByModulesAsync(modules, queryFrom, queryTo, ct: ct);
            // Supplemental PON-layer series (attached ONT configs); empty for modules without one.
            var ponData = await influx.QuerySfpPonByModulesAsync(modules, queryFrom, queryTo, ct: ct);

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
                    category = (int)s.Category,
                    sfpPart = s.SfpPart,
                    data = pts.Select(p => new
                    {
                        time = p.Time.ToString("o"),
                        rx = p.RxPowerDbm,
                        tx = p.TxPowerDbm,
                        temp = p.TemperatureC,
                        voltage = p.VoltageV
                    }),
                    pon = BuildPonSeries(ponData.TryGetValue(key, out var ponPts) ? ponPts : null)
                };
            });

            return Results.Ok(new { modules = result });
        });
    }

    /// <summary>
    /// Project supplemental PON points into the chart payload, converting cumulative
    /// counters to per-interval deltas (CM Stats style - cumulative lines are unreadable).
    /// A negative step means the ONT rebooted and reset its counters; that interval's
    /// delta is null (a gap) rather than a bogus spike. Null when the module has no
    /// supplemental data, so the UI can hide the PON section entirely.
    /// </summary>
    private static List<object>? BuildPonSeries(List<MonitoringInfluxClient.SfpPonPoint>? points)
    {
        if (points is not { Count: > 0 }) return null;

        static long? Delta(long? cur, long? prev) =>
            cur is long c && prev is long p && c >= p ? c - p : null;

        var ordered = points.OrderBy(p => p.Time).ToList();
        var items = new List<object>(ordered.Count);
        MonitoringInfluxClient.SfpPonPoint? prev = null;
        foreach (var p in ordered)
        {
            items.Add(new
            {
                time = p.Time.ToString("o"),
                state = p.PonLinkStatus,
                statePrev = p.PonLinkStatusPrev,
                onuId = p.OnuId,
                dsFec = p.DsFecEnabled,
                usFec = p.UsFecEnabled,
                respTime = p.OnuResponseTime,
                uptime = p.SfpUptimeS,
                bip = Delta(p.BipErrors, prev?.BipErrors),
                fec = Delta(p.FecErrors, prev?.FecErrors),
                fecCorr = Delta(p.FecCorrectedWords, prev?.FecCorrectedWords),
                hec = Delta(p.HecUncorrected, prev?.HecUncorrected),
                gemTx = Delta(p.GemTxFrames, prev?.GemTxFrames),
                gemTxIdle = Delta(p.GemTxIdleFrames, prev?.GemTxIdleFrames),
                gemRx = Delta(p.GemRxFrames, prev?.GemRxFrames),
                gemDrop = Delta(p.GemRxDropped, prev?.GemRxDropped),
                allocLost = Delta(p.AllocLost, prev?.AllocLost),
                lanFcs = Delta(p.LanRxFcsErrors, prev?.LanRxFcsErrors),
                lanDrop = Delta(p.LanTxDropEvents, prev?.LanTxDropEvents),
                lanOvfl = Delta(p.LanBufferOverflow, prev?.LanBufferOverflow),
            });
            prev = p;
        }
        return items;
    }
}
