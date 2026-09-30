using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations.Auth
{
    /// <summary>
    /// Deletes the authz.denied rows the method gate wrote for IDisposable.Dispose. The cellular
    /// monitor's gated interface inherited IDisposable, so every scope end called Dispose through the
    /// gate and was refused. No user did anything, and the rows buried real denials. Matched exactly,
    /// so no other denial is touched. Data only: the model is unchanged.
    /// </summary>
    public partial class RemoveDisposeDenialAuditNoise : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "DELETE FROM \"AuditEvents\" " +
                "WHERE \"Action\" = 'authz.denied' " +
                "AND \"DetailsJson\" = '{\"reason\":\"IDisposable.Dispose declares no role gate\"}';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The deleted rows recorded nothing a user did; there is nothing to restore.
        }
    }
}
