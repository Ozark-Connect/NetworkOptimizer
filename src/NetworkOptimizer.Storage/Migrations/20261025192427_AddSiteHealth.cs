using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "HealthAfter",
                table: "ConfigChanges",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HealthBefore",
                table: "ConfigChanges",
                type: "REAL",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SiteHealthHours",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HourStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Health = table.Column<double>(type: "REAL", nullable: true),
                    Internet = table.Column<double>(type: "REAL", nullable: true),
                    InternetIsp = table.Column<double>(type: "REAL", nullable: true),
                    Wifi = table.Column<double>(type: "REAL", nullable: true),
                    Availability = table.Column<double>(type: "REAL", nullable: true),
                    Lan = table.Column<double>(type: "REAL", nullable: true),
                    WirelessShare = table.Column<double>(type: "REAL", nullable: true),
                    Clients = table.Column<int>(type: "INTEGER", nullable: true),
                    LossPct = table.Column<double>(type: "REAL", nullable: true),
                    AccessRttMs = table.Column<double>(type: "REAL", nullable: true),
                    LoadedDeltaMs = table.Column<double>(type: "REAL", nullable: true),
                    LoadedShare = table.Column<double>(type: "REAL", nullable: true),
                    Worst = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DetailsJson = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    ComputedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteHealthHours", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteHealthHours_HourStart",
                table: "SiteHealthHours",
                column: "HourStart",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteHealthHours");

            migrationBuilder.DropColumn(
                name: "HealthAfter",
                table: "ConfigChanges");

            migrationBuilder.DropColumn(
                name: "HealthBefore",
                table: "ConfigChanges");
        }
    }
}
