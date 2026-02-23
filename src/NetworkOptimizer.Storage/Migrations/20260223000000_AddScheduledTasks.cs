using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TaskType = table.Column<string>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    FrequencyMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    CustomMorningHour = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomMorningMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomEveningHour = table.Column<int>(type: "INTEGER", nullable: true),
                    CustomEveningMinute = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetId = table.Column<string>(type: "TEXT", nullable: true),
                    TargetConfig = table.Column<string>(type: "TEXT", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextRunAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastStatus = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastResultSummary = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_TaskType",
                table: "ScheduledTasks",
                column: "TaskType");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_Enabled",
                table: "ScheduledTasks",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_NextRunAt",
                table: "ScheduledTasks",
                column: "NextRunAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ScheduledTasks");
        }
    }
}
