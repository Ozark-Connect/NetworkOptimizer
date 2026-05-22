using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Add the remaining monitoring subsystem tables: MonitoringTargets, InterfaceNameMaps,
    /// UpstreamDiscoveries, MonitoredSfps, OuiVendors. Also adds access-tech and InfluxDB
    /// health columns to MonitoringSettings. See research/gate1-schema-proposal.md.
    /// </summary>
    public partial class AddMonitoringTables : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add new columns to MonitoringSettings
            migrationBuilder.AddColumn<int>(
                name: "AccessTechnology",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "WanNeighborMac",
                table: "MonitoringSettings",
                type: "TEXT",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WanNeighborOui",
                table: "MonitoringSettings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "InfluxDbReachable",
                table: "MonitoringSettings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastInfluxDbCheck",
                table: "MonitoringSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastInfluxDbError",
                table: "MonitoringSettings",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            // MonitoringTargets
            migrationBuilder.CreateTable(
                name: "MonitoringTargets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TargetId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ProbeMode = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Port = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetType = table.Column<int>(type: "INTEGER", nullable: false),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    AsnNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    AsnName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    VantagePoint = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, defaultValue: "server"),
                    PollIntervalSeconds = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                    PingCount = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 10),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                    AutoDiscovered = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    DiscoveryMethod = table.Column<int>(type: "INTEGER", nullable: true),
                    PtrHostname = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    AutoLabel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LastVerified = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoringTargets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_TargetId",
                table: "MonitoringTargets",
                column: "TargetId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_TargetType",
                table: "MonitoringTargets",
                column: "TargetType");

            migrationBuilder.CreateIndex(
                name: "IX_MonitoringTargets_Enabled",
                table: "MonitoringTargets",
                column: "Enabled");

            // InterfaceNameMaps
            migrationBuilder.CreateTable(
                name: "InterfaceNameMaps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IfName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    PortNumber = table.Column<int>(type: "INTEGER", nullable: true),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    IsWan = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    WanName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IfIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    IfAlias = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SpeedMbps = table.Column<int>(type: "INTEGER", nullable: true),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterfaceNameMaps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InterfaceNameMaps_DeviceMac_IfName",
                table: "InterfaceNameMaps",
                columns: new[] { "DeviceMac", "IfName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InterfaceNameMaps_DeviceMac",
                table: "InterfaceNameMaps",
                column: "DeviceMac");

            // UpstreamDiscoveries
            migrationBuilder.CreateTable(
                name: "UpstreamDiscoveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MonitoringTargetId = table.Column<int>(type: "INTEGER", nullable: true),
                    AsnNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    AsnName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    HopIp = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    HopNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    WanInterface = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    LastValidated = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastTracerouteAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UpstreamDiscoveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UpstreamDiscoveries_AsnNumber",
                table: "UpstreamDiscoveries",
                column: "AsnNumber");

            migrationBuilder.CreateIndex(
                name: "IX_UpstreamDiscoveries_IsActive",
                table: "UpstreamDiscoveries",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_UpstreamDiscoveries_MonitoringTargetId",
                table: "UpstreamDiscoveries",
                column: "MonitoringTargetId");

            // MonitoredSfps
            migrationBuilder.CreateTable(
                name: "MonitoredSfps",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DeviceMac = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    PortName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SfpPart = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    SfpVendor = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    IsGpon = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    IsMonitoredOnt = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    FriendlyName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MonitoredSfps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredSfps_DeviceMac_PortName",
                table: "MonitoredSfps",
                columns: new[] { "DeviceMac", "PortName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MonitoredSfps_IsMonitoredOnt",
                table: "MonitoredSfps",
                column: "IsMonitoredOnt");

            // OuiVendors
            migrationBuilder.CreateTable(
                name: "OuiVendors",
                columns: table => new
                {
                    OuiPrefix = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    VendorName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    LastUpdated = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OuiVendors", x => x.OuiPrefix);
                });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MonitoringTargets");
            migrationBuilder.DropTable(name: "InterfaceNameMaps");
            migrationBuilder.DropTable(name: "UpstreamDiscoveries");
            migrationBuilder.DropTable(name: "MonitoredSfps");
            migrationBuilder.DropTable(name: "OuiVendors");

            migrationBuilder.DropColumn(name: "AccessTechnology", table: "MonitoringSettings");
            migrationBuilder.DropColumn(name: "WanNeighborMac", table: "MonitoringSettings");
            migrationBuilder.DropColumn(name: "WanNeighborOui", table: "MonitoringSettings");
            migrationBuilder.DropColumn(name: "InfluxDbReachable", table: "MonitoringSettings");
            migrationBuilder.DropColumn(name: "LastInfluxDbCheck", table: "MonitoringSettings");
            migrationBuilder.DropColumn(name: "LastInfluxDbError", table: "MonitoringSettings");
        }
    }
}
