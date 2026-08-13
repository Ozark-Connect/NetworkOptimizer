using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoringTargetRetirement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "RetiredAt",
                table: "MonitoringTargets",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetiredReason",
                table: "MonitoringTargets",
                type: "TEXT",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetiredAt",
                table: "MonitoringTargets");

            migrationBuilder.DropColumn(
                name: "RetiredReason",
                table: "MonitoringTargets");
        }
    }
}
