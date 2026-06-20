using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Point-in-time per-port statistics for the Live View port playback table.
/// Reads <c>interface_counters</c> at the current map scrubber position (or the
/// latest sample when no instant is supplied).
/// </summary>
public static class PortStatsEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/port-stats", async (
            MonitoringInfluxClient influx,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            string? macs,
            DateTime? at,
            CancellationToken ct) =>
        {
            var requested = (macs ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            var points = await influx.QueryPortStatsAsync(
                requested.Count > 0 ? requested : null,
                at?.ToUniversalTime(),
                ct);

            // Map device MAC -> display name from the fabric monitoring targets,
            // matching how the device health chart resolves device names.
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var nameByMac = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.DeviceMac != null)
                .Select(t => new { t.DeviceMac, t.Name })
                .ToDictionaryAsync(t => t.DeviceMac!, t => t.Name, StringComparer.OrdinalIgnoreCase, ct);

            var devices = points
                .GroupBy(p => p.DeviceMac, StringComparer.OrdinalIgnoreCase)
                .Select(g => new
                {
                    mac = g.Key,
                    name = nameByMac.TryGetValue(g.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : g.Key,
                    ports = g
                        .OrderBy(p => p.PortId, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(p => p.IfName, StringComparer.OrdinalIgnoreCase)
                        .Select(p => new
                        {
                            ifName = p.IfName,
                            portId = p.PortId,
                            operStatus = p.OperStatus,
                            speedBps = p.SpeedBps,
                            rateInBps = p.RateInBps,
                            rateOutBps = p.RateOutBps,
                            bytesIn = p.BytesIn,
                            bytesOut = p.BytesOut,
                            ucastPktsIn = p.UcastPktsIn,
                            ucastPktsOut = p.UcastPktsOut,
                            mcastPktsIn = p.McastPktsIn,
                            mcastPktsOut = p.McastPktsOut,
                            bcastPktsIn = p.BcastPktsIn,
                            bcastPktsOut = p.BcastPktsOut,
                            errorsIn = p.ErrorsIn,
                            errorsOut = p.ErrorsOut,
                            discardsIn = p.DiscardsIn,
                            discardsOut = p.DiscardsOut,
                        })
                        .ToList()
                })
                .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new { at = at?.ToUniversalTime().ToString("o"), devices });
        });
    }
}
