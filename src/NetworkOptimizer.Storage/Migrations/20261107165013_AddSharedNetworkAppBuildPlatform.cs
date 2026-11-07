using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSharedNetworkAppBuildPlatform : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_SharedNetworkAppBuilds",
                table: "SharedNetworkAppBuilds");

            migrationBuilder.AddColumn<string>(
                name: "Platform",
                table: "SharedNetworkAppBuilds",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SharedNetworkAppBuilds",
                table: "SharedNetworkAppBuilds",
                columns: new[] { "Platform", "Channel", "Version" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // One build offered on several platforms would collide on the old (Channel, Version) key.
            migrationBuilder.Sql("DELETE FROM SharedNetworkAppBuilds WHERE Platform <> '';");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SharedNetworkAppBuilds",
                table: "SharedNetworkAppBuilds");

            migrationBuilder.DropColumn(
                name: "Platform",
                table: "SharedNetworkAppBuilds");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SharedNetworkAppBuilds",
                table: "SharedNetworkAppBuilds",
                columns: new[] { "Channel", "Version" });
        }
    }
}
