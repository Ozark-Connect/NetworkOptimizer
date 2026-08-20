using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NetworkOptimizer.Monitoring;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Removes custom OID configurations that are now covered by standard polling.
/// Only removes entries where both the OID and the field name match, so custom
/// charts with a different field name for the same OID are preserved.
/// </summary>
public static class CustomOidMigration
{
    private static readonly (string Oid, string FieldName)[] SupersededOids =
    [
        (UniFiOids.LmFanSensorsCpuRpm, InfluxFieldNames.FanSpeedRpm),
    ];

    public static async Task<int> RemoveSupersededAsync(
        NetworkOptimizerDbContext db, string site, ILogger? logger, CancellationToken ct = default)
    {
        var removed = 0;
        foreach (var (oid, fieldName) in SupersededOids)
        {
            var superseded = await db.CustomOidConfigurations
                .Where(c => c.Oid == oid && c.FieldName == fieldName)
                .ToListAsync(ct);
            if (superseded.Count > 0)
            {
                db.CustomOidConfigurations.RemoveRange(superseded);
                removed += superseded.Count;
            }
        }

        if (removed > 0)
        {
            await db.SaveChangesAsync(ct);
            logger?.LogInformation("Removed {Count} custom OID(s) superseded by standard polling on site {Site}", removed, site);
        }

        return removed;
    }
}
