using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations;

/// <inheritdoc />
public partial class AddHealthCheckDefinitions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "HealthCheckDefinitions",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                FieldName = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                TemplateId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                IntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                Command = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: false),
                TimeoutSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                Parser = table.Column<int>(type: "INTEGER", nullable: false),
                ParserArg = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                Operator = table.Column<int>(type: "INTEGER", nullable: false),
                Threshold = table.Column<double>(type: "REAL", nullable: false),
                ConsecutiveSamples = table.Column<int>(type: "INTEGER", nullable: false),
                NotApplicablePattern = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                AlertEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AlertSeverity = table.Column<int>(type: "INTEGER", nullable: false),
                Remedy = table.Column<int>(type: "INTEGER", nullable: false),
                RemedyArg = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                RemedyCooldownSeconds = table.Column<int>(type: "INTEGER", nullable: false),
                RemedyMaxPerDay = table.Column<int>(type: "INTEGER", nullable: false),
                Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_HealthCheckDefinitions", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_HealthCheckDefinitions_DeviceMac",
            table: "HealthCheckDefinitions",
            column: "DeviceMac");

        migrationBuilder.CreateIndex(
            name: "IX_HealthCheckDefinitions_DeviceMac_FieldName",
            table: "HealthCheckDefinitions",
            columns: new[] { "DeviceMac", "FieldName" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "HealthCheckDefinitions");
    }
}
