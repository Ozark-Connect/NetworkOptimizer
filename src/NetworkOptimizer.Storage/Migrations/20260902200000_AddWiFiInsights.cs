using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddWiFiInsights : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "WiFiIssueAcknowledgments",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                IssueKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                AcknowledgedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WiFiIssueAcknowledgments", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "WiFiRadioPreferences",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ApMac = table.Column<string>(type: "TEXT", maxLength: 17, nullable: false),
                Band = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                KeepChannelSince = table.Column<DateTime>(type: "TEXT", nullable: true),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_WiFiRadioPreferences", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_WiFiIssueAcknowledgments_IssueKey",
            table: "WiFiIssueAcknowledgments",
            column: "IssueKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_WiFiRadioPreferences_ApMac_Band",
            table: "WiFiRadioPreferences",
            columns: new[] { "ApMac", "Band" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "WiFiIssueAcknowledgments");

        migrationBuilder.DropTable(
            name: "WiFiRadioPreferences");
    }
}
