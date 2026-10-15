using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddThreatBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ThreatBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Trigger = table.Column<int>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Strikes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enforced = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ThreatBlocks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ThreatBlocks_ExpiresAt",
                table: "ThreatBlocks",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_ThreatBlocks_SourceIp_CreatedAt",
                table: "ThreatBlocks",
                columns: new[] { "SourceIp", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ThreatBlocks");
        }
    }
}
