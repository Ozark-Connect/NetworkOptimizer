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
                name: "JitterP95Ms",
                table: "PathEpisodeTargets",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ConcurrentCongestion",
                table: "PathEpisodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BaselineJitterMs",
                table: "PathEpisodeTargets");

            migrationBuilder.DropColumn(
                name: "JitterP95Ms",
                table: "PathEpisodeTargets");

            migrationBuilder.DropColumn(
                name: "ConcurrentCongestion",
                table: "PathEpisodes");
        }
    }
}
