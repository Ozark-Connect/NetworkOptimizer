using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddNeighborSightingCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Backfill pre-existing rows at full weight: they were captured before the count
            // existed, under the prior accepted behavior, and off-channel rows would never be
            // re-counted (a serving radio can't currently see them). New sightings start at 1
            // in code and climb, so the persistence gate applies going forward.
            migrationBuilder.AddColumn<int>(
                name: "SightingCount",
                table: "ApNeighborSightings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SightingCount",
                table: "ApNeighborSightings");
        }
    }
}
