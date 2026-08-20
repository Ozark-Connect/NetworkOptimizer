using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddFirmwareRolloutConsoleChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NetworkAppChannel",
                table: "FirmwareRolloutSettings",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UniFiOsChannel",
                table: "FirmwareRolloutSettings",
                type: "TEXT",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NetworkAppChannel",
                table: "FirmwareRolloutSettings");

            migrationBuilder.DropColumn(
                name: "UniFiOsChannel",
                table: "FirmwareRolloutSettings");
        }
    }
}
