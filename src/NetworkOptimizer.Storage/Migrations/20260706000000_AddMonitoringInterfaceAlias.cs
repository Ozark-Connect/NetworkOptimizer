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
            // on Name only). If an existing database already has two rows with the same Name
            // on different WANs, creating the unique index below fails with a raw SQLite
            // constraint error. That error is caught at the migration call site
            // (MigrationSafety.MigrateWithFriendlyErrors) and rethrown with an actionable
            // message, so no SQL-level guard is needed (and RAISE() is illegal outside a
            // trigger anyway).
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
