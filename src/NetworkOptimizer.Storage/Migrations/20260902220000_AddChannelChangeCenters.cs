using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddChannelChangeCenters : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "PreviousCenterChannel",
            table: "ApChannelChanges",
            type: "INTEGER",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "NewCenterChannel",
            table: "ApChannelChanges",
            type: "INTEGER",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "PreviousCenterChannel", table: "ApChannelChanges");
        migrationBuilder.DropColumn(name: "NewCenterChannel", table: "ApChannelChanges");
    }
}
