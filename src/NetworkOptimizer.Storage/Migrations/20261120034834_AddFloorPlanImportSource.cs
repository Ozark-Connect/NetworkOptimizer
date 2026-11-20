using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFloorPlanImportSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceRef",
                table: "PlannedAps",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "HeightM",
                table: "FloorPlans",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceImportedAt",
                table: "FloorPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePlacementJson",
                table: "FloorPlans",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceRef",
                table: "FloorPlans",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SourceRef",
                table: "PlannedAps");

            migrationBuilder.DropColumn(
                name: "HeightM",
                table: "FloorPlans");

            migrationBuilder.DropColumn(
                name: "SourceImportedAt",
                table: "FloorPlans");

            migrationBuilder.DropColumn(
                name: "SourcePlacementJson",
                table: "FloorPlans");

            migrationBuilder.DropColumn(
                name: "SourceRef",
                table: "FloorPlans");
        }
    }
}
