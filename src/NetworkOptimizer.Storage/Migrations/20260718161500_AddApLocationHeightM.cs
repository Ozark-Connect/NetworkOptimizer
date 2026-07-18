using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <summary>
/// Adds ApLocations.HeightM: precise height in metres above the assigned floor's
/// base elevation, written by 3D map repositioning. Nullable - existing rows keep
/// deriving height from MountType / device kind.
/// </summary>
public partial class AddApLocationHeightM : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<double>(
            name: "HeightM",
            table: "ApLocations",
            type: "REAL",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "HeightM",
            table: "ApLocations");
    }
}
