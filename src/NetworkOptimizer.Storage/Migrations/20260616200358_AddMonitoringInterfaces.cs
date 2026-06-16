using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringInterfaces : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringInterfaces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    WanIfName = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    WanKey = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    TargetIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SubnetPrefix = table.Column<int>(type: "INTEGER", nullable: false),
                    GatewayLocalIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnatEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    WatchdogIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsManuallyDeployed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringInterfaces", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_TargetIp",
                table: "MonitoringInterfaces",
                column: "TargetIp");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringInterfaces_WanIfName_Name",
                table: "MonitoringInterfaces",
                columns: new[] { "WanIfName", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MonitoringInterfaces");
        }
    }
}
