using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    /// <summary>
    /// Moves saved UniFi Network JVM GC Thrash checks to the template's fixed command, which drops
    /// the previous JVM's samples when gc.log's uptime resets after a unifi restart.
    ///
    /// A saved check holds its own copy of the template's command, so a template change never
    /// reaches it. Only rows whose command is still the exact v2.9.0 template are rewritten; a check
    /// the user edited is left alone.
    /// </summary>
    public partial class FixJvmGcCheckAcrossRestarts : Migration
    {
        private const string OldCommand = @"[ -r /data/unifi/logs/gc.log ] || { echo NA; exit 0; }; tail -n 600 /data/unifi/logs/gc.log | awk 'BEGIN { n = 0 } /^\[/ { t = substr($1, 2) + 0; if (t > last) last = t } /Pause Full/ { ts[n] = t; ps[n] = $NF + 0; n++ } END { w = 60; s = 0; c = 0; for (i = 0; i < n; i++) if (ts[i] >= last - w) { s += ps[i]; c++ }; printf ""%.1f %d\n"", s / (w * 10), c }'";

        private const string NewCommand = @"[ -r /data/unifi/logs/gc.log ] || { echo NA; exit 0; }; tail -n 600 /data/unifi/logs/gc.log | awk 'BEGIN { n = 0 } /^\[/ { t = substr($1, 2) + 0; if (t < last) n = 0; last = t } /Pause Full/ { ts[n] = t; ps[n] = $NF + 0; n++ } END { w = 60; s = 0; c = 0; for (i = 0; i < n; i++) if (ts[i] >= last - w) { s += ps[i]; c++ }; printf ""%.1f %d\n"", s / (w * 10), c }'";

        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql($@"
                UPDATE HealthCheckDefinitions
                SET Command = {Quote(NewCommand)}
                WHERE TemplateId = 'unifi-network-jvm-gc'
                  AND Command = {Quote(OldCommand)};");
        }

        /// <summary>
        /// Not reversed: the old command is the bug, and nothing marks which rows this rewrote.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }

        private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
    }
}
