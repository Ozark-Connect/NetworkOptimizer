using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringTabVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowCellularTab",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowCmTab",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowOntTab",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowStarlinkTab",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowCellularTab",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "ShowCmTab",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "ShowOntTab",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "ShowStarlinkTab",
                table: "MonitoringSettings");
        }
    }
}
