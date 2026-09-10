using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddIgnoreSfpDdmSpikes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(name: "IgnoreSfpDdmSpikes", table: "MonitoringSettings", type: "INTEGER", nullable: false, defaultValue: false);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "IgnoreSfpDdmSpikes", table: "MonitoringSettings");
    }
}
