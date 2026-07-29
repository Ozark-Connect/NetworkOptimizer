using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSshKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SshKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    KeyType = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                    PrivateKeyProtected = table.Column<string>(type: "TEXT", nullable: false),
                    PassphraseProtected = table.Column<string>(type: "TEXT", nullable: true),
                    Fingerprint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SshKeys", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SshKeys");
        }
    }
}
