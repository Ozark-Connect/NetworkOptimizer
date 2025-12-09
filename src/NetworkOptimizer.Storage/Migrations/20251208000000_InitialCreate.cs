using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditResults",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AuditDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    TotalChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    PassedChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    WarningChecks = table.Column<int>(type: "INTEGER", nullable: false),
                    FirmwareVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    FindingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ComplianceScore = table.Column<double>(type: "REAL", nullable: false),
                    AuditVersion = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditResults", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SqmBaselines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    InterfaceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    InterfaceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    BaselineStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BaselineEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                    BaselineHours = table.Column<int>(type: "INTEGER", nullable: false),
                    AvgBytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    AvgBytesOut = table.Column<long>(type: "INTEGER", nullable: false),
                    PeakBytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    PeakBytesOut = table.Column<long>(type: "INTEGER", nullable: false),
                    MedianBytesIn = table.Column<long>(type: "INTEGER", nullable: false),
                    MedianBytesOut = table.Column<long>(type: "INTEGER", nullable: false),
                    AvgLatency = table.Column<double>(type: "REAL", nullable: false),
                    PeakLatency = table.Column<double>(type: "REAL", nullable: false),
                    P95Latency = table.Column<double>(type: "REAL", nullable: false),
                    P99Latency = table.Column<double>(type: "REAL", nullable: false),
                    AvgPacketLoss = table.Column<double>(type: "REAL", nullable: false),
                    MaxPacketLoss = table.Column<double>(type: "REAL", nullable: false),
                    AvgJitter = table.Column<double>(type: "REAL", nullable: false),
                    MaxJitter = table.Column<double>(type: "REAL", nullable: false),
                    AvgUtilization = table.Column<double>(type: "REAL", nullable: false),
                    PeakUtilization = table.Column<double>(type: "REAL", nullable: false),
                    HourlyDataJson = table.Column<string>(type: "TEXT", nullable: true),
                    RecommendedDownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                    RecommendedUploadMbps = table.Column<double>(type: "REAL", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqmBaselines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentConfigurations",
                columns: table => new
                {
                    AgentId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    AgentName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    DeviceUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    MetricsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SqmEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuditEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuditIntervalHours = table.Column<int>(type: "INTEGER", nullable: false),
                    BatchSize = table.Column<int>(type: "INTEGER", nullable: false),
                    FlushIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    AdditionalSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentConfigurations", x => x.AgentId);
                });

            migrationBuilder.CreateTable(
                name: "Licenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LicenseKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    LicensedTo = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Organization = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LicenseType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MaxDevices = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxAgents = table.Column<int>(type: "INTEGER", nullable: false),
                    IssueDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpirationDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    FeaturesJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licenses", x => x.Id);
                });

            // Create indexes
            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_DeviceId",
                table: "AuditResults",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_AuditDate",
                table: "AuditResults",
                column: "AuditDate");

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_DeviceId_AuditDate",
                table: "AuditResults",
                columns: new[] { "DeviceId", "AuditDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_DeviceId",
                table: "SqmBaselines",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_InterfaceId",
                table: "SqmBaselines",
                column: "InterfaceId");

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_DeviceId_InterfaceId",
                table: "SqmBaselines",
                columns: new[] { "DeviceId", "InterfaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_BaselineStart",
                table: "SqmBaselines",
                column: "BaselineStart");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConfigurations_IsEnabled",
                table: "AgentConfigurations",
                column: "IsEnabled");

            migrationBuilder.CreateIndex(
                name: "IX_AgentConfigurations_LastSeenAt",
                table: "AgentConfigurations",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_LicenseKey",
                table: "Licenses",
                column: "LicenseKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_IsActive",
                table: "Licenses",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ExpirationDate",
                table: "Licenses",
                column: "ExpirationDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "AuditResults");
            migrationBuilder.DropTable(name: "SqmBaselines");
            migrationBuilder.DropTable(name: "AgentConfigurations");
            migrationBuilder.DropTable(name: "Licenses");
        }
    }
}
