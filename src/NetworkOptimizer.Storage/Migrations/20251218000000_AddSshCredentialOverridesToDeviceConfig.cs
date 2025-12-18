using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSshCredentialOverridesToDeviceConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SshUsername",
                table: "DeviceSshConfigurations",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshPassword",
                table: "DeviceSshConfigurations",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SshPrivateKeyPath",
                table: "DeviceSshConfigurations",
                type: "TEXT",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SshUsername",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropColumn(
                name: "SshPassword",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropColumn(
                name: "SshPrivateKeyPath",
                table: "DeviceSshConfigurations");
        }
    }
}
