using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDataUsageResetMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastResetAt",
                table: "WanDataUsageConfigs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResetMode",
                table: "WanDataUsageConfigs",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastResetAt",
                table: "WanDataUsageConfigs");

            migrationBuilder.DropColumn(
                name: "ResetMode",
                table: "WanDataUsageConfigs");
        }
    }
}
