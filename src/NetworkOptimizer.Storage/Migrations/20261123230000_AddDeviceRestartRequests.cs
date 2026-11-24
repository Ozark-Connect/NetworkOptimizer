using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceRestartRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DeviceRestartRequests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DeviceType = table.Column<int>(type: "INTEGER", nullable: false),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    DueAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    QuietBasis = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    RequestedMethod = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    StatusReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastCommandAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Method = table.Column<int>(type: "INTEGER", nullable: true),
                    PoeSwitchMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    PoePortIdx = table.Column<int>(type: "INTEGER", nullable: true),
                    BatchStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RequestedByUserId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    RequestedByName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceRestartRequests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRestartRequests_DeviceMac",
                table: "DeviceRestartRequests",
                column: "DeviceMac");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceRestartRequests_Status_DueAtUtc",
                table: "DeviceRestartRequests",
                columns: new[] { "Status", "DueAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeviceRestartRequests");
        }
    }
}
