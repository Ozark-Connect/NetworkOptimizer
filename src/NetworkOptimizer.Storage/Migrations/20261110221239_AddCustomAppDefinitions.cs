using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomAppDefinitions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CustomAppDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Protocol = table.Column<int>(type: "INTEGER", nullable: false),
                    Ports = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PortOn = table.Column<int>(type: "INTEGER", nullable: false),
                    Scope = table.Column<int>(type: "INTEGER", nullable: false),
                    Remote = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    IconClass = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomAppDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomAppDefinitions_SortOrder",
                table: "CustomAppDefinitions",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomAppDefinitions");
        }
    }
}
