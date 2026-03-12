using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Purge negative CrowdSec cache entries that may contain stale data.
    /// A prior bug cached rate-limited lookups as "null" (indistinguishable from
    /// a real 404 NotFound). These entries make IPs appear as "unknown" in the UI
    /// when they were never actually checked. Legitimate negative entries have a
    /// 24-hour TTL and will be re-checked automatically.
    /// </summary>
    public partial class PurgeStaleCrowdSecNegativeCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM CrowdSecReputations WHERE ReputationJson = 'null';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data-only migration - cannot restore deleted rows
        }
    }
}
