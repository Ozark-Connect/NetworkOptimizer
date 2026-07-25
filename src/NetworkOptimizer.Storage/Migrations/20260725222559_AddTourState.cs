using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTourState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FirstSeenVersion",
                table: "AdminSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastSeenAppVersion",
                table: "AdminSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TourStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Subject = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SeenTourSteps = table.Column<string>(type: "TEXT", nullable: false),
                    DismissedTours = table.Column<string>(type: "TEXT", nullable: false),
                    TourOffers = table.Column<string>(type: "TEXT", nullable: false),
                    ToursDisabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TourStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TourStates_Subject",
                table: "TourStates",
                column: "Subject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TourStates");

            migrationBuilder.DropColumn(
                name: "FirstSeenVersion",
                table: "AdminSettings");

            migrationBuilder.DropColumn(
                name: "LastSeenAppVersion",
                table: "AdminSettings");
        }
    }
}
