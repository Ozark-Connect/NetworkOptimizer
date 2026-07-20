using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <summary>
/// Adds OntConfigurations.AttachedSfpId: when set, the ONT config supplements the
/// monitored SFP module with that MonitoredSfp.Id - polled on the gateway SFP
/// collection cycle and merged into that module's sfp measurement instead of
/// being polled standalone. Nullable - existing rows stay standalone.
/// </summary>
public partial class AddOntAttachedSfpId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "AttachedSfpId",
            table: "OntConfigurations",
            type: "INTEGER",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "AttachedSfpId",
            table: "OntConfigurations");
    }
}
