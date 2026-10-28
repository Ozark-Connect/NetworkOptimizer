using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddWanContextReassignedFrom : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ReassignedFromAgentId",
                table: "WanContexts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReassignedFromInterfaceName",
                table: "WanContexts",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReassignedFromProbeSourceIp",
                table: "WanContexts",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "ReassignedFromAgentId", table: "WanContexts");
            migrationBuilder.DropColumn(name: "ReassignedFromInterfaceName", table: "WanContexts");
            migrationBuilder.DropColumn(name: "ReassignedFromProbeSourceIp", table: "WanContexts");
        }
    }
}
