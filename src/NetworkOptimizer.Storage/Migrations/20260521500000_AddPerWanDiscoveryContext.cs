using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Splits upstream tracer state from MonitoringSettings (one row, single-WAN
    /// assumption) into WanDiscoveryContexts (one row per WAN). Adds WanInterface
    /// column on MonitoringTarget so access/transit targets can be scoped to the WAN
    /// they were discovered against. Backfills the existing global state into a
    /// "wan1" row so single-WAN deployments keep working with no user action.
    /// </summary>
    public partial class AddPerWanDiscoveryContext : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanDiscoveryContexts",
                columns: table => new
                {
                    WanInterface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    L2NeighborMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    L2NeighborOui = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    AccessTechnology = table.Column<int>(type: "INTEGER", nullable: false),
                    LastDiscoveryAt = table.Column<System.DateTime>(type: "TEXT", nullable: true),
                    NeedsReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<System.DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanDiscoveryContexts", x => x.WanInterface);
                });

            migrationBuilder.AddColumn<string>(
                name: "WanInterface",
                table: "MonitoringTargets",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_WanInterface",
                table: "MonitoringTargets",
                column: "WanInterface");

            // Backfill: copy the existing global tracer state from MonitoringSettings
            // into a wan1 row if there's any non-default state. SQLite syntax. The
            // INSERT is conditional - only fires when the global state suggests the
            // tracer ran. Otherwise this user just hasn't run discovery yet.
            migrationBuilder.Sql(@"
INSERT INTO WanDiscoveryContexts (WanInterface, L2NeighborMac, L2NeighborOui, AccessTechnology, LastDiscoveryAt, NeedsReview, CreatedAt, UpdatedAt)
SELECT 'wan1', s.WanNeighborMac, s.WanNeighborOui, s.AccessTechnology, s.LastUpstreamDiscoveryAt, s.UpstreamDiscoveryNeedsReview, datetime('now'), datetime('now')
FROM MonitoringSettings s
WHERE s.WanNeighborMac IS NOT NULL OR s.WanNeighborOui IS NOT NULL OR s.LastUpstreamDiscoveryAt IS NOT NULL;");

            // Backfill: existing tracer-origin targets all came from the (only) WAN we
            // could track, so attribute them to wan1.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = 'wan1'
WHERE WanInterface IS NULL
  AND DiscoveryMethod IS NOT NULL
  AND TargetType IN (1, 2);"); // 1=Wan, 2=AccessIsp per MonitoringTargetType - adjust if order changes

            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = 'wan1'
WHERE WanInterface IS NULL AND TargetType = 3;"); // 3=Transit
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(name: "IX_MonitoringTargets_WanInterface", table: "MonitoringTargets");
            migrationBuilder.DropColumn(name: "WanInterface", table: "MonitoringTargets");
            migrationBuilder.DropTable(name: "WanDiscoveryContexts");
        }
    }
}
