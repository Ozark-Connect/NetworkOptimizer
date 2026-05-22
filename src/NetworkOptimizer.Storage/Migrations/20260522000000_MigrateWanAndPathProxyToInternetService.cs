using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class MigrateWanAndPathProxyToInternetService : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Wan (1) → InternetService (5)
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET TargetType = 5 WHERE TargetType = 1");

            // Transit (3) with PathProxy discovery → InternetService (5)
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET TargetType = 5 WHERE TargetType = 3 AND DiscoveryMethod = 1");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // InternetService (5) rows that were originally Wan
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET TargetType = 1 WHERE TargetType = 5 AND DiscoveryMethod IS NULL");

            // InternetService (5) rows that were PathProxy Transit
            migrationBuilder.Sql(
                "UPDATE MonitoringTargets SET TargetType = 3 WHERE TargetType = 5 AND DiscoveryMethod = 1");
        }
    }
}
