using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class DropAgentConfigurations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentConfigurations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentConfigurations",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AdditionalSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    AgentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AuditEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuditIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    DeviceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    FlushIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    MetricsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    SqmEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConfigurations", x => x.AgentId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentConfigurations_IsEnabled",
                table: "AgentConfigurations",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConfigurations_LastSeenAt",
                table: "AgentConfigurations",
                column: "LastSeenAt");
        }
    }
}
