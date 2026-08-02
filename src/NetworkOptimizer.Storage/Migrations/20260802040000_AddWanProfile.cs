using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanNetworkgroup = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Interface = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DownloadMbps = table.Column<double>(type: "REAL", nullable: true),
                    UploadMbps = table.Column<double>(type: "REAL", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WanProfiles_WanNetworkgroup",
                table: "WanProfiles",
                column: "WanNetworkgroup",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WanProfiles");
        }
    }
}
