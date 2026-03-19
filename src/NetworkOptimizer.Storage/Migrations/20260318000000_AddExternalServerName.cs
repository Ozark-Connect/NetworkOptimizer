using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Add ExternalServerName column to Iperf3Results for WAN speed tests
    /// via external OpenSpeedTest servers (e.g., VPS).
    /// </summary>
    public partial class AddExternalServerName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalServerName",
                table: "Iperf3Results",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExternalServerName",
                table: "Iperf3Results");
        }
    }
}
