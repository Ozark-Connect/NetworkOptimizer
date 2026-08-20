using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAutopilotSettingsSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Additive only. Existing Autopilot sites seed themselves from the live row on the
            // first autopilot tick, which serializes with the same model that reads it.
            migrationBuilder.AddColumn<string>(
                name: "AutopilotSettingsJson",
                table: "FirmwareRolloutSettings",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutopilotSettingsJson",
                table: "FirmwareRolloutSettings");
        }
    }
}
