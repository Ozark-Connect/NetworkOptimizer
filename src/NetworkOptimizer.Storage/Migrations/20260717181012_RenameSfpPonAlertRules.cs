using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RenameSfpPonAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the "PON" qualifier: these alerts fire for AE optical modules too, not just
            // PON. Keyed on the stable EventTypePattern and guarded on the old default name so a
            // user-renamed rule is left untouched.
            migrationBuilder.Sql(
                "UPDATE AlertRules SET Name = 'SFP: RX Power Low' " +
                "WHERE EventTypePattern = 'monitoring.sfp_rx_power' AND Name = 'SFP: PON RX Power Low';");
            migrationBuilder.Sql(
                "UPDATE AlertRules SET Name = 'SFP: TX Power High' " +
                "WHERE EventTypePattern = 'monitoring.sfp_tx_power' AND Name = 'SFP: PON TX Power High';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE AlertRules SET Name = 'SFP: PON RX Power Low' " +
                "WHERE EventTypePattern = 'monitoring.sfp_rx_power' AND Name = 'SFP: RX Power Low';");
            migrationBuilder.Sql(
                "UPDATE AlertRules SET Name = 'SFP: PON TX Power High' " +
                "WHERE EventTypePattern = 'monitoring.sfp_tx_power' AND Name = 'SFP: TX Power High';");
        }
    }
}
