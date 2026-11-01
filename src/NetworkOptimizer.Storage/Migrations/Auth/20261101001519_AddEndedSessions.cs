using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations.Auth
{
    /// <summary>
    /// Signed-out logins, so a copy of a signed-out cookie stops working. The cookie is a self-contained
    /// ticket: signing out only deleted the browser's copy, and any other copy kept authenticating for
    /// the rest of its 14-day sliding lifetime. Rows expire with the longest a cookie can live.
    /// </summary>
    public partial class AddEndedSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EndedSessions",
                columns: table => new
                {
                    LoginId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    EndedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EndedSessions", x => x.LoginId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EndedSessions_ExpiresAt",
                table: "EndedSessions",
                column: "ExpiresAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EndedSessions");
        }
    }
}
