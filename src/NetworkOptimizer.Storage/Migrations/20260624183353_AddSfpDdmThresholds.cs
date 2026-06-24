using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSfpDdmThresholds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AeRxPowerLowDbm",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AeTempHighC",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "AeTxPowerHighDbm",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PonRxPowerLowDbm",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PonTempHighC",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PonTxPowerHighDbm",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "SfpTempHighGenericC",
                table: "MonitoringSettings",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AeRxPowerLowDbm",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "AeTempHighC",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "AeTxPowerHighDbm",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "PonRxPowerLowDbm",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "PonTempHighC",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "PonTxPowerHighDbm",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "SfpTempHighGenericC",
                table: "MonitoringSettings");
        }
    }
}
