using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationAndWifiSignal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Geolocation fields (browser tests with permission)
            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LocationAccuracyMeters",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            // Wi-Fi signal fields (from UniFi at test time)
            migrationBuilder.AddColumn<int>(
                name: "WifiSignalDbm",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WifiNoiseDbm",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WifiChannel",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WifiRadioProto",
                table: "Iperf3Results",
                type: "TEXT",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "LocationAccuracyMeters",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiSignalDbm",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiNoiseDbm",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiChannel",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiRadioProto",
                table: "Iperf3Results");
        }
    }
}
