using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Records which default alert rule patterns have already been seeded into this database, so
    /// startup seeds each pattern at most once. Seeding previously inserted any default whose
    /// pattern was missing from AlertRules, which brought deleted rules back on every restart.
    /// </summary>
    public partial class AddSeededAlertRules : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeededAlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EventTypePattern = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    SeededAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeededAlertRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeededAlertRules_EventTypePattern",
                table: "SeededAlertRules",
                column: "EventTypePattern",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeededAlertRules");
        }
    }
}
