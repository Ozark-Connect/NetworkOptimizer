using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddClientSpeedTestResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClientSpeedTestResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TestTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ClientIp = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    ClientMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: true),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    DownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                    UploadMbps = table.Column<double>(type: "REAL", nullable: false),
                    PingMs = table.Column<double>(type: "REAL", nullable: true),
                    JitterMs = table.Column<double>(type: "REAL", nullable: true),
                    DownloadDataMb = table.Column<double>(type: "REAL", nullable: true),
                    UploadDataMb = table.Column<double>(type: "REAL", nullable: true),
                    DownloadRetransmits = table.Column<int>(type: "INTEGER", nullable: true),
                    UploadRetransmits = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    ParallelStreams = table.Column<int>(type: "INTEGER", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClientSpeedTestResults", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSpeedTestResults_ClientIp",
                table: "ClientSpeedTestResults",
                column: "ClientIp");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSpeedTestResults_ClientIp_TestTime",
                table: "ClientSpeedTestResults",
                columns: new[] { "ClientIp", "TestTime" });

            migrationBuilder.CreateIndex(
                name: "IX_ClientSpeedTestResults_ClientMac",
                table: "ClientSpeedTestResults",
                column: "ClientMac");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSpeedTestResults_Source",
                table: "ClientSpeedTestResults",
                column: "Source");

            migrationBuilder.CreateIndex(
                name: "IX_ClientSpeedTestResults_TestTime",
                table: "ClientSpeedTestResults",
                column: "TestTime");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClientSpeedTestResults");
        }
    }
}
