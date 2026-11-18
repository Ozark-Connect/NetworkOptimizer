using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddHopLocationOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HopLocationOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Address = table.Column<string>(type: "TEXT", maxLength: 45, nullable: false),
                    City = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    Region = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    SetBy = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SetAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HopLocationOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HopLocationOverrides_Address",
                table: "HopLocationOverrides",
                column: "Address",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HopLocationOverrides");
        }
    }
}
