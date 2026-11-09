using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddChangeControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ChangeControlSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KeepEveryChangeDays = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepDailyDays = table.Column<int>(type: "INTEGER", nullable: false),
                    KeepMonthlyMonths = table.Column<int>(type: "INTEGER", nullable: true),
                    ConsoleBackupsToKeep = table.Column<int>(type: "INTEGER", nullable: false),
                    CommitWindowMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentCommitWindowMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    WatchInternet = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScheduleQuietestHour = table.Column<bool>(type: "INTEGER", nullable: false),
                    ApprovalsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmergencyRollbackWithoutApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                    DailySnapshotHour = table.Column<int>(type: "INTEGER", nullable: false),
                    DailySnapshotMinute = table.Column<int>(type: "INTEGER", nullable: false),
                    ShowOwnChanges = table.Column<bool>(type: "INTEGER", nullable: false),
                    BaselineTemplateId = table.Column<int>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeControlSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChangeDetectionGaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CaughtUp = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeDetectionGaps", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChangeDetectionStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    LastAuditEventTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    LastAuditEventIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CfgVersionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DeviceVersionsJson = table.Column<string>(type: "TEXT", nullable: true),
                    NetworkAppVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    NetworkAppVersionChangedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPollAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPollOk = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastSnapshotAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastDailySnapshotAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastConsoleBackupAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRetentionAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChangeDetectionStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OccurredAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    SnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    PreviousSnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    ActorKind = table.Column<int>(type: "INTEGER", nullable: false),
                    ActorName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ActorIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ActorPlatform = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ObjectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ServiceTagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ManagementPath = table.Column<bool>(type: "INTEGER", nullable: false),
                    WanPath = table.Column<bool>(type: "INTEGER", nullable: false),
                    RegressionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FixCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImpactJson = table.Column<string>(type: "TEXT", nullable: true),
                    PlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    FirmwareRolloutPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditEventsJson = table.Column<string>(type: "TEXT", nullable: true),
                    VersionFrom = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    VersionTo = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Evaluated = table.Column<bool>(type: "INTEGER", nullable: false),
                    ManagementChecked = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigChanges", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceDescription = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SourceSnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceSiteSlug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    OperationsEncrypted = table.Column<string>(type: "TEXT", nullable: false),
                    HighestTier = table.Column<int>(type: "INTEGER", nullable: false),
                    ObjectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AcknowledgementsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ApprovedLiveHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ScheduledFor = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApplyOnApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    AppliedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    WatchUntil = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PreApplySnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    PostApplySnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReportJson = table.Column<string>(type: "TEXT", nullable: true),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    RevertOfPlanId = table.Column<int>(type: "INTEGER", nullable: true),
                    AgentRevertArmed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TakenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    TriggerDetail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    NetworkAppVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    PreviousSnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    ChangedObjectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ObjectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Pinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    PinReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    AuditIssueCount = table.Column<int>(type: "INTEGER", nullable: true),
                    AuditRegressionCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AuditIssuesJson = table.Column<string>(type: "TEXT", nullable: true),
                    ReadErrorsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    SourceSiteSlug = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ContentEncrypted = table.Column<string>(type: "TEXT", nullable: false),
                    CollectionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigWriteJournal",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    At = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Collection = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigWriteJournal", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConsoleBackups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TakenAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FirmwareRollout = table.Column<bool>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    Sha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NetworkAppVersion = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConsoleBackups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ConfigChangeObjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ChangeId = table.Column<int>(type: "INTEGER", nullable: false),
                    Collection = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ObjectName = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    FieldsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ServiceTagsJson = table.Column<string>(type: "TEXT", nullable: true),
                    OwnedBy = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigChangeObjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConfigChangeObjects_ConfigChanges_ChangeId",
                        column: x => x.ChangeId,
                        principalTable: "ConfigChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConfigSnapshotBlobs",
                columns: table => new
                {
                    SnapshotId = table.Column<int>(type: "INTEGER", nullable: false),
                    Collection = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ObjectCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SizeBytes = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConfigSnapshotBlobs", x => new { x.SnapshotId, x.Collection });
                    table.ForeignKey(
                        name: "FK_ConfigSnapshotBlobs_ConfigSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "ConfigSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChangeDetectionGaps_StartedAt",
                table: "ChangeDetectionGaps",
                column: "StartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigChangeObjects_ChangeId",
                table: "ConfigChangeObjects",
                column: "ChangeId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigChangeObjects_Collection_ObjectId",
                table: "ConfigChangeObjects",
                columns: new[] { "Collection", "ObjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ConfigChanges_OccurredAt",
                table: "ConfigChanges",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigChanges_SnapshotId",
                table: "ConfigChanges",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigPlans_CreatedAt",
                table: "ConfigPlans",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigPlans_Status",
                table: "ConfigPlans",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSnapshotBlobs_Hash",
                table: "ConfigSnapshotBlobs",
                column: "Hash");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigSnapshots_TakenAt",
                table: "ConfigSnapshots",
                column: "TakenAt");

            migrationBuilder.CreateIndex(
                name: "IX_ConfigWriteJournal_At",
                table: "ConfigWriteJournal",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_ConsoleBackups_FileName",
                table: "ConsoleBackups",
                column: "FileName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChangeControlSettings");

            migrationBuilder.DropTable(
                name: "ChangeDetectionGaps");

            migrationBuilder.DropTable(
                name: "ChangeDetectionStates");

            migrationBuilder.DropTable(
                name: "ConfigChangeObjects");

            migrationBuilder.DropTable(
                name: "ConfigPlans");

            migrationBuilder.DropTable(
                name: "ConfigSnapshotBlobs");

            migrationBuilder.DropTable(
                name: "ConfigTemplates");

            migrationBuilder.DropTable(
                name: "ConfigWriteJournal");

            migrationBuilder.DropTable(
                name: "ConsoleBackups");

            migrationBuilder.DropTable(
                name: "ConfigChanges");

            migrationBuilder.DropTable(
                name: "ConfigSnapshots");
        }
    }
}
