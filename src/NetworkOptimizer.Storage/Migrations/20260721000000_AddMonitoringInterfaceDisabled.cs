using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <summary>
/// Adds MonitoringInterfaces.Disabled: true when the user disabled (un-deployed) an
/// interface but kept its config for later re-enable. Defaults to false so existing
/// rows stay deployed.
/// </summary>
public partial class AddMonitoringInterfaceDisabled : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<bool>(
            name: "Disabled",
            table: "MonitoringInterfaces",
            type: "INTEGER",
            nullable: false,
            defaultValue: false);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Disabled",
            table: "MonitoringInterfaces");
    }
}
