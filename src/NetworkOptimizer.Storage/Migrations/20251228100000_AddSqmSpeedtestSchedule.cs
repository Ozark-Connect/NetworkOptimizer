using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddSqmSpeedtestSchedule : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SpeedtestMorningHour",
                table: "SqmWanConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.AddColumn<int>(
                name: "SpeedtestMorningMinute",
                table: "SqmWanConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "SpeedtestEveningHour",
                table: "SqmWanConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 18);

            migrationBuilder.AddColumn<int>(
                name: "SpeedtestEveningMinute",
                table: "SqmWanConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SpeedtestMorningHour",
                table: "SqmWanConfigurations");

            migrationBuilder.DropColumn(
                name: "SpeedtestMorningMinute",
                table: "SqmWanConfigurations");

            migrationBuilder.DropColumn(
                name: "SpeedtestEveningHour",
                table: "SqmWanConfigurations");

            migrationBuilder.DropColumn(
                name: "SpeedtestEveningMinute",
                table: "SqmWanConfigurations");
        }
    }
}
