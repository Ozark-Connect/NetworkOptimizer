using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddSqmLearningProbeLimits : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(name: "LiftDownloadMbps", table: "SqmLearningSamples", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>(name: "LiftUploadMbps", table: "SqmLearningSamples", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<bool>(name: "ProbeLimited", table: "SqmLearningSamples", type: "INTEGER", nullable: false, defaultValue: false);

        migrationBuilder.AddColumn<bool>(name: "PeakIsLowerBound", table: "SqmCongestionProfiles", type: "INTEGER", nullable: false, defaultValue: false);
        migrationBuilder.AddColumn<int>(name: "LiftDownloadMbps", table: "SqmCongestionProfiles", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>(name: "LiftUploadMbps", table: "SqmCongestionProfiles", type: "INTEGER", nullable: true);
        migrationBuilder.AddColumn<int>(name: "LiftCeilingMbps", table: "SqmCongestionProfiles", type: "INTEGER", nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "LiftCeilingMbps", table: "SqmCongestionProfiles");
        migrationBuilder.DropColumn(name: "LiftUploadMbps", table: "SqmCongestionProfiles");
        migrationBuilder.DropColumn(name: "LiftDownloadMbps", table: "SqmCongestionProfiles");
        migrationBuilder.DropColumn(name: "PeakIsLowerBound", table: "SqmCongestionProfiles");
        migrationBuilder.DropColumn(name: "ProbeLimited", table: "SqmLearningSamples");
        migrationBuilder.DropColumn(name: "LiftUploadMbps", table: "SqmLearningSamples");
        migrationBuilder.DropColumn(name: "LiftDownloadMbps", table: "SqmLearningSamples");
    }
}
