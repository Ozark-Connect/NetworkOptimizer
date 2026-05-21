using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Tracks when the upstream tracer last committed results and whether the auto
    /// re-discovery scheduler has surfaced a change that needs user review.
    /// </summary>
    public partial class AddUpstreamDiscoverySchedulerState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<System.DateTime>(
                name: "LastUpstreamDiscoveryAt",
                table: "MonitoringSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UpstreamDiscoveryNeedsReview",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastUpstreamDiscoveryAt",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "UpstreamDiscoveryNeedsReview",
                table: "MonitoringSettings");
        }
    }
}
