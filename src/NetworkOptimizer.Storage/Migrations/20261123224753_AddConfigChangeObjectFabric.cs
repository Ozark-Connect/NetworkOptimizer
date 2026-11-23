using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddConfigChangeObjectFabric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Fabric",
                table: "ConfigChangeObjects",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Fabric",
                table: "ConfigChangeObjects");
        }
    }
}
