using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddSqmLearningMode : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "UploadCongestionSeverity",
            table: "SqmWanConfigurations",
            type: "REAL",
            nullable: false,
            defaultValue: 0.0);

        migrationBuilder.AddColumn<bool>(
            name: "UseLearnedProfile",
            table: "SqmWanConfigurations",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateTable(
            name: "SqmCongestionProfiles",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WanNumber = table.Column<int>(type: "INTEGER", nullable: false),
                Interface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                ConnectionType = table.Column<int>(type: "INTEGER", nullable: false),
                ScheduledTaskId = table.Column<int>(type: "INTEGER", nullable: true),
                LearningStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LearningEndsAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LearningCompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                SampleDurationSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                LastSampleAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                DownloadMultipliersJson = table.Column<string>(type: "TEXT", nullable: true),
                UploadMultipliersJson = table.Column<string>(type: "TEXT", nullable: true),
                SampleCountsJson = table.Column<string>(type: "TEXT", nullable: true),
                PeakDownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                PeakUploadMbps = table.Column<double>(type: "REAL", nullable: false),
                ValidSampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                CoveragePercent = table.Column<double>(type: "REAL", nullable: false),
                DaysSpanned = table.Column<int>(type: "INTEGER", nullable: false),
                IsReliable = table.Column<bool>(type: "INTEGER", nullable: false),
                ProfileUpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SqmCongestionProfiles", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SqmCongestionProfiles_WanNumber",
            table: "SqmCongestionProfiles",
            column: "WanNumber",
            unique: true);

        migrationBuilder.CreateTable(
            name: "SqmLearningSamples",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                WanNumber = table.Column<int>(type: "INTEGER", nullable: false),
                SampledAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                LocalDayOfWeek = table.Column<int>(type: "INTEGER", nullable: false),
                LocalHour = table.Column<int>(type: "INTEGER", nullable: false),
                DownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                UploadMbps = table.Column<double>(type: "REAL", nullable: false),
                LatencyMs = table.Column<double>(type: "REAL", nullable: true),
                DownloadLoadedLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                UploadLoadedLatencyMs = table.Column<double>(type: "REAL", nullable: true),
                IdleDownloadMbps = table.Column<double>(type: "REAL", nullable: false),
                IdleUploadMbps = table.Column<double>(type: "REAL", nullable: false),
                IdleSource = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                Success = table.Column<bool>(type: "INTEGER", nullable: false),
                Error = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Excluded = table.Column<bool>(type: "INTEGER", nullable: false),
                ExclusionReason = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_SqmLearningSamples", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_SqmLearningSamples_WanNumber_SampledAt",
            table: "SqmLearningSamples",
            columns: new[] { "WanNumber", "SampledAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "SqmLearningSamples");
        migrationBuilder.DropTable(name: "SqmCongestionProfiles");
        migrationBuilder.DropColumn(name: "UseLearnedProfile", table: "SqmWanConfigurations");
        migrationBuilder.DropColumn(name: "UploadCongestionSeverity", table: "SqmWanConfigurations");
    }
}
