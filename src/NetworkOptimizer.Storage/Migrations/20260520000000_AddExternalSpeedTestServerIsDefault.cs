using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddExternalSpeedTestServerIsDefault : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDefault",
                table: "ExternalSpeedTestServers",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Promote the first existing server (if any) to default
            migrationBuilder.Sql(
                "UPDATE ExternalSpeedTestServers SET IsDefault = 1 WHERE Id = (SELECT Id FROM ExternalSpeedTestServers ORDER BY Id LIMIT 1);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDefault",
                table: "ExternalSpeedTestServers");
        }
    }
}
