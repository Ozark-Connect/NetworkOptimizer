using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations.Auth
{
    /// <summary>
    /// Per-user counts of teaching hints shown, so a hint that exists to reveal a non-obvious
    /// gesture can stop repeating once the user has plainly seen it. Per user rather than per site
    /// or per install: what someone has learned travels with them, and one operator learning a
    /// gesture says nothing about their colleagues.
    /// </summary>
    public partial class AddUserUiHints : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserUiHints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    HintKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    TimesShown = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserUiHints", x => x.Id);
                });

            // One row per user per hint - the upsert relies on it, and a duplicate would let a
            // hint count twice as slowly and outstay its welcome.
            migrationBuilder.CreateIndex(
                name: "IX_UserUiHints_UserId_HintKey",
                table: "UserUiHints",
                columns: new[] { "UserId", "HintKey" },
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UserUiHints");
        }
    }
}
