using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Reconciles the two WAN keys a target can carry. MonitoringTarget.WanContextId says who
    /// probes a target (the routing key, set by hand in the per-target WAN dropdown), while
    /// MonitoringTarget.WanInterface says which WAN its data describes (the reading key, written
    /// by upstream discovery). Contexts predate the WanInterface column on WanContext, so a
    /// target assigned to a secondary WAN's context has been carrying no WAN at all, or the
    /// primary's - and no per-WAN reader could find it under the WAN it actually measures.
    ///
    /// Data-only, so there is no schema change and no model change: it copies each context's WAN
    /// onto the targets assigned to that context. The context assignment is always the user's own
    /// statement about a target (discovery never set it before this release), so it is the
    /// authority here and overwrites a WanInterface left over from an earlier primary-WAN
    /// discovery. Targets with no context - every target on a single-WAN install - are untouched.
    /// </summary>
    public partial class BackfillWanContextTargetWan : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets
SET WanInterface = (SELECT c.WanInterface FROM WanContexts c WHERE c.Id = MonitoringTargets.WanContextId)
WHERE WanContextId IS NOT NULL
  AND (SELECT c.WanInterface FROM WanContexts c WHERE c.Id = MonitoringTargets.WanContextId) IS NOT NULL
  AND (SELECT c.WanInterface FROM WanContexts c WHERE c.Id = MonitoringTargets.WanContextId) <> ''
  AND IFNULL(WanInterface, '') <> IFNULL((SELECT c.WanInterface FROM WanContexts c WHERE c.Id = MonitoringTargets.WanContextId), '');");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The prior WanInterface values were unrecoverable guesses (null, or the primary's
            // key from a discovery that never knew about this WAN), so there is nothing truthful
            // to restore. Leaving the corrected values in place is the honest no-op.
        }
    }
}
