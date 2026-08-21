using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Rewrites speed test alert links from <c>#result-N</c> to <c>?result=N</c>, and gives the WAN
    /// ones the <c>wan=</c> filter their page reads.
    ///
    /// The fragment never worked: a browser does not send one to the server, so Blazor could not
    /// see it and the link opened the page at whatever the reader was last looking at. Two shapes
    /// exist, because site stamping used to append after the fragment as well:
    /// <c>/speedtest#result-5554?site=main</c> and <c>/wan-speedtest?site=main#result-5810</c>.
    ///
    /// The WAN of a result is not in the link, so it comes from the result itself. A bonded test
    /// records "WAN+WAN2", which becomes the comma list the analysis charts read.
    ///
    /// Runs per site database. Skips rows already converted, so it is safe to re-run.
    /// </summary>
    public partial class SpeedTestAlertLinksUseAQueryParam : Migration
    {
        // Everything after "#result-", which is either "5554?site=main" or "5810".
        private const string Tail = "substr(SourceUrl, instr(SourceUrl, '#result-') + 8)";
        // The id alone: up to the '?' where one followed the fragment, otherwise the whole tail.
        private const string Id = $"CASE WHEN instr({Tail}, '?') > 0 THEN substr({Tail}, 1, instr({Tail}, '?') - 1) ELSE {Tail} END";
        // What site stamping appended after the fragment, if anything: "site=main".
        private const string Stranded = $"CASE WHEN instr({Tail}, '?') > 0 THEN '&' || substr({Tail}, instr({Tail}, '?') + 1) ELSE '' END";
        // Everything before the fragment, which is where the query belongs.
        private const string Head = "substr(SourceUrl, 1, instr(SourceUrl, '#result-') - 1)";
        private const string Separator = $"CASE WHEN instr({Head}, '?') > 0 THEN '&' ELSE '?' END";
        private const string Recent = "TriggeredAt >= datetime('now', '-90 days')";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // WAN speed tests first, while the id is still findable in the fragment: the filter
            // comes from the result's own WanNetworkGroup, which is the key format that page parses.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = {Head} || {Separator} || 'wan='
                    || replace(lower((SELECT r.WanNetworkGroup FROM Iperf3Results r
                                      WHERE r.Id = CAST({Id} AS INTEGER))), '+', '%2C')
                    || '&result=' || {Id} || {Stranded}
                WHERE SourceUrl LIKE '%/wan-speedtest%#result-%'
                  AND SourceUrl NOT LIKE '%wan=%'
                  AND (SELECT r.WanNetworkGroup FROM Iperf3Results r
                       WHERE r.Id = CAST({Id} AS INTEGER)) IS NOT NULL
                  AND {Recent};");

            // Everything else with a fragment, including WAN rows whose result is gone or already
            // carried a WAN: the fragment becomes the parameter and nothing else changes.
            migrationBuilder.Sql($@"
                UPDATE AlertHistory
                SET SourceUrl = {Head} || {Separator} || 'result=' || {Id} || {Stranded}
                WHERE SourceUrl LIKE '%#result-%'
                  AND {Recent};");
        }

        /// <summary>Not reversed: the fragment it replaced never worked, so there is no state worth
        /// returning to.</summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
