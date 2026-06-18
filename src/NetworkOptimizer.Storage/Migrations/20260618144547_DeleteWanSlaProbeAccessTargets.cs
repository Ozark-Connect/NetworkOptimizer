using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Remove UniFi WAN SLA probe targets (1.1.1.1 / 8.8.8.8) that an earlier upstream
    /// discovery could mistake for the L2 neighbor and save as Access ISP targets. These
    /// are public DNS resolvers, not ISP first-mile infrastructure. TargetType 2 = AccessIsp.
    /// </summary>
    public partial class DeleteWanSlaProbeAccessTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM MonitoringTargets WHERE TargetType = 2 AND Address IN ('1.1.1.1', '8.8.8.8');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration - cannot restore deleted rows
        }
    }
}
