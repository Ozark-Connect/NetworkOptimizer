using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLanHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LanHealthExclusions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Label = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanHealthExclusions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LanHealthLinkHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HourStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LinkId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ClientsBehind = table.Column<int>(type: "INTEGER", nullable: false),
                    Drops = table.Column<double>(type: "REAL", nullable: false),
                    DropsExplained = table.Column<double>(type: "REAL", nullable: false),
                    Relinks = table.Column<double>(type: "REAL", nullable: false),
                    LossBursts = table.Column<double>(type: "REAL", nullable: false),
                    ErrorBursts = table.Column<double>(type: "REAL", nullable: false),
                    SpeedChanges = table.Column<double>(type: "REAL", nullable: false),
                    DownSeconds = table.Column<double>(type: "REAL", nullable: false),
                    UpRxErrors = table.Column<double>(type: "REAL", nullable: true),
                    DownRxErrors = table.Column<double>(type: "REAL", nullable: true),
                    UpTxErrors = table.Column<double>(type: "REAL", nullable: true),
                    DownTxErrors = table.Column<double>(type: "REAL", nullable: true),
                    UpTxDiscards = table.Column<double>(type: "REAL", nullable: true),
                    DownTxDiscards = table.Column<double>(type: "REAL", nullable: true),
                    UpRxPackets = table.Column<double>(type: "REAL", nullable: true),
                    DownRxPackets = table.Column<double>(type: "REAL", nullable: true),
                    UpTxPackets = table.Column<double>(type: "REAL", nullable: true),
                    DownTxPackets = table.Column<double>(type: "REAL", nullable: true),
                    LateCollisions = table.Column<double>(type: "REAL", nullable: true),
                    StpChanges = table.Column<double>(type: "REAL", nullable: true),
                    UpErrorsBlind = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpLinkDownCount = table.Column<double>(type: "REAL", nullable: true),
                    DownLinkDownCount = table.Column<double>(type: "REAL", nullable: true),
                    DownCarrierChanges = table.Column<double>(type: "REAL", nullable: true),
                    MaxInBps = table.Column<double>(type: "REAL", nullable: true),
                    MaxOutBps = table.Column<double>(type: "REAL", nullable: true),
                    MeanInBps = table.Column<double>(type: "REAL", nullable: true),
                    MeanOutBps = table.Column<double>(type: "REAL", nullable: true),
                    BroadcastPps = table.Column<double>(type: "REAL", nullable: true),
                    MulticastPps = table.Column<double>(type: "REAL", nullable: true),
                    PacketsPps = table.Column<double>(type: "REAL", nullable: true),
                    SpeedMinMbps = table.Column<int>(type: "INTEGER", nullable: true),
                    SpeedMaxMbps = table.Column<int>(type: "INTEGER", nullable: true),
                    HalfDuplex = table.Column<bool>(type: "INTEGER", nullable: true),
                    MeshSignalDbm = table.Column<int>(type: "INTEGER", nullable: true),
                    MeshTxRateKbps = table.Column<long>(type: "INTEGER", nullable: true),
                    MloMembers = table.Column<int>(type: "INTEGER", nullable: true),
                    ParentMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ProbeLossPct = table.Column<double>(type: "REAL", nullable: true),
                    PoeWattsMin = table.Column<double>(type: "REAL", nullable: true),
                    PoeWattsMax = table.Column<double>(type: "REAL", nullable: true),
                    Reboots = table.Column<int>(type: "INTEGER", nullable: false),
                    InformFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    IncidentsJson = table.Column<string>(type: "TEXT", maxLength: 16000, nullable: true),
                    CoverageJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanHealthLinkHours", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LanHealthSiteHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HourStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Score = table.Column<double>(type: "REAL", nullable: true),
                    HourImpairment = table.Column<double>(type: "REAL", nullable: true),
                    Incidents = table.Column<int>(type: "INTEGER", nullable: false),
                    Explained = table.Column<int>(type: "INTEGER", nullable: false),
                    FabricLinks = table.Column<int>(type: "INTEGER", nullable: false),
                    AccessPorts = table.Column<int>(type: "INTEGER", nullable: false),
                    Worst = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LanHealthSiteHours", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LanHealthExclusions_Scope_Key",
                table: "LanHealthExclusions",
                columns: new[] { "Scope", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LanHealthLinkHours_HourStart_LinkId",
                table: "LanHealthLinkHours",
                columns: new[] { "HourStart", "LinkId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LanHealthLinkHours_LinkId",
                table: "LanHealthLinkHours",
                column: "LinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LanHealthSiteHours_HourStart",
                table: "LanHealthSiteHours",
                column: "HourStart",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LanHealthExclusions");

            migrationBuilder.DropTable(
                name: "LanHealthLinkHours");

            migrationBuilder.DropTable(
                name: "LanHealthSiteHours");
        }
    }
}
