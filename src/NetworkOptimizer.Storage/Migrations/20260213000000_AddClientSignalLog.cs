using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSignalLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientSignalLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SignalDbm = table.Column<int>(type: "INTEGER", nullable: true),
                    NoiseDbm = table.Column<int>(type: "INTEGER", nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: true),
                    Band = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    TxRateKbps = table.Column<long>(type: "INTEGER", nullable: true),
                    RxRateKbps = table.Column<long>(type: "INTEGER", nullable: true),
                    IsMlo = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    MloLinksJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: true),
                    ApName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ApModel = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ApChannel = table.Column<int>(type: "INTEGER", nullable: true),
                    ApTxPower = table.Column<int>(type: "INTEGER", nullable: true),
                    ApClientCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ApRadioBand = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    LocationAccuracyMeters = table.Column<int>(type: "INTEGER", nullable: true),
                    TraceHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TraceJson = table.Column<string>(type: "TEXT", nullable: true),
                    HopCount = table.Column<int>(type: "INTEGER", nullable: true),
                    BottleneckLinkSpeedMbps = table.Column<double>(type: "REAL", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSignalLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSignalLogs_ClientMac_Timestamp",
                table: "ClientSignalLogs",
                columns: new[] { "ClientMac", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSignalLogs_TraceHash",
                table: "ClientSignalLogs",
                column: "TraceHash");

            // Fix historical OpenSpeedTest results: they defaulted to DurationSeconds=10
            // but the actual JS test duration is 12 seconds per direction
            migrationBuilder.Sql(
                "UPDATE Iperf3Results SET DurationSeconds = 12 WHERE Direction = 2 AND DurationSeconds = 10");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientSignalLogs");

            // Revert browser test durations back to 10
            migrationBuilder.Sql(
                "UPDATE Iperf3Results SET DurationSeconds = 10 WHERE Direction = 2 AND DurationSeconds = 12");
        }
    }
}
