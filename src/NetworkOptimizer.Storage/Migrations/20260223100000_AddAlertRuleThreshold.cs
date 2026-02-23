using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddAlertRuleThreshold : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ThresholdPercent",
                table: "AlertRules",
                type: "REAL",
                nullable: true);

            // Backfill default thresholds for seeded rules
            migrationBuilder.Sql(
                "UPDATE AlertRules SET ThresholdPercent = 15 WHERE EventTypePattern = 'audit.score_dropped'");
            migrationBuilder.Sql(
                "UPDATE AlertRules SET ThresholdPercent = 40 WHERE EventTypePattern = 'wan.speed_degradation'");
            migrationBuilder.Sql(
                "UPDATE AlertRules SET ThresholdPercent = 25 WHERE EventTypePattern = 'speedtest.regression'");

            // Fix label consistency
            migrationBuilder.Sql(
                "UPDATE AlertRules SET Name = REPLACE(Name, 'WiFi', 'Wi-Fi') WHERE Name LIKE '%WiFi%'");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThresholdPercent",
                table: "AlertRules");
        }
    }
}
