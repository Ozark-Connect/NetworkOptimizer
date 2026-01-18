using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiSiteSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Step 1: Create Sites table FIRST (before adding FK columns)
            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 1001, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                });

            // Step 2: Create default site with ID 1001 from existing data (if any exists)
            // Using 1001 as the known default site ID - new sites will be 1001+
            migrationBuilder.Sql(@"
                INSERT INTO Sites (Id, Name, DisplayName, Enabled, SortOrder, Notes, CreatedAt, UpdatedAt)
                SELECT 1001,
                    COALESCE(
                        CASE
                            WHEN ControllerUrl IS NOT NULL AND ControllerUrl != ''
                            THEN REPLACE(REPLACE(REPLACE(ControllerUrl, 'https://', ''), 'http://', ''), '/', '')
                            ELSE NULL
                        END,
                        'My Network'
                    ),
                    NULL,
                    1,
                    0,
                    'Default site migrated from single-site installation',
                    datetime('now'),
                    datetime('now')
                FROM UniFiConnectionSettings
                LIMIT 1;
            ");

            // If no UniFi settings exist but other data does, still create a default site
            migrationBuilder.Sql(@"
                INSERT INTO Sites (Id, Name, DisplayName, Enabled, SortOrder, Notes, CreatedAt, UpdatedAt)
                SELECT 1001, 'My Network', NULL, 1, 0, 'Default site migrated from single-site installation', datetime('now'), datetime('now')
                WHERE NOT EXISTS (SELECT 1 FROM Sites WHERE Id = 1001)
                AND (
                    EXISTS (SELECT 1 FROM AuditResults LIMIT 1)
                    OR EXISTS (SELECT 1 FROM Iperf3Results LIMIT 1)
                    OR EXISTS (SELECT 1 FROM SqmBaselines LIMIT 1)
                    OR EXISTS (SELECT 1 FROM DeviceSshConfigurations LIMIT 1)
                    OR EXISTS (SELECT 1 FROM UniFiSshSettings LIMIT 1)
                    OR EXISTS (SELECT 1 FROM GatewaySshSettings LIMIT 1)
                );
            ");

            // Seed the autoincrement sequence so new sites start at 1001
            migrationBuilder.Sql(@"
                INSERT OR REPLACE INTO sqlite_sequence (name, seq) VALUES ('Sites', 1001);
            ");

            // Step 3: Drop old indexes that will be recreated with SiteId
            migrationBuilder.DropIndex(
                name: "IX_UpnpNotes_HostIp_Port_Protocol",
                table: "UpnpNotes");

            migrationBuilder.DropIndex(
                name: "IX_SqmWanConfigurations_WanNumber",
                table: "SqmWanConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_SqmBaselines_DeviceId_InterfaceId",
                table: "SqmBaselines");

            migrationBuilder.DropIndex(
                name: "IX_Iperf3Results_DeviceHost_TestTime",
                table: "Iperf3Results");

            migrationBuilder.DropIndex(
                name: "IX_DismissedIssues_IssueKey",
                table: "DismissedIssues");

            migrationBuilder.DropIndex(
                name: "IX_AuditResults_DeviceId_AuditDate",
                table: "AuditResults");

            // Step 4: Rename Site column to UniFiSiteId in UniFiConnectionSettings
            migrationBuilder.RenameColumn(
                name: "Site",
                table: "UniFiConnectionSettings",
                newName: "UniFiSiteId");

            // Step 5: Add SiteId columns with default of 1 (referencing the default site we created)
            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "UpnpNotes",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE UpnpNotes SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "UniFiSshSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE UniFiSshSettings SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "UniFiConnectionSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE UniFiConnectionSettings SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "SqmWanConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE SqmWanConfigurations SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "SqmBaselines",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE SqmBaselines SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "ModemConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE ModemConfigurations SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "Iperf3Results",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE Iperf3Results SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "GatewaySshSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE GatewaySshSettings SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "DismissedIssues",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE DismissedIssues SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "DeviceSshConfigurations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE DeviceSshConfigurations SET SiteId = 1001 WHERE SiteId = 0;");

            migrationBuilder.AddColumn<int>(
                name: "SiteId",
                table: "AuditResults",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1001);
            migrationBuilder.Sql("UPDATE AuditResults SET SiteId = 1001 WHERE SiteId = 0;");

            // Step 6: Create indexes and foreign keys
            migrationBuilder.CreateIndex(
                name: "IX_UpnpNotes_SiteId",
                table: "UpnpNotes",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_UpnpNotes_SiteId_HostIp_Port_Protocol",
                table: "UpnpNotes",
                columns: new[] { "SiteId", "HostIp", "Port", "Protocol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UniFiSshSettings_SiteId",
                table: "UniFiSshSettings",
                column: "SiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UniFiConnectionSettings_SiteId",
                table: "UniFiConnectionSettings",
                column: "SiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqmWanConfigurations_SiteId",
                table: "SqmWanConfigurations",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_SqmWanConfigurations_SiteId_WanNumber",
                table: "SqmWanConfigurations",
                columns: new[] { "SiteId", "WanNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_SiteId",
                table: "SqmBaselines",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_SiteId_DeviceId_InterfaceId",
                table: "SqmBaselines",
                columns: new[] { "SiteId", "DeviceId", "InterfaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModemConfigurations_SiteId",
                table: "ModemConfigurations",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_SiteId",
                table: "Iperf3Results",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_SiteId_DeviceHost_TestTime",
                table: "Iperf3Results",
                columns: new[] { "SiteId", "DeviceHost", "TestTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_SiteId_TestTime",
                table: "Iperf3Results",
                columns: new[] { "SiteId", "TestTime" });

            migrationBuilder.CreateIndex(
                name: "IX_GatewaySshSettings_SiteId",
                table: "GatewaySshSettings",
                column: "SiteId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DismissedIssues_SiteId",
                table: "DismissedIssues",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_DismissedIssues_SiteId_IssueKey",
                table: "DismissedIssues",
                columns: new[] { "SiteId", "IssueKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSshConfigurations_SiteId",
                table: "DeviceSshConfigurations",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_SiteId",
                table: "AuditResults",
                column: "SiteId");

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_SiteId_AuditDate",
                table: "AuditResults",
                columns: new[] { "SiteId", "AuditDate" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_SiteId_DeviceId_AuditDate",
                table: "AuditResults",
                columns: new[] { "SiteId", "DeviceId", "AuditDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Enabled",
                table: "Sites",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_Name",
                table: "Sites",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Sites_SortOrder",
                table: "Sites",
                column: "SortOrder");

            migrationBuilder.AddForeignKey(
                name: "FK_AuditResults_Sites_SiteId",
                table: "AuditResults",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DeviceSshConfigurations_Sites_SiteId",
                table: "DeviceSshConfigurations",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_DismissedIssues_Sites_SiteId",
                table: "DismissedIssues",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_GatewaySshSettings_Sites_SiteId",
                table: "GatewaySshSettings",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Iperf3Results_Sites_SiteId",
                table: "Iperf3Results",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ModemConfigurations_Sites_SiteId",
                table: "ModemConfigurations",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SqmBaselines_Sites_SiteId",
                table: "SqmBaselines",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_SqmWanConfigurations_Sites_SiteId",
                table: "SqmWanConfigurations",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UniFiConnectionSettings_Sites_SiteId",
                table: "UniFiConnectionSettings",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UniFiSshSettings_Sites_SiteId",
                table: "UniFiSshSettings",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_UpnpNotes_Sites_SiteId",
                table: "UpnpNotes",
                column: "SiteId",
                principalTable: "Sites",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // Migrate audit settings from global keys to site-specific keys for default site
            migrationBuilder.Sql(@"
                UPDATE SystemSettings
                SET Key = 'site:1001:' || Key
                WHERE Key LIKE 'audit:%';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AuditResults_Sites_SiteId",
                table: "AuditResults");

            migrationBuilder.DropForeignKey(
                name: "FK_DeviceSshConfigurations_Sites_SiteId",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_DismissedIssues_Sites_SiteId",
                table: "DismissedIssues");

            migrationBuilder.DropForeignKey(
                name: "FK_GatewaySshSettings_Sites_SiteId",
                table: "GatewaySshSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_Iperf3Results_Sites_SiteId",
                table: "Iperf3Results");

            migrationBuilder.DropForeignKey(
                name: "FK_ModemConfigurations_Sites_SiteId",
                table: "ModemConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_SqmBaselines_Sites_SiteId",
                table: "SqmBaselines");

            migrationBuilder.DropForeignKey(
                name: "FK_SqmWanConfigurations_Sites_SiteId",
                table: "SqmWanConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_UniFiConnectionSettings_Sites_SiteId",
                table: "UniFiConnectionSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_UniFiSshSettings_Sites_SiteId",
                table: "UniFiSshSettings");

            migrationBuilder.DropForeignKey(
                name: "FK_UpnpNotes_Sites_SiteId",
                table: "UpnpNotes");

            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropIndex(
                name: "IX_UpnpNotes_SiteId",
                table: "UpnpNotes");

            migrationBuilder.DropIndex(
                name: "IX_UpnpNotes_SiteId_HostIp_Port_Protocol",
                table: "UpnpNotes");

            migrationBuilder.DropIndex(
                name: "IX_UniFiSshSettings_SiteId",
                table: "UniFiSshSettings");

            migrationBuilder.DropIndex(
                name: "IX_UniFiConnectionSettings_SiteId",
                table: "UniFiConnectionSettings");

            migrationBuilder.DropIndex(
                name: "IX_SqmWanConfigurations_SiteId",
                table: "SqmWanConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_SqmWanConfigurations_SiteId_WanNumber",
                table: "SqmWanConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_SqmBaselines_SiteId",
                table: "SqmBaselines");

            migrationBuilder.DropIndex(
                name: "IX_SqmBaselines_SiteId_DeviceId_InterfaceId",
                table: "SqmBaselines");

            migrationBuilder.DropIndex(
                name: "IX_ModemConfigurations_SiteId",
                table: "ModemConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_Iperf3Results_SiteId",
                table: "Iperf3Results");

            migrationBuilder.DropIndex(
                name: "IX_Iperf3Results_SiteId_DeviceHost_TestTime",
                table: "Iperf3Results");

            migrationBuilder.DropIndex(
                name: "IX_Iperf3Results_SiteId_TestTime",
                table: "Iperf3Results");

            migrationBuilder.DropIndex(
                name: "IX_GatewaySshSettings_SiteId",
                table: "GatewaySshSettings");

            migrationBuilder.DropIndex(
                name: "IX_DismissedIssues_SiteId",
                table: "DismissedIssues");

            migrationBuilder.DropIndex(
                name: "IX_DismissedIssues_SiteId_IssueKey",
                table: "DismissedIssues");

            migrationBuilder.DropIndex(
                name: "IX_DeviceSshConfigurations_SiteId",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_AuditResults_SiteId",
                table: "AuditResults");

            migrationBuilder.DropIndex(
                name: "IX_AuditResults_SiteId_AuditDate",
                table: "AuditResults");

            migrationBuilder.DropIndex(
                name: "IX_AuditResults_SiteId_DeviceId_AuditDate",
                table: "AuditResults");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "UpnpNotes");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "UniFiSshSettings");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "UniFiConnectionSettings");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "SqmWanConfigurations");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "SqmBaselines");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "ModemConfigurations");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "Iperf3Results");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "GatewaySshSettings");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "DismissedIssues");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "DeviceSshConfigurations");

            migrationBuilder.DropColumn(
                name: "SiteId",
                table: "AuditResults");

            migrationBuilder.RenameColumn(
                name: "UniFiSiteId",
                table: "UniFiConnectionSettings",
                newName: "Site");

            migrationBuilder.CreateIndex(
                name: "IX_UpnpNotes_HostIp_Port_Protocol",
                table: "UpnpNotes",
                columns: new[] { "HostIp", "Port", "Protocol" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqmWanConfigurations_WanNumber",
                table: "SqmWanConfigurations",
                column: "WanNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SqmBaselines_DeviceId_InterfaceId",
                table: "SqmBaselines",
                columns: new[] { "DeviceId", "InterfaceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Iperf3Results_DeviceHost_TestTime",
                table: "Iperf3Results",
                columns: new[] { "DeviceHost", "TestTime" });

            migrationBuilder.CreateIndex(
                name: "IX_DismissedIssues_IssueKey",
                table: "DismissedIssues",
                column: "IssueKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditResults_DeviceId_AuditDate",
                table: "AuditResults",
                columns: new[] { "DeviceId", "AuditDate" });
        }
    }
}
