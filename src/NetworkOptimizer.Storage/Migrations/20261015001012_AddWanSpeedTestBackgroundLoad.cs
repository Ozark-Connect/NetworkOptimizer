using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanSpeedTestBackgroundLoad : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BackgroundDownloadMbps",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "BackgroundLoadInvalid",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "BackgroundUploadMbps",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExpectedDownloadMbps",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "ExpectedUploadMbps",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackgroundDownloadMbps",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "BackgroundLoadInvalid",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "BackgroundUploadMbps",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "ExpectedDownloadMbps",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "ExpectedUploadMbps",
                table: "Iperf3Results");
        }
    }
}
