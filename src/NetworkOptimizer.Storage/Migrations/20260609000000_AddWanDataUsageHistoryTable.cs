using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddWanDataUsageHistoryTable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanDataUsageHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CycleStart = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CycleEnd = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UsedGb = table.Column<double>(type: "REAL", nullable: false),
                    CapGb = table.Column<double>(type: "REAL", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanDataUsageHistory", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WanDataUsageHistory_WanKey_CycleStart",
                table: "WanDataUsageHistory",
                columns: new[] { "WanKey", "CycleStart" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WanDataUsageHistory");
        }
    }
}
