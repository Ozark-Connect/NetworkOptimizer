using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTempThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "GatewayTempHighC",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SwitchTempHighC",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GatewayTempHighC",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "SwitchTempHighC",
                table: "MonitoringSettings");
        }
    }
}
