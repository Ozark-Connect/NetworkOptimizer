using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// The two shapes FrameAlertLinksOnTheirMoment could not reach.
    ///
    /// Its Network Performance branches are all guarded on the row NOT already carrying a category,
    /// which excludes every row written since links began stamping one - the common case on an
    /// install upgrading from v2.7.0, not the rarity that migration's comment assumed. Those rows
    /// have a category and a window and are still missing the WAN, so a Custom or Fabric alert opens
    /// on whichever WAN the analysis filter was last left on.
    ///
    /// Device offline, recovered and rebooted rows carried no SourceUrl at all, so a REPLACE had
    /// nothing to match and they were skipped everywhere. They get the link built outright.
    ///
    /// Same 90 day bound and the same idempotence: each statement is guarded on the absence of what
    /// it writes, so re-running changes nothing.
    /// </summary>
    public partial class BackfillRemainingAlertLinks : Migration
    {
        // Text datetimes read back as Unspecified, so the kind is stated rather than assumed.
        private const string AtMs = "(CAST(strftime('%s', TriggeredAt) AS INTEGER) * 1000)";
        private const string Recent = "TriggeredAt >= datetime('now', '-90 days')";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Neither a LAN target nor an unpinned custom one is reached over a single WAN, so both
            // ask for all of them. A custom target that IS pinned already carries its own wan=, and
            // the guard leaves it alone - the absence of wan= is what identifies an unpinned row.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = REPLACE(SourceUrl, 'tab=performance', 'tab=performance&wan=all')
                WHERE SourceUrl LIKE '%tab=performance%'
                  AND SourceUrl NOT LIKE '%wan=%'
                  AND (SourceUrl LIKE '%category=Custom%' OR SourceUrl LIKE '%category=Fabric%')
                  AND {Recent};");

            // Matches MonitoringLinks.DeviceStats, including the escaping Uri.EscapeDataString
            // applies to a MAC, so a backfilled link is indistinguishable from a freshly written one.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = '/monitoring?tab=devices&at=' || {AtMs}
                    || CASE WHEN DeviceId IS NULL OR DeviceId = '' THEN ''
                            ELSE '&device=' || REPLACE(DeviceId, ':', '%3A') END
                WHERE (SourceUrl IS NULL OR SourceUrl = '')
                  AND EventType IN ('device.offline', 'device.recovered', 'device.rebooted')
                  AND {Recent};");
        }

        /// <summary>
        /// Deliberately not reversed, for the same reason as FrameAlertLinksOnTheirMoment: what this
        /// writes is indistinguishable from what the app writes, so an inverse would have to guess
        /// which rows it authored. Nothing here changes schema.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
