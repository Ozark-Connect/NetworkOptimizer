using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Folds the legacy 'wan1' WAN key into 'wan'. Migration 20260521500000 stamped existing rows
    /// 'wan1' when per-WAN discovery contexts arrived; everything written since uses 'wan', which
    /// is what GatewayWanHelper produces for the first WAN group. The two spellings named the same
    /// WAN and nothing minded while only one WAN was ever read.
    ///
    /// Per-WAN reading makes them disagree. A discovery run committing 'wan' does not recognize a
    /// 'wan1' row as its own, so it creates a second, WAN-qualified target beside it - a legacy
    /// single-WAN install would quietly double its access and transit targets on the next run. The
    /// per-WAN scorer likewise excludes 'wan1' rows from the 'wan' report, taking their upstream
    /// hops and their access technology with them.
    ///
    /// The runtime paths normalize both spellings, so this migration is about the stored data:
    /// one key per WAN, so a row means what it says. Data-only - no schema or model change.
    ///
    /// WanDiscoveryContexts is keyed by WanInterface, so a site holding both spellings cannot
    /// simply have its 'wan1' row renamed. The newer row wins (it describes the more recent
    /// discovery) and the stale one is dropped.
    /// </summary>
    public partial class NormalizeLegacyWan1Key : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Discovery contexts: drop the older spelling where both exist, then rename what is left.
            migrationBuilder.Sql(@"
DELETE FROM WanDiscoveryContexts
WHERE WanInterface = 'wan1'
  AND EXISTS (SELECT 1 FROM WanDiscoveryContexts w WHERE w.WanInterface = 'wan')
  AND IFNULL(LastDiscoveryAt, '') <= IFNULL(
        (SELECT w.LastDiscoveryAt FROM WanDiscoveryContexts w WHERE w.WanInterface = 'wan'), '');");

            migrationBuilder.Sql(@"
DELETE FROM WanDiscoveryContexts
WHERE WanInterface = 'wan'
  AND EXISTS (SELECT 1 FROM WanDiscoveryContexts w WHERE w.WanInterface = 'wan1');");

            migrationBuilder.Sql(@"
UPDATE WanDiscoveryContexts SET WanInterface = 'wan' WHERE WanInterface = 'wan1';");

            // Targets and discoveries carry no uniqueness on the WAN key, so a plain rename is safe.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET WanInterface = 'wan' WHERE WanInterface = 'wan1';");

            migrationBuilder.Sql(@"
UPDATE UpstreamDiscoveries SET WanInterface = 'wan' WHERE WanInterface = 'wan1';");

            // A context created against the legacy spelling reads under it too.
            migrationBuilder.Sql(@"
UPDATE WanContexts SET WanInterface = 'wan' WHERE WanInterface = 'wan1';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 'wan' is the spelling every writer has used since 20260521500000, so the rows this
            // migration touched are indistinguishable from the ones it did not. Restoring 'wan1'
            // would rename both, which is worse than leaving the normalized key in place - and an
            // older build reads 'wan' correctly anyway.
        }
    }
}
