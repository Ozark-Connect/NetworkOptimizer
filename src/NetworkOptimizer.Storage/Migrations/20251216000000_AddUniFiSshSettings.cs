using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddUniFiSshSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create UniFiSshSettings table
            migrationBuilder.CreateTable(
                name: "UniFiSshSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 22),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LastTestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestResult = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UniFiSshSettings", x => x.Id);
                });

            // Create Iperf3Results table
            migrationBuilder.CreateTable(
                name: "Iperf3Results",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    TestTime = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    ParallelStreams = table.Column<int>(type: "INTEGER", nullable: false),
                    UploadBitsPerSecond = table.Column<double>(type: "REAL", nullable: false),
                    UploadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UploadRetransmits = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadBitsPerSecond = table.Column<double>(type: "REAL", nullable: false),
                    DownloadBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    DownloadRetransmits = table.Column<int>(type: "INTEGER", nullable: false),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    RawUploadJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawDownloadJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Iperf3Results", x => x.Id);
                });

            // Create indexes for Iperf3Results
            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_DeviceHost",
                table: "Iperf3Results",
                column: "DeviceHost");

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_TestTime",
                table: "Iperf3Results",
                column: "TestTime");

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_DeviceHost_TestTime",
                table: "Iperf3Results",
                columns: new[] { "DeviceHost", "TestTime" });

            // Update DeviceSshConfigurations table - remove credential columns if they exist
            // SQLite doesn't support dropping columns easily, so we'll just leave them (they won't be used)
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UniFiSshSettings");
            migrationBuilder.DropTable(name: "Iperf3Results");
        }
    }
}
