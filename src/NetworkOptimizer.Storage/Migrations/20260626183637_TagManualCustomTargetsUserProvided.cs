using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Classify hand-added Transit / Access ISP targets as UserProvided (DiscoveryMethod = 2)
    /// so upstream change-detection treats them as curated - never flagged "removed", never
    /// re-suggested as "added" - while ISP Health still grades them by TargetType.
    ///
    /// Scoped tightly to user-origin rows: only TargetId LIKE 'custom-%' (the tracer never mints
    /// custom-* ids - it uses access-*/transit-as*/path-*), only AutoDiscovered = 0, and only
    /// DiscoveryMethod IS NULL. The null gate is deliberate: a manual transit that discovery later
    /// confirmed on-path is promoted to DirectRouter (UpstreamTracerService upsert), and those must
    /// stay DirectRouter so they remain eligible for removed-detection. TargetType 2 = AccessIsp,
    /// 3 = Transit; DiscoveryMethod 2 = UserProvided.
    /// </summary>
    public partial class TagManualCustomTargetsUserProvided : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET DiscoveryMethod = 2 " +
                "WHERE TargetId LIKE 'custom-%' AND TargetType IN (2, 3) " +
                "AND AutoDiscovered = 0 AND DiscoveryMethod IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Best-effort reversal: these rows had no DiscoveryMethod before.
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET DiscoveryMethod = NULL " +
                "WHERE TargetId LIKE 'custom-%' AND TargetType IN (2, 3) " +
                "AND AutoDiscovered = 0 AND DiscoveryMethod = 2;");
        }
    }
}
