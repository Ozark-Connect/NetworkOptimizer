using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Repairs the poll intervals a metered WAN's commit overwrote on targets that were never its
    /// to touch (it owned every unpinned row, not just its own - fixed in
    /// UpstreamTracerService.OwnsTargetRow), then names the unpinned state instead of leaving it
    /// NULL for each reader to interpret.
    /// <para>
    /// The original cadences were never recorded, so they are inferred: only the two intervals a
    /// metered plan produces are suspect (30s at rung 1, 60s at rung 2), and only on sites with a
    /// metered WAN. Private addresses go back to the default - no data plan has a claim on LAN
    /// traffic - and the rest take the modal cadence of their own type among rows still faster than
    /// a metered plan allows, which is exactly the set that escaped. A hand-set 30s or 60s target
    /// is indistinguishable from a clobbered one and gets sped up; that is the cost of the repair.
    /// </para>
    /// </summary>
    public partial class RestoreMeteredClobberedIntervals : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // TargetType: 0=Fabric (excluded from the overwrite in the first place, so excluded
            // here), 2=AccessIsp, 3=Transit, 4=Custom, 5=InternetService.
            // AccessTechnology: 6=FixedWireless, 7=Satellite, 8=Cellular - the technologies
            // MeteredProbePolicy assumes are metered. A DataCapGb above zero is the operator
            // saying so outright.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets
SET PollIntervalSeconds = CASE
        WHEN Address GLOB '10.*'
          OR Address GLOB '192.168.*'
          OR Address GLOB '127.*'
          OR Address GLOB '172.1[6-9].*'
          OR Address GLOB '172.2[0-9].*'
          OR Address GLOB '172.3[01].*'
        THEN 10
        ELSE COALESCE((
            SELECT s.PollIntervalSeconds
            FROM MonitoringTargets s
            WHERE s.TargetType = MonitoringTargets.TargetType
              AND s.TargetType <> 0
              AND s.PollIntervalSeconds < 30
            GROUP BY s.PollIntervalSeconds
            ORDER BY COUNT(*) DESC, s.PollIntervalSeconds ASC
            LIMIT 1
        ), 10)
    END
WHERE WanInterface IS NULL
  AND TargetType <> 0
  AND PollIntervalSeconds IN (30, 60)
  AND EXISTS (
        SELECT 1 FROM WanDataUsageConfigs WHERE DataCapGb > 0
        UNION ALL
        SELECT 1 FROM WanDiscoveryContexts WHERE AccessTechnology IN (6, 7, 8)
  );");

            // Then name what the absent WAN meant: not pinned to one. Runs AFTER the restore,
            // which keys on NULL.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = 'unpinned'
WHERE WanInterface IS NULL OR WanInterface = '';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The intervals were lost before this ran, so there is nothing to put back - re-slowing
            // every row to a cadence it may never have had is a second guess, not a rollback. The
            // unpinned marker does reverse cleanly.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = NULL WHERE WanInterface = 'unpinned';");
        }
    }
}
