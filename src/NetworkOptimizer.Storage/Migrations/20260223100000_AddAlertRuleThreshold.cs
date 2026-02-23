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
            // Note: ThresholdPercent defaults are set in DefaultAlertRules.cs for new installs.
            // No backfill needed here since the seeder handles it.
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ThresholdPercent",
                table: "AlertRules");
        }
    }
}
