using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringInterfaceAlias : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AliasIp",
                table: "MonitoringInterfaces",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            // Tightening WanIfName+Name -> Name alone (nothing should share a boot-script
            // name across WANs, since the script filename and macvlan name are both keyed
            // on Name only). Rows sharing a Name were legal under the old composite index
            // but already broken on the gateway (both generate the same boot-script file and
            // macvlan name), so auto-suffix every colliding row except the oldest with its
            // own unique Id rather than failing the migration and blocking startup. The
            // suffix keeps the name inside the validated shape (<= 15 chars, lowercase
            // letter start, [a-z0-9-]): first chars of the old name + "-" + Id. A renamed
            // row shows as not-deployed afterwards; redeploying it recreates its artifacts
            // under the new name.
            migrationBuilder.Sql(
                "UPDATE MonitoringInterfaces " +
                "SET Name = substr(Name, 1, 14 - length(CAST(Id AS TEXT))) || '-' || CAST(Id AS TEXT) " +
                "WHERE Id NOT IN (SELECT MIN(Id) FROM MonitoringInterfaces GROUP BY Name) " +
                "AND Name IN (SELECT Name FROM MonitoringInterfaces GROUP BY Name HAVING COUNT(*) > 1);");

            // If a suffixed name still collides with a pre-existing row (pathological: the
            // user already had a row named exactly "<truncated>-<thatId>"), the unique index
            // below fails with a raw SQLite constraint error. That error is caught at the
            // migration call site (MigrationSafety.MigrateWithFriendlyErrors) and rethrown
            // with an actionable message, so no further SQL-level guard is needed (and
            // RAISE() is illegal outside a trigger anyway). GatewayLocalIp collisions take
            // the same friendly-error path - an address can't be auto-renumbered safely.
            migrationBuilder.DropIndex(
                name: "IX_MonitoringInterfaces_WanIfName_Name",
                table: "MonitoringInterfaces");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_Name",
                table: "MonitoringInterfaces",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_AliasIp",
                table: "MonitoringInterfaces",
                column: "AliasIp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_GatewayLocalIp",
                table: "MonitoringInterfaces",
                column: "GatewayLocalIp",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MonitoringInterfaces_GatewayLocalIp",
                table: "MonitoringInterfaces");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringInterfaces_AliasIp",
                table: "MonitoringInterfaces");

            migrationBuilder.DropIndex(
                name: "IX_MonitoringInterfaces_Name",
                table: "MonitoringInterfaces");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_WanIfName_Name",
                table: "MonitoringInterfaces",
                columns: new[] { "WanIfName", "Name" },
                unique: true);

            migrationBuilder.DropColumn(
                name: "AliasIp",
                table: "MonitoringInterfaces");
        }
    }
}
