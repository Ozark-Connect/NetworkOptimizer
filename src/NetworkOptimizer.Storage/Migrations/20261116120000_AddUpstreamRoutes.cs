using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddUpstreamRoutes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RouteJson",
                table: "UpstreamDiscoveries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RouteShiftsJson",
                table: "UpstreamDiscoveries",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RouteJson", table: "UpstreamDiscoveries");
            migrationBuilder.DropColumn(name: "RouteShiftsJson", table: "UpstreamDiscoveries");
        }
    }
}
