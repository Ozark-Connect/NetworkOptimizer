using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Records whether a target sits on the local network, so it stops being re-guessed from the
    /// text of its address on every read - and so a hostname can be answered at all.
    /// <para>
    /// Backfills the literal addresses here, since those need no lookup. Anything named by hostname
    /// stays null and is resolved later; null means "not known yet", never "not local".
    /// </para>
    /// </summary>
    public partial class AddMonitoringTargetIsLocal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLocal", table: "MonitoringTargets", type: "INTEGER", nullable: true);

            // Fabric is the discovered gateway, switches and APs - local by what it is, whatever
            // address it wears.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET IsLocal = 1 WHERE TargetType = 0;");

            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET IsLocal = 1
WHERE IsLocal IS NULL
  AND (Address GLOB '10.*'
    OR Address GLOB '192.168.*'
    OR Address GLOB '127.*'
    OR Address GLOB '172.1[6-9].*'
    OR Address GLOB '172.2[0-9].*'
    OR Address GLOB '172.3[01].*');");

            // A literal address that is not private is settled too - only names need looking up.
            migrationBuilder.Sql(@"
UPDATE MonitoringTargets SET IsLocal = 0
WHERE IsLocal IS NULL
  AND Address GLOB '*.*.*.*'
  AND Address NOT GLOB '*[a-zA-Z]*';");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "IsLocal", table: "MonitoringTargets");
        }
    }
}
