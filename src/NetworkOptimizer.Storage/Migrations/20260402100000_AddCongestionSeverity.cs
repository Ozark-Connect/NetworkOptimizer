using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddCongestionSeverity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CongestionSeverity",
                table: "SqmWanConfigurations",
                type: "REAL",
                nullable: false,
                defaultValue: 1.0);

            migrationBuilder.AddColumn<double>(
                name: "LatencyThresholdMs",
                table: "SqmWanConfigurations",
                type: "REAL",
                nullable: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CongestionSeverity",
                table: "SqmWanConfigurations");

            migrationBuilder.DropColumn(
                name: "LatencyThresholdMs",
                table: "SqmWanConfigurations");
        }
    }
}
