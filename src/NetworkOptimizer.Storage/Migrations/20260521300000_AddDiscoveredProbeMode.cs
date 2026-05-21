using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Adds MonitoringTargets.DiscoveredProbeMode for the upstream tracer (Build #10).
    /// Records the probe mode a target answered to during initial discovery so the
    /// re-validation flow can detect drift between original and current modes.
    /// </summary>
    public partial class AddDiscoveredProbeMode : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DiscoveredProbeMode",
                table: "MonitoringTargets",
                type: "INTEGER",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscoveredProbeMode",
                table: "MonitoringTargets");
        }
    }
}
