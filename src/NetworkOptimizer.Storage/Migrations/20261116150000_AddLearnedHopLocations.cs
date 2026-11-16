using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLearnedHopLocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LearnedHopLocations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteSlug = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    SeenAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnedHopLocations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LearnedHopLocations_SiteSlug_Address",
                table: "LearnedHopLocations",
                columns: new[] { "SiteSlug", "Address" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LearnedHopLocations");
        }
    }
}
