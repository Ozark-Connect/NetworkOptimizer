using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddPerfTweakSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PerfTweakSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TweakId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsManuallyDeployed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PerfTweakSettings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PerfTweakSettings_TweakId",
                table: "PerfTweakSettings",
                column: "TweakId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "PerfTweakSettings");
        }
    }
}
