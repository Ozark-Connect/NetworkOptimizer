using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddOutcomeCenterAndNoiseFloor : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "CenterChannel",
            table: "ApChannelOutcomes",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<double>(
            name: "NoiseFloorSum",
            table: "ApChannelOutcomes",
            type: "REAL",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NoiseFloorSamples",
            table: "ApChannelOutcomes",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "CenterChannel", table: "ApChannelOutcomes");
        migrationBuilder.DropColumn(name: "NoiseFloorSum", table: "ApChannelOutcomes");
        migrationBuilder.DropColumn(name: "NoiseFloorSamples", table: "ApChannelOutcomes");
    }
}
