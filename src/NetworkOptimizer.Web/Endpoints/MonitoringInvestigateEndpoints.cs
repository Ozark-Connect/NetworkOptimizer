using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

public static class MonitoringInvestigateEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/investigate/packet-loss", async (
            MonitoringInfluxClient influx,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            DateTime? before,
            DateTime? after,
            CancellationToken ct) =>
        {
            var result = await influx.FindRecentLossEventAsync(
                before?.ToUniversalTime(), after?.ToUniversalTime(), ct);
            if (result == null)
                return Results.Ok(new { found = false });

            string? targetName = null;
            if (!string.IsNullOrEmpty(result.TargetId))
            {
                await using var db = await dbFactory.CreateDbContextAsync(ct);
                targetName = await db.MonitoringTargets.AsNoTracking()
                    .Where(t => t.TargetId == result.TargetId)
                    .Select(t => t.Name)
                    .FirstOrDefaultAsync(ct);
            }

            var category = result.TargetType switch
            {
                "accessisp" => "AccessIsp",
                "transit" => "Transit",
                _ => "InternetService"
            };

            return Results.Ok(new
            {
                found = true,
                timestamp = result.Timestamp.ToString("o"),
                category,
                targetName,
                lossPercent = result.LossPercent
            });
        });

        app.MapGet("/api/monitoring/investigate/sfp-anomaly", async (
            MonitoringInfluxClient influx,
            CancellationToken ct) =>
        {
            var result = await influx.FindRecentSfpAnomalyAsync(ct);
            if (result == null)
                return Results.Ok(new { found = false });

            return Results.Ok(new
            {
                found = true,
                timestamp = result.Timestamp.ToString("o"),
                metric = result.Metric,
                value = result.Value,
                deviceMac = result.DeviceMac,
                portName = result.PortName
            });
        });
    }
}
