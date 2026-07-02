using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNeighborSightingMemory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApNeighborSightings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ApMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    Band = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Bssid = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                    Channel = table.Column<int>(type: "INTEGER", nullable: false),
                    WidthMhz = table.Column<int>(type: "INTEGER", nullable: false),
                    SignalDbm = table.Column<int>(type: "INTEGER", nullable: false),
                    Ssid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    FirstSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApNeighborSightings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApNeighborSightings_ApMac_Band_Bssid_Channel",
                table: "ApNeighborSightings",
                columns: new[] { "ApMac", "Band", "Bssid", "Channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApNeighborSightings_LastSeenUtc",
                table: "ApNeighborSightings",
                column: "LastSeenUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApNeighborSightings");
        }
    }
}
