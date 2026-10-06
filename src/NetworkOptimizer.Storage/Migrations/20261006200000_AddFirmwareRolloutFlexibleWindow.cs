using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddFirmwareRolloutFlexibleWindow : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "FixedWindowFlexible",
            table: "FirmwareRolloutSettings",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "FixedWindowFlexible", table: "FirmwareRolloutSettings");
    }
}
