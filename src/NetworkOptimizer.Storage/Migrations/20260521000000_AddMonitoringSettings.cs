using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    public partial class AddMonitoringSettings : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MonitoringSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    InfluxDbUrl = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false, defaultValue: "http://localhost:8086"),
                    InfluxDbToken = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    InfluxDbOrg = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "network-optimizer"),
                    InfluxDbBucket = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "network_monitoring"),
                    InfluxDbLongtermBucket = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "network_monitoring_longterm"),
                    FastPollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 5),
                    MediumPollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 30),
                    SlowPollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 300),
                    SnmpVersion = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 1),
                    SnmpCommunity = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SnmpV3Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SnmpV3AuthPassword = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SnmpDetectionState = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    LastSnmpDetection = table.Column<string>(type: "TEXT", nullable: true),
                    LastSnmpSuccess = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringSettings", x => x.Id);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MonitoringSettings");
        }
    }
}
