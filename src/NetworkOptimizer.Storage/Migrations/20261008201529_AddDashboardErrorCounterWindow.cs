using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDashboardErrorCounterWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CmErrorCountersLast24h",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "OntErrorCountersLast24h",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CmErrorCountersLast24h",
                table: "MonitoringSettings");

            migrationBuilder.DropColumn(
                name: "OntErrorCountersLast24h",
                table: "MonitoringSettings");
        }
    }
}
