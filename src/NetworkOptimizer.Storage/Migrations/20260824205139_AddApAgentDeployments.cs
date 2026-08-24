using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddApAgentDeployments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApAgentDeployments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    DeviceName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Architecture = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    DeployedVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    DeployedBinaryVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    LastDeployedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastHealthyAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApAgentDeployments", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApAgentDeployments_DeviceMac",
                table: "ApAgentDeployments",
                column: "DeviceMac",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ApAgentDeployments_Enabled",
                table: "ApAgentDeployments",
                column: "Enabled");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApAgentDeployments");
        }
    }
}
