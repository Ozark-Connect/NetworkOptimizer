using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanDataUsageTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanDataUsageConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DataCapGb = table.Column<double>(type: "REAL", nullable: false),
                    ManualAdjustmentGb = table.Column<double>(type: "REAL", nullable: false),
                    WarningThresholdPercent = table.Column<int>(type: "INTEGER", nullable: false),
                    BillingCycleDayOfMonth = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanDataUsageConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WanDataUsageSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TxBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    IsCounterReset = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBaseline = table.Column<bool>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanDataUsageSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WanDataUsageConfigs_WanKey",
                table: "WanDataUsageConfigs",
                column: "WanKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WanDataUsageSnapshots_WanKey_Timestamp",
                table: "WanDataUsageSnapshots",
                columns: new[] { "WanKey", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WanDataUsageSnapshots");
            migrationBuilder.DropTable(name: "WanDataUsageConfigs");
        }
    }
}
