using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanSpeedTestLoss : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "DownloadLossPercent",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DownloadLossProbes",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "IdleLossPercent",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "IdleLossProbes",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "UploadLossPercent",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UploadLossProbes",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DownloadLossPercent",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "DownloadLossProbes",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "IdleLossPercent",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "IdleLossProbes",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "UploadLossPercent",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "UploadLossProbes",
                table: "Iperf3Results");
        }
    }
}
