using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EventTypePattern = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true),
                    MinSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    CooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalationMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    EscalationSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    DigestOnly = table.Column<bool>(type: "INTEGER", nullable: false),
                    TargetDevices = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryChannels",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ChannelType = table.Column<int>(type: "INTEGER", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    MinSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                    DigestEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DigestSchedule = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryChannels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    AlertCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CorrelationKey = table.Column<string>(type: "TEXT", nullable: false),
                    FirstTriggeredAt = table.Column<string>(type: "TEXT", nullable: false),
                    LastTriggeredAt = table.Column<string>(type: "TEXT", nullable: false),
                    ResolvedAt = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertIncidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false),
                    DeviceId = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceName = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceIp = table.Column<string>(type: "TEXT", nullable: true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: true),
                    IncidentId = table.Column<int>(type: "INTEGER", nullable: true),
                    ContextJson = table.Column<string>(type: "TEXT", nullable: true),
                    TriggeredAt = table.Column<string>(type: "TEXT", nullable: false),
                    AcknowledgedAt = table.Column<string>(type: "TEXT", nullable: true),
                    ResolvedAt = table.Column<string>(type: "TEXT", nullable: true),
                    DeliveredToChannels = table.Column<string>(type: "TEXT", nullable: true),
                    DeliverySucceeded = table.Column<bool>(type: "INTEGER", nullable: false),
                    DeliveryError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_TriggeredAt",
                table: "AlertHistory",
                column: "TriggeredAt");

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_Source_TriggeredAt",
                table: "AlertHistory",
                columns: new[] { "Source", "TriggeredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_Status",
                table: "AlertHistory",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_RuleId",
                table: "AlertHistory",
                column: "RuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertHistory_IncidentId",
                table: "AlertHistory",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_CorrelationKey",
                table: "AlertIncidents",
                column: "CorrelationKey");

            migrationBuilder.CreateIndex(
                name: "IX_AlertIncidents_Status",
                table: "AlertIncidents",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AlertHistory");
            migrationBuilder.DropTable(name: "AlertIncidents");
            migrationBuilder.DropTable(name: "DeliveryChannels");
            migrationBuilder.DropTable(name: "AlertRules");
        }
    }
}
