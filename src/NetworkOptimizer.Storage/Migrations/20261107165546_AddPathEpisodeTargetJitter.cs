using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPathEpisodeTargetJitter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "BaselineJitterMs",
                table: "PathEpisodeTargets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PeakJitterMs",
                table: "PathEpisodeTargets",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineJitterMs",
                table: "PathEpisodeTargets");

            migrationBuilder.DropColumn(
                name: "PeakJitterMs",
                table: "PathEpisodeTargets");
        }
    }
}
