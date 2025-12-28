using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSqmWanConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SqmWanConfigurations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WanNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    ConnectionType = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Interface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    NominalDownloadMbps = table.Column<int>(type: "INTEGER", nullable: false),
                    NominalUploadMbps = table.Column<int>(type: "INTEGER", nullable: false),
                    PingHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    SpeedtestServerId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SqmWanConfigurations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SqmWanConfigurations_WanNumber",
                table: "SqmWanConfigurations",
                column: "WanNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SqmWanConfigurations");
        }
    }
}
