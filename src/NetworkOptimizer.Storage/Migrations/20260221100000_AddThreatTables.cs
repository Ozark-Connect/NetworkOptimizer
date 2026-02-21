using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddThreatTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThreatPatterns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PatternType = table.Column<int>(type: "INTEGER", nullable: false),
                    DetectedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceIpsJson = table.Column<string>(type: "TEXT", nullable: false),
                    TargetPort = table.Column<int>(type: "INTEGER", nullable: true),
                    EventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatPatterns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ThreatEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceIp = table.Column<string>(type: "TEXT", nullable: false),
                    SourcePort = table.Column<int>(type: "INTEGER", nullable: false),
                    DestIp = table.Column<string>(type: "TEXT", nullable: false),
                    DestPort = table.Column<int>(type: "INTEGER", nullable: false),
                    Protocol = table.Column<string>(type: "TEXT", nullable: false),
                    SignatureId = table.Column<long>(type: "INTEGER", nullable: false),
                    SignatureName = table.Column<string>(type: "TEXT", nullable: false),
                    Category = table.Column<string>(type: "TEXT", nullable: false),
                    Severity = table.Column<int>(type: "INTEGER", nullable: false),
                    Action = table.Column<int>(type: "INTEGER", nullable: false),
                    InnerAlertId = table.Column<string>(type: "TEXT", nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", nullable: true),
                    City = table.Column<string>(type: "TEXT", nullable: true),
                    Asn = table.Column<int>(type: "INTEGER", nullable: true),
                    AsnOrg = table.Column<string>(type: "TEXT", nullable: true),
                    Latitude = table.Column<double>(type: "REAL", nullable: true),
                    Longitude = table.Column<double>(type: "REAL", nullable: true),
                    KillChainStage = table.Column<int>(type: "INTEGER", nullable: false),
                    PatternId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ThreatEvents_ThreatPatterns_PatternId",
                        column: x => x.PatternId,
                        principalTable: "ThreatPatterns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "CrowdSecReputations",
                columns: table => new
                {
                    Ip = table.Column<string>(type: "TEXT", nullable: false),
                    ReputationJson = table.Column<string>(type: "TEXT", nullable: false),
                    FetchedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CrowdSecReputations", x => x.Ip);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_Timestamp",
                table: "ThreatEvents",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_SourceIp_Timestamp",
                table: "ThreatEvents",
                columns: new[] { "SourceIp", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_DestPort_Timestamp",
                table: "ThreatEvents",
                columns: new[] { "DestPort", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_KillChainStage",
                table: "ThreatEvents",
                column: "KillChainStage");

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_InnerAlertId",
                table: "ThreatEvents",
                column: "InnerAlertId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_PatternId",
                table: "ThreatEvents",
                column: "PatternId");

            migrationBuilder.CreateIndex(
                name: "IX_ThreatPatterns_PatternType_DetectedAt",
                table: "ThreatPatterns",
                columns: new[] { "PatternType", "DetectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CrowdSecReputations_ExpiresAt",
                table: "CrowdSecReputations",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ThreatEvents");
            migrationBuilder.DropTable(name: "ThreatPatterns");
            migrationBuilder.DropTable(name: "CrowdSecReputations");
        }
    }
}
