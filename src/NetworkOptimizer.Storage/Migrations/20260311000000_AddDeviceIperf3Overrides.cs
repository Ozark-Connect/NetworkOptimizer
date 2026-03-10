using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceIperf3Overrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Iperf3ParallelStreams",
                table: "DeviceSshConfigurations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Iperf3DurationSeconds",
                table: "DeviceSshConfigurations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Iperf3ParallelStreams",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropColumn(
                name: "Iperf3DurationSeconds",
                table: "DeviceSshConfigurations");
        }
    }
}
