using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddModemAndSpeedTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ModemConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ModemType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    QmiDevice = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    LastPolled = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModemConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSshConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSshConfigurations", x => x.Id);
                });

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

            // Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_ModemConfigurations_Host",
                table: "ModemConfigurations",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_ModemConfigurations_Enabled",
                table: "ModemConfigurations",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSshConfigurations_Host",
                table: "DeviceSshConfigurations",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSshConfigurations_Enabled",
                table: "DeviceSshConfigurations",
                column: "Enabled");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ModemConfigurations");
            migrationBuilder.DropTable(name: "DeviceSshConfigurations");
            migrationBuilder.DropTable(name: "Iperf3Results");
        }
    }
}
