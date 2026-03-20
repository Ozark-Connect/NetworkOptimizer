using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanSteerTrafficClasses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WanSteerTrafficClasses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetWanKey = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Probability = table.Column<double>(type: "REAL", nullable: false),
                    SrcCidrsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SrcMacsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DstCidrsJson = table.Column<string>(type: "TEXT", nullable: true),
                    Protocol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    SrcPortsJson = table.Column<string>(type: "TEXT", nullable: true),
                    DstPortsJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WanSteerTrafficClasses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WanSteerTrafficClasses_SortOrder",
                table: "WanSteerTrafficClasses",
                column: "SortOrder");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "WanSteerTrafficClasses");
        }
    }
}
