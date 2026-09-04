using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddApAgentRoamAndRadioHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApAgentEventCursors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LastSeq = table.Column<long>(type: "INTEGER", nullable: false),
                    AgentStartedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPolledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTruncatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TruncationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    DroppedEvents = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApAgentEventCursors", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApRadioHealthSamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Radio = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Band = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    SampleAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    WindowSeconds = table.Column<double>(type: "REAL", nullable: false),
                    CycleDelta = table.Column<long>(type: "INTEGER", nullable: true),
                    RxClearDelta = table.Column<long>(type: "INTEGER", nullable: true),
                    TxFrameDelta = table.Column<long>(type: "INTEGER", nullable: true),
                    PhyErrDelta = table.Column<long>(type: "INTEGER", nullable: true),
                    PdevResets = table.Column<long>(type: "INTEGER", nullable: true),
                    PdevResetDelta = table.Column<long>(type: "INTEGER", nullable: true),
                    BusyRatio = table.Column<double>(type: "REAL", nullable: true),
                    Wedged = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApRadioHealthSamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ApRoamRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RoamedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ObservedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClientMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    LinkMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FromApMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    FromBssid = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ToApMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ToBssid = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    Band = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Channel = table.Column<int>(type: "INTEGER", nullable: true),
                    FromBand = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    FromChannel = table.Column<int>(type: "INTEGER", nullable: true),
                    DwellSeconds = table.Column<double>(type: "REAL", nullable: true),
                    AuthRssiDbm = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthDeltaMs = table.Column<int>(type: "INTEGER", nullable: true),
                    AssocDeltaMs = table.Column<int>(type: "INTEGER", nullable: true),
                    WpaAuthDeltaMs = table.Column<int>(type: "INTEGER", nullable: true),
                    AuthAlgo = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    ObservedByApMacs = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    ObservationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    AfterEventGap = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApRoamRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApAgentEventCursors_DeviceMac",
                table: "ApAgentEventCursors",
                column: "DeviceMac",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApRadioHealthSamples_ApMac_Radio_SampleAt",
                table: "ApRadioHealthSamples",
                columns: new[] { "ApMac", "Radio", "SampleAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApRadioHealthSamples_SampleAt",
                table: "ApRadioHealthSamples",
                column: "SampleAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApRoamRecords_ClientMac_RoamedAt",
                table: "ApRoamRecords",
                columns: new[] { "ClientMac", "RoamedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ApRoamRecords_RoamedAt",
                table: "ApRoamRecords",
                column: "RoamedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ApRoamRecords_ToApMac_RoamedAt",
                table: "ApRoamRecords",
                columns: new[] { "ToApMac", "RoamedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApAgentEventCursors");

            migrationBuilder.DropTable(
                name: "ApRadioHealthSamples");

            migrationBuilder.DropTable(
                name: "ApRoamRecords");
        }
    }
}
