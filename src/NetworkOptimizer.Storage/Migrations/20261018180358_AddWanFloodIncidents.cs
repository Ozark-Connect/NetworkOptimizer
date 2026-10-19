using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanFloodIncidents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanFloodIncidents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    IncidentId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Wan = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PeakUnsolicitedPps = table.Column<int>(type: "INTEGER", nullable: false),
                    PeakSoftirqPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    HotCpu = table.Column<int>(type: "INTEGER", nullable: false),
                    PeakConntrackPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    SoftnetDropped = table.Column<long>(type: "INTEGER", nullable: false),
                    TableFull = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActionsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Delivery = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanFloodIncidents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WanFloodIncidents_IncidentId",
                table: "WanFloodIncidents",
                column: "IncidentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WanFloodIncidents_StartedAt",
                table: "WanFloodIncidents",
                column: "StartedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WanFloodIncidents");
        }
    }
}
