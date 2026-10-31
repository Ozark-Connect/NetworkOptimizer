using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPathEpisodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PathEpisodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanContextId = table.Column<int>(type: "INTEGER", nullable: true),
                    WanInterface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    OpenedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RecoveryTracedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GateSnapshot = table.Column<string>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Disposition = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    BottleneckHopIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BottleneckHopNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    BottleneckLabel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true),
                    ReturnPathFinding = table.Column<int>(type: "INTEGER", nullable: false),
                    Summary = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathEpisodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PathEpisodeTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitoringTargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    BaselineRttMs = table.Column<double>(type: "REAL", nullable: true),
                    PeakRttMs = table.Column<double>(type: "REAL", nullable: true),
                    BaselineLossPct = table.Column<double>(type: "REAL", nullable: true),
                    PeakLossPct = table.Column<double>(type: "REAL", nullable: true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Disposition = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    BottleneckHopIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BottleneckHopNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    BottleneckLabel = table.Column<string>(type: "TEXT", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathEpisodeTargets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PathEpisodeTargets_PathEpisodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "PathEpisodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PathEpisodeTraces",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EpisodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    MonitoringTargetId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Vantage = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SourceInterface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    Reached = table.Column<bool>(type: "INTEGER", nullable: false),
                    ResolvedAddress = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HopsJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PathEpisodeTraces", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PathEpisodeTraces_PathEpisodes_EpisodeId",
                        column: x => x.EpisodeId,
                        principalTable: "PathEpisodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PathEpisodes_OpenedAt",
                table: "PathEpisodes",
                column: "OpenedAt");

            migrationBuilder.CreateIndex(
                name: "IX_PathEpisodeTargets_EpisodeId_MonitoringTargetId_Role",
                table: "PathEpisodeTargets",
                columns: new[] { "EpisodeId", "MonitoringTargetId", "Role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PathEpisodeTraces_EpisodeId",
                table: "PathEpisodeTraces",
                column: "EpisodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PathEpisodeTargets");

            migrationBuilder.DropTable(
                name: "PathEpisodeTraces");

            migrationBuilder.DropTable(
                name: "PathEpisodes");
        }
    }
}
