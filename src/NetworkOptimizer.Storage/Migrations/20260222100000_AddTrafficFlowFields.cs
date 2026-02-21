using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddTrafficFlowFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EventSource",
                table: "ThreatEvents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Domain",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Service",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BytesTotal",
                table: "ThreatEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FlowDurationMs",
                table: "ThreatEvents",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NetworkName",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RiskLevel",
                table: "ThreatEvents",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ThreatEvents_EventSource",
                table: "ThreatEvents",
                column: "EventSource");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ThreatEvents_EventSource",
                table: "ThreatEvents");

            migrationBuilder.DropColumn(name: "EventSource", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "Domain", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "Direction", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "Service", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "BytesTotal", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "FlowDurationMs", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "NetworkName", table: "ThreatEvents");
            migrationBuilder.DropColumn(name: "RiskLevel", table: "ThreatEvents");
        }
    }
}
