using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Records which WAN holds the primary role, and whether the site load balances, so the answer
    /// survives away from a console. Primary is a role rather than a name - any WAN group can hold
    /// it - and the paths that need it most cannot ask: the probe-push path runs on the tunnel's
    /// background thread with no console call available, and the offline scoring fallbacks would
    /// otherwise guess at the conventional first group and be wrong on a WAN2-primary site.
    ///
    /// Both are nullable on purpose: null means no connected compute has resolved the role yet, and
    /// readers must treat that as unknown - falling back to their documented guess - rather than as
    /// a negative answer.
    /// </summary>
    public partial class AddWanProfileRoleMarkers : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPrimary", table: "WanProfiles", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<bool>(
                name: "SiteLoadBalances", table: "WanProfiles", type: "INTEGER", nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsPrimary", table: "WanProfiles");
            migrationBuilder.DropColumn(name: "SiteLoadBalances", table: "WanProfiles");
        }
    }
}
