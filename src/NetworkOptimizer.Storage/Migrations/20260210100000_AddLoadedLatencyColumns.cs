using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLoadedLatencyColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DownloadLatencyMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "DownloadJitterMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UploadLatencyMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UploadJitterMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DownloadLatencyMs",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "DownloadJitterMs",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "UploadLatencyMs",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "UploadJitterMs",
                table: "Iperf3Results");
        }
    }
}
