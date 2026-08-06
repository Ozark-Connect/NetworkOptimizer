using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Restores the poll intervals a metered WAN's discovery commit overwrote on targets that
    /// were never its to touch.
    /// <para>
    /// A commit for a metered WAN slows the targets already on that WAN to the plan's cadence, so
    /// a link just declared metered does not keep paying for 10s probing. It selected those rows
    /// through an ownership test that read an UNSTAMPED row (no WanInterface) as owned by whatever
    /// WAN happened to be committing, rather than by the primary. On a multi-WAN site every
    /// hand-added target that had never been assigned to a WAN context was therefore adopted by
    /// the metered WAN's run and slowed with it - Access ISP, Transit, Custom and Internet alike,
    /// down to LAN targets that cost the metered link nothing. The ownership test is fixed in
    /// UpstreamTracerService.OwnsTargetRow; this repairs the rows it already rewrote.
    /// </para>
    /// <para>
    /// The original cadences were never recorded anywhere, so they are inferred. Only the two
    /// intervals a metered plan can produce are treated as suspect (30s at rung 1, 60s at rung 2),
    /// and only on sites that actually have a metered WAN - nowhere else could the overwrite have
    /// run. A private address goes back to the default, since no WAN's data plan has any claim on
    /// LAN traffic. Everything else takes the most common cadence among targets of its own type
    /// that are still probing faster than a metered plan allows, which is exactly the set that
    /// escaped: rows carrying a WAN stamp, on WANs that are not metered. With no such sibling to
    /// learn from, the default stands in.
    /// </para>
    /// <para>
    /// A target deliberately set to 30s or 60s by hand, on a site with a metered WAN, and never
    /// assigned to a WAN context, is indistinguishable from a clobbered one and will be sped up.
    /// That is the accepted cost of repairing the rest: it is a poll interval, it is visible in
    /// Latency Targets, and it is one click to set back.
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

            // Then say what the absent WAN meant, rather than leaving every reader to infer it.
            // A target with no WAN is one whose probe is not pinned to a WAN - it leaves by the
            // box's own route, which measures the primary on a failover site and no single WAN on
            // one that load balances. Runs AFTER the restore above, which keys on NULL.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = 'unpinned'
WHERE WanInterface IS NULL OR WanInterface = '';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The intervals this repairs were lost before it ran, so there is no prior state to
            // put back - re-slowing every restored row to a cadence it may never have had would be
            // a second guess, not a rollback. The unpinned marker does reverse cleanly.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = NULL WHERE WanInterface = 'unpinned';");
        }
    }
}
