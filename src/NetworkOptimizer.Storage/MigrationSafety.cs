using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Storage;

/// <summary>
/// Wraps <see cref="Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.Migrate"/> so
/// that migration failures caused by pre-existing data (rather than a schema bug) surface as a clear,
/// actionable message instead of a raw SQLite constraint error.
/// </summary>
public static class MigrationSafety
{
    /// <summary>
    /// Applies pending migrations. The AddMonitoringInterfaceAlias migration adds unique indexes on
    /// <c>MonitoringInterfaces.Name</c>, <c>GatewayLocalIp</c>, and <c>AliasIp</c>. If an existing
    /// database already has two rows colliding on Name (across different WANs) or GatewayLocalIp
    /// (never enforced before this migration), the raw SQLite exception is rethrown as an
    /// <see cref="InvalidOperationException"/> that tells the operator exactly how to fix it.
    /// AliasIp is a new column in this same migration, so no pre-existing row can already collide
    /// on it - that branch exists for defense in depth only (e.g. a hand-edited database).
    /// </summary>
    public static void MigrateWithFriendlyErrors(DbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);

        try
        {
            db.Database.Migrate();
        }
        catch (Exception ex) when (IsUniqueConstraintCollision(ex, "IX_MonitoringInterfaces_Name", "MonitoringInterfaces.Name"))
        {
            throw new InvalidOperationException(
                "Migration failed: two Monitoring Interfaces share the same Name on different WANs, which is no longer allowed " +
                "(interface names must be globally unique - the boot script filename and macvlan interface name are both keyed on Name alone). " +
                "Before upgrading, rename one of the colliding interfaces. To find them, run this against the database: " +
                "SELECT Id, Name, WanIfName FROM MonitoringInterfaces ORDER BY Name;", ex);
        }
        catch (Exception ex) when (IsUniqueConstraintCollision(ex, "IX_MonitoringInterfaces_GatewayLocalIp", "MonitoringInterfaces.GatewayLocalIp"))
        {
            throw new InvalidOperationException(
                "Migration failed: two Monitoring Interfaces share the same GatewayLocalIp, which is no longer allowed " +
                "(this is the macvlan's own address on the gateway and must be unique per interface). " +
                "Before upgrading, change one of the colliding interfaces' gateway-local IP. To find them, run this against the database: " +
                "SELECT Id, Name, GatewayLocalIp FROM MonitoringInterfaces ORDER BY GatewayLocalIp;", ex);
        }
        catch (Exception ex) when (IsUniqueConstraintCollision(ex, "IX_MonitoringInterfaces_AliasIp", "MonitoringInterfaces.AliasIp"))
        {
            throw new InvalidOperationException(
                "Migration failed: two Monitoring Interfaces share the same AliasIp, which is not allowed " +
                "(the alias IP must be unique across interfaces). Before upgrading, change one of the colliding " +
                "interfaces' alias IP. To find them, run this against the database: " +
                "SELECT Id, Name, AliasIp FROM MonitoringInterfaces ORDER BY AliasIp;", ex);
        }
    }

    /// <summary>
    /// True if any exception in the chain is a SQLite unique-constraint failure on the given index
    /// or column. Walks inner exceptions because EF Core may wrap the provider exception depending
    /// on where in the migration pipeline it surfaces.
    /// </summary>
    private static bool IsUniqueConstraintCollision(Exception ex, string indexName, string columnQualifiedName)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SqliteException &&
                (e.Message.Contains(indexName, StringComparison.OrdinalIgnoreCase) ||
                 e.Message.Contains(columnQualifiedName, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
