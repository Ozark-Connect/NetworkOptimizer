using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddCmAndOntTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CmConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 80),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    StatusPagePath = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 300),
                    LastPolled = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CmConfigurations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OntConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Host = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 80),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    PollingIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 300),
                    LastPolled = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OntConfigurations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CmConfigurations_Host",
                table: "CmConfigurations",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_CmConfigurations_Enabled",
                table: "CmConfigurations",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_OntConfigurations_Host",
                table: "OntConfigurations",
                column: "Host");

            migrationBuilder.CreateIndex(
                name: "IX_OntConfigurations_Enabled",
                table: "OntConfigurations",
                column: "Enabled");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "CmConfigurations");
            migrationBuilder.DropTable(name: "OntConfigurations");
        }
    }
}
