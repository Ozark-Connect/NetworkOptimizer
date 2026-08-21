using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Backfills ?at= onto alert links that predate it, so an alert in History opens the window it
    /// is about rather than wherever the tab was left. Runs per site database, like every migration
    /// here, so each site repairs its own rows.
    ///
    /// Bounded to 90 days: that is the short-term bucket's retention, and framing an hour with no
    /// telemetry behind it would be a correct link onto an empty chart.
    ///
    /// Every statement is guarded on the row NOT already carrying at=, so it cannot double-stamp a
    /// link and is safe to re-run. The at= is spliced in after the tab, leaving each row's own host
    /// prefix, selectors and &amp;site= suffix exactly as they are.
    /// </summary>
    public partial class FrameAlertLinksOnTheirMoment : Migration
    {
        // Text datetimes read back as Unspecified, so the kind is stated rather than assumed.
        private const string AtMs = "(CAST(strftime('%s', TriggeredAt) AS INTEGER) * 1000)";
        private const string Recent = "TriggeredAt >= datetime('now', '-90 days')";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hardware stat tabs: the instant alone, since each row already names its own module.
            foreach (var tab in new[] { "sfp", "ont", "cm", "cellular", "starlink" })
            {
                migrationBuilder.Sql($@"
                    UPDATE AlertHistory
                    SET SourceUrl = REPLACE(SourceUrl, 'tab={tab}', 'tab={tab}&at=' || {AtMs})
                    WHERE SourceUrl LIKE '%tab={tab}%'
                      AND SourceUrl NOT LIKE '%at=%'
                      AND {Recent};");
            }

            // Device Stats: the instant, plus the device where the row knows it, so the link picks
            // the device out of the framed window rather than landing on all of them.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = REPLACE(
                        SourceUrl, 'tab=devices',
                        'tab=devices&at=' || {AtMs} ||
                        CASE WHEN DeviceId IS NULL OR DeviceId = '' THEN '' ELSE '&device=' || DeviceId END)
                WHERE SourceUrl LIKE '%tab=devices%'
                  AND SourceUrl NOT LIKE '%at=%'
                  AND {Recent};");

            // Network Performance rows predating the category too. The target type is in the alert's
            // own context, so each row can be given the category it always belonged to. Matched with
            // LIKE rather than json_extract, which needs an extension this may not be built with.
            //
            // Fabric and Custom ask for every WAN: neither is reached over one, and naming none
            // leaves the page on whichever WAN its filter was last set to. AccessIsp and Transit
            // name no WAN, which is what an unpinned one of those does today.
            PerformanceRow(migrationBuilder, "\"target_type\":\"Fabric\"", "Fabric", allWans: true);
            PerformanceRow(migrationBuilder, "\"target_type\":\"AccessIsp\"", "AccessIsp", allWans: false);
            PerformanceRow(migrationBuilder, "\"target_type\":\"Transit\"", "Transit", allWans: false);
            // Both name the WAN's own service, and both are reached over that one WAN.
            PerformanceRow(migrationBuilder, "\"target_type\":\"InternetService\"", "InternetService", allWans: false);
            PerformanceRow(migrationBuilder, "\"target_type\":\"Wan\"", "InternetService", allWans: false);
            // Anything left on that tab is a custom target, including rows with no context at all.
            PerformanceRow(migrationBuilder, null, "Custom", allWans: true);

            // A row that already knows its category only needs the window. None exist on the
            // installs this was checked against, but the branches above all skip such a row, and
            // being skipped silently is how a link stays broken.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = REPLACE(SourceUrl, 'tab=performance', 'tab=performance&at=' || {AtMs})
                WHERE SourceUrl LIKE '%tab=performance%'
                  AND SourceUrl NOT LIKE '%at=%'
                  AND {Recent};");
        }

        /// <summary>One category's share of the Network Performance rows.</summary>
        private static void PerformanceRow(MigrationBuilder builder, string contextMarker, string category, bool allWans)
        {
            var wan = allWans ? " || '&wan=all'" : "";
            var context = contextMarker is null ? "" : $"AND ContextJson LIKE '%{contextMarker}%'";
            builder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = REPLACE(
                        SourceUrl, 'tab=performance',
                        'tab=performance&category={category}&at=' || {AtMs}{wan})
                WHERE SourceUrl LIKE '%tab=performance%'
                  AND SourceUrl NOT LIKE '%at=%'
                  AND SourceUrl NOT LIKE '%category=%'
                  {context}
                  AND {Recent};");
        }

        /// <summary>
        /// Deliberately not reversed. The category and WAN this adds are indistinguishable from the
        /// ones links have always carried, so an inverse would have to guess which parts it wrote.
        /// Nothing here changes schema, and a link with a window is not a state to roll back to.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
