using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmwareRollout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FirmwareModelTimings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Model = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MedianDowntimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    P90DowntimeSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    RecentSamplesJson = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmwareModelTimings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FirmwareRolloutPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ScheduledStartAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PlanJson = table.Column<string>(type: "TEXT", nullable: false),
                    OriginalChannelSettingsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmwareRolloutPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FirmwareRolloutSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Mode = table.Column<int>(type: "INTEGER", nullable: false),
                    GlobalChannel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PerDeviceTypeChannelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    PerSkuChannelsJson = table.Column<string>(type: "TEXT", nullable: false),
                    IncludeUniFiOs = table.Column<bool>(type: "INTEGER", nullable: false),
                    IncludeUniFiNetwork = table.Column<bool>(type: "INTEGER", nullable: false),
                    ExclusionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    SpacingProfile = table.Column<int>(type: "INTEGER", nullable: false),
                    AdvancedSpacingJson = table.Column<string>(type: "TEXT", nullable: true),
                    SuppressStandardAlerts = table.Column<bool>(type: "INTEGER", nullable: false),
                    AutopilotWindowMode = table.Column<int>(type: "INTEGER", nullable: false),
                    FixedDayOfWeek = table.Column<int>(type: "INTEGER", nullable: true),
                    FixedHour = table.Column<int>(type: "INTEGER", nullable: true),
                    NotifyHoursAhead = table.Column<int>(type: "INTEGER", nullable: false),
                    SoakHours = table.Column<int>(type: "INTEGER", nullable: false),
                    MinReleaseAgeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    WaiveBackup = table.Column<bool>(type: "INTEGER", nullable: false),
                    PerWaveApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmwareRolloutSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FirmwareRolloutSteps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DeviceType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    FromVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ToVersion = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Channel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Wave = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CommandedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WentDownAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BackAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DowntimeSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    PreStatsJson = table.Column<string>(type: "TEXT", nullable: true),
                    PostStatsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FirmwareRolloutSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FirmwareRolloutSteps_FirmwareRolloutPlans_PlanId",
                        column: x => x.PlanId,
                        principalTable: "FirmwareRolloutPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareModelTimings_Model",
                table: "FirmwareModelTimings",
                column: "Model",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareRolloutPlans_CreatedAt",
                table: "FirmwareRolloutPlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareRolloutPlans_ScheduledStartAt",
                table: "FirmwareRolloutPlans",
                column: "ScheduledStartAt");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareRolloutPlans_Status",
                table: "FirmwareRolloutPlans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareRolloutSteps_DeviceMac",
                table: "FirmwareRolloutSteps",
                column: "DeviceMac");

            migrationBuilder.CreateIndex(
                name: "IX_FirmwareRolloutSteps_PlanId_Wave",
                table: "FirmwareRolloutSteps",
                columns: new[] { "PlanId", "Wave" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FirmwareModelTimings");

            migrationBuilder.DropTable(
                name: "FirmwareRolloutSettings");

            migrationBuilder.DropTable(
                name: "FirmwareRolloutSteps");

            migrationBuilder.DropTable(
                name: "FirmwareRolloutPlans");
        }
    }
}
