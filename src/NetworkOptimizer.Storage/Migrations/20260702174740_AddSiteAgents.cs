using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSiteAgents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SiteAgents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SiteId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    EnrollmentTokenHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TokenCreatedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AgentKeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    EnrolledAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteAgents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SiteAgents_AgentKeyHash",
                table: "SiteAgents",
                column: "AgentKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_SiteAgents_EnrollmentTokenHash",
                table: "SiteAgents",
                column: "EnrollmentTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_SiteAgents_SiteId",
                table: "SiteAgents",
                column: "SiteId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SiteAgents");
        }
    }
}
