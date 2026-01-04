using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSpeedTestFieldsToIperf3Result : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Direction",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<double>(
                name: "PingMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "JitterMs",
                table: "Iperf3Results",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserAgent",
                table: "Iperf3Results",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientMac",
                table: "Iperf3Results",
                type: "TEXT",
                maxLength: 17,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_Direction",
                table: "Iperf3Results",
                column: "Direction");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Iperf3Results_Direction",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "Direction",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "PingMs",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "JitterMs",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "UserAgent",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "ClientMac",
                table: "Iperf3Results");
        }
    }
}
