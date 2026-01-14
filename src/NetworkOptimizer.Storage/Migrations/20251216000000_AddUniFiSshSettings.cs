using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddUniFiSshSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Create UniFiSshSettings table
            migrationBuilder.CreateTable(
                name: "UniFiSshSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Port = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 22),
                    Username = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Password = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    PrivateKeyPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    LastTestedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastTestResult = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UniFiSshSettings", x => x.Id);
                });

            // Note: Iperf3Results table is created in AddModemAndSpeedTables migration
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UniFiSshSettings");
            // Note: Iperf3Results table is dropped in AddModemAndSpeedTables migration
        }
    }
}
