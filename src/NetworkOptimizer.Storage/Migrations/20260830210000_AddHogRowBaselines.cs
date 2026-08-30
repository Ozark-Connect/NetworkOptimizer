using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddHogRowBaselines : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HogRowBaselines",
            columns: table => new
            {
                RowKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                DownBps = table.Column<double>(type: "REAL", nullable: false),
                UpBps = table.Column<double>(type: "REAL", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HogRowBaselines", x => x.RowKey);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "HogRowBaselines");
    }
}
