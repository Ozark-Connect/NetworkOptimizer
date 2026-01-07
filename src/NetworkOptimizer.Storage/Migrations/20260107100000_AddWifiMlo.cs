using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWifiMlo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "WifiIsMlo",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WifiMloLinksJson",
                table: "Iperf3Results",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WifiIsMlo",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "WifiMloLinksJson",
                table: "Iperf3Results");
        }
    }
}
