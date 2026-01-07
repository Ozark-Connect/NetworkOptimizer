using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWifiTxRxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WifiTxRateKbps",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WifiRxRateKbps",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WifiTxRateKbps",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiRxRateKbps",
                table: "Iperf3Results");
        }
    }
}
