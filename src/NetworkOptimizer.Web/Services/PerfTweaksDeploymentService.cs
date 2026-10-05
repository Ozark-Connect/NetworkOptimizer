using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

public class PerfTweaksDeploymentService : IPerfTweaksDeploymentService
{
    private readonly ILogger<PerfTweaksDeploymentService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly ISqmDeploymentService _sqmDeployment;

    private const string OnBootDir = "/data/on_boot.d";
    private const string PerfTweaksDir = "/data/perf-tweaks";
    private const string SfpModuleDir = "/data/sfp-sgmiiplus";
    // Highest UniFi OS version the perf tweaks + SGMII+ module are verified against, one
    // ceiling per gateway line because the UXG and UCG lines receive UniFi OS 6 releases on
    // different schedules (6.0.5 shipped for the UXG-Fiber only; 6.0.7 for the UCG-Fiber).
    // 6.0.11 static-verified on the UCG-Fiber image: a kernel rebuild with no code change,
    // qca-ssdk.ko byte-identical to 6.0.10 so the SGMII+ contract holds, and no tweak dependency
    // changed (unifi-perf-tweaks docs/compat-6.0.11.md).
    private static readonly Version MaxSupportedFirmware = new(6, 0, 11);
    // 6.0.10 live-verified on a production UXG-Fiber: SGMII+ loaded at 2500Mb/s, journald and fan
    // tweaks in effect, and its qca-ssdk.ko and qca-nss-dp.ko are byte-identical to the UCG-Fiber
    // 6.0.10 image, so that image's static check applies (unifi-perf-tweaks docs/compat-6.0.10.md).
    private static readonly Version MaxSupportedFirmwareUxg = new(6, 0, 10);

    /// <summary>
    /// The verified firmware ceiling for a gateway, read off the same lowercased shortname the
    /// supported-gateway check uses.
    /// </summary>
    private static Version MaxSupportedFirmwareFor(string modelLower) =>
        modelLower is "uxg-fiber" or "uxgfiber" or "uxg-max" or "uxgmax" or "uxgb"
            ? MaxSupportedFirmwareUxg
            : MaxSupportedFirmware;

    private static readonly Dictionary<string, string> BootScriptFiles = new()
    {
        ["fan-control"] = "15-fan-control-tuning.sh",
        ["mongodb-ssd"] = "06-mongodb-ssd-offload.sh",
        ["mongodb-backup"] = "07-mongodb-ssd-backup.sh",
        ["postgresql-ssd"] = "08-postgresql-ssd-offload.sh",
        ["postgresql-backup"] = "09-postgresql-ssd-backup.sh",
        ["journald-volatile"] = "10-journald-volatile.sh",
        ["sfp-sgmiiplus-port6"] = "19-sfp-sgmiiplus-eth5.sh",
        ["sfp-sgmiiplus"] = "20-sfp-sgmiiplus.sh"
    };

    // Additional boot scripts a tweak deploys beyond its primary, for out-of-date
    // detection. mongodb-ssd deploys 07 as a backup companion (see DeployTweakAsync),
    // so a 07-only change must also flag the tweak as outdated.
    private static readonly Dictionary<string, string[]> CompanionScripts = new()
    {
        ["mongodb-ssd"] = new[] { "07-mongodb-ssd-backup.sh" },
        ["postgresql-ssd"] = new[] { "09-postgresql-ssd-backup.sh" }
    };

    // Not a boot hook: retires 06/07 and puts the newest MongoDB copy back on the eMMC.
    // Runs from PerfTweaksDir as the first step of the postgresql-ssd deploy and as the
    // mongodb-ssd Remove. Its last output line is a machine-readable RESULT= line.
    private const string DecommissionScript = "mongodb-ssd-decommission.sh";
    private const string PgRevertScript = "postgresql-ssd-revert.sh";
    private const string PgSsdDirName = "postgresql-14-apps";

    /// <summary>
    /// PerfTweakSetting row recording that this app decommissioned the MongoDB SSD tweak, so a
    /// later downgrade to a MongoDB build can warn that the MongoDB data is from the migration point.
    /// </summary>
    internal const string MongoDecommissionedSettingId = "mongodb-ssd-decommissioned";

    // Copies PGDATA back from the SSD (docs/postgresql-ssd-offload.md#reverting on the script repo),
    // then retires 08 and 09. The SSD copy, the marker dir and the backups stay. Any failure after
    // the stop leaves the services stopped for investigation, as the doc requires.
    private const string PgRevertScriptContent = """
        #!/bin/bash
        set -euo pipefail
        PG_DATA=/data/postgresql/14/apps/data
        STATE_DIR=/data/unifi-pg-ssd
        MARKER="$STATE_DIR/14-apps.ssd-active"
        ARCHIVE="/data/on_boot.d.disabled/postgresql-$(date +%Y%m%d%H%M%S)-$$"
        retire_hooks() {
            if [ -f /data/on_boot.d/08-postgresql-ssd-offload.sh ]; then
                mkdir -p "$ARCHIVE"
                mv /data/on_boot.d/08-postgresql-ssd-offload.sh "$ARCHIVE/"
            fi
            rm -f /data/on_boot.d/09-postgresql-ssd-backup.sh /etc/cron.d/postgresql-ssd-backup
        }
        # 08 never migrated (no marker, no bind): PostgreSQL already runs on the eMMC.
        if [ ! -f "$MARKER" ] && ! mountpoint -q "$PG_DATA"; then
            retire_hooks
            echo 'Not offloaded; boot hooks retired.'
            echo 'removed'
            exit 0
        fi
        SSD_PG_DIR=$(awk '{print $2}' "$MARKER")
        test -n "$SSD_PG_DIR"
        test -f "$SSD_PG_DIR/global/pg_control"
        test "$(cat "$SSD_PG_DIR/PG_VERSION")" = 14
        exec 9>/run/postgresql-ssd-offload.lock
        flock -n 9
        systemctl stop unifi.service
        systemctl stop postgresql@14-apps.service
        test ! -e "$PG_DATA/postmaster.pid"
        case "$(systemctl is-active postgresql@14-apps.service)" in
            inactive|failed) ;;
            *) echo "Cluster not stopped; aborting" >&2; exit 1 ;;
        esac
        if mountpoint -q "$PG_DATA"; then
            test "$(stat -c '%d:%i' "$PG_DATA")" = "$(stat -c '%d:%i' "$SSD_PG_DIR")"
            umount "$PG_DATA"
        fi
        mkdir -p "$ARCHIVE"
        if [ -f /data/on_boot.d/08-postgresql-ssd-offload.sh ]; then
            mv /data/on_boot.d/08-postgresql-ssd-offload.sh "$ARCHIVE/"
        fi
        NEW_DATA=$(mktemp -d "$PG_DATA.revert.XXXXXX")
        cp -a "$SSD_PG_DIR"/. "$NEW_DATA"/
        chown postgres:postgres "$NEW_DATA"
        chmod 0700 "$NEW_DATA"
        sync -f "$NEW_DATA"
        OLD_DATA="$PG_DATA.pre-revert-$(date +%Y%m%d%H%M%S)-$$"
        test ! -e "$OLD_DATA"
        mv -T "$PG_DATA" "$OLD_DATA"
        mv -T "$NEW_DATA" "$PG_DATA"
        sync -f "$PG_DATA"
        rm -f "$MARKER"
        sync -f "$STATE_DIR"
        systemctl start postgresql@14-apps.service
        runuser -u postgres -- psql -X -w -v ON_ERROR_STOP=1 -h /var/run/postgresql -p 5434 \
            -d unifi-network -Atc 'SELECT 1;'
        systemctl start unifi.service
        systemctl is-active --quiet postgresql@14-apps.service
        systemctl is-active --quiet unifi.service
        retire_hooks
        printf 'Reverted; previous eMMC data retained at %s.\n' "$OLD_DATA"
        echo 'removed'
        """;

    private static readonly Lazy<Dictionary<string, string>> ExpectedHashes = new(() =>
    {
        var hashes = new Dictionary<string, string>();
        foreach (var (tweakId, fileName) in BootScriptFiles)
        {
            var content = ReadEmbeddedResource(fileName);
            if (content != null)
            {
                var normalized = content.Replace("\r\n", "\n");
                var hash = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(normalized)));
                hashes[fileName] = hash;
            }
        }
        return hashes;
    });

    public PerfTweaksDeploymentService(
        ILogger<PerfTweaksDeploymentService> logger,
        IGatewaySshService gatewaySsh,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        SiteContextService siteContext,
        ISqmDeploymentService sqmDeployment,
        Licensing.LicenseStateService licenseState,
        Firmware.RolloutSuppressionRegistry suppression)
    {
        _suppression = suppression;
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _sqmDeployment = sqmDeployment;
        _licenseState = licenseState;
    }

    private readonly Licensing.LicenseStateService _licenseState;
    private readonly Firmware.RolloutSuppressionRegistry _suppression;

    /// <summary>
    /// Mutes the site's standard alerts while a step stops UniFi Network, the way Firmware Rollout
    /// mutes a Network app update. Refreshed every minute until disposed, then left to lapse on its
    /// own: devices re-inform a minute or two after Network is back.
    /// </summary>
    private IDisposable NetworkRestartWindow()
    {
        var slug = _siteContext.Slug;
        _suppression.RefreshConsoleCycle(slug, DateTime.UtcNow);
        return new System.Threading.Timer(_ => _suppression.RefreshConsoleCycle(slug, DateTime.UtcNow),
            null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Context for the current site's database. Performance Tweaks deployment state
    /// (PerfTweakSettings) is per-site - it tracks what's installed on that site's
    /// gateway - so the main-DB factory would show the main site's installed state
    /// (e.g. the SGMII+ patch) on every site.
    /// </summary>
    private NetworkOptimizerDbContext CreateSiteDb() =>
        _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    private Task<(bool success, string output)> RunCommandAsync(string command, TimeSpan? timeout = null)
        => _gatewaySsh.RunCommandAsync(command, timeout);

    public async Task<PerfTweaksStatus> CheckAllStatusAsync()
    {
        var status = new PerfTweaksStatus();

        try
        {
            var combinedCommand =
                // UDM boot
                // TODO: use IUdmBootService.IsInstalledAsync() instead of this inline check
                // (shared gateway boot infrastructure - NetworkOptimizer.Web.Services.Ssh.UdmBootService).
                "echo '---UDM_BOOT_CHECK---'; test -f /etc/systemd/system/udm-boot.service && echo 'installed' || echo 'missing'; " +
                "echo '---UDM_BOOT_ENABLED---'; systemctl is-enabled udm-boot 2>/dev/null || echo 'disabled'; " +
                // Gateway model and firmware
                "echo '---GATEWAY_MODEL---'; ubnt-device-info model_short 2>/dev/null || (grep -i '^shortname=' /proc/ubnthal/system.info 2>/dev/null | cut -d= -f2-) || echo 'unknown'; echo; " +
                "echo '---FIRMWARE_VERSION---'; ubnt-device-info firmware 2>/dev/null || (grep -i '^version=' /etc/os-release 2>/dev/null | cut -d= -f2- | tr -d '\"') || echo 'unknown'; echo; " +
                // Boot script hashes (for version checking)
                $"echo '---SCRIPT_HASHES---'; for s in 15-fan-control-tuning.sh 06-mongodb-ssd-offload.sh 07-mongodb-ssd-backup.sh 08-postgresql-ssd-offload.sh 09-postgresql-ssd-backup.sh 10-journald-volatile.sh 19-sfp-sgmiiplus-eth5.sh 20-sfp-sgmiiplus.sh; do [ -f {OnBootDir}/$s ] && echo \"$s:$(md5sum {OnBootDir}/$s | cut -d' ' -f1)\"; done; " +
                // Fan control
                $"echo '---FAN_BOOT_SCRIPT---'; test -f {OnBootDir}/15-fan-control-tuning.sh && echo 'exists' || echo 'missing'; " +
                "echo '---FAN_PWM---'; cat /sys/class/hwmon/hwmon0/pwm1 2>/dev/null || echo 'N/A'; " +
                "echo '---FAN_RPM---'; cat /sys/class/hwmon/hwmon0/fan1_input 2>/dev/null || echo 'N/A'; " +
                "echo '---FAN_TEMPS---'; for f in /sys/class/hwmon/hwmon0/temp*_input; do [ -f \"$f\" ] && echo \"$(basename $f .input):$(($(cat $f)/1000))\"; done; " +
                "echo '---CPU_DIE_TEMP---'; cat /sys/class/thermal/thermal_zone*/temp 2>/dev/null | sort -n | tail -1 | awk '{printf \"%d\", $1/1000}'; echo; " +
                "echo '---FAN_LOG---'; tail -3 /var/log/fan-control-tuning.log 2>/dev/null || echo 'no log'; " +
                // UniFi OS 6.0 moved the fan PID loop out of uhwd into a dedicated daemon,
                // ufcd, which exists in no 5.1.x image. Report whichever one owns the loop.
                "echo '---FAN_DAEMON---'; systemctl cat ufcd.service >/dev/null 2>&1 && echo 'ufcd' || echo 'uhwd'; " +
                "echo '---FAN_DAEMON_STATUS---'; systemctl is-active \"$(systemctl cat ufcd.service >/dev/null 2>&1 && echo ufcd || echo uhwd)\" 2>/dev/null || echo 'inactive'; " +
                // SSD availability (for MongoDB SSD tweak gating)
                "echo '---SSD_VOLUME---'; (mountpoint -q /volume1 2>/dev/null && echo '/volume1') || (for d in /volume/*/; do [ -d \"$d\" ] && mountpoint -q \"${d%/}\" 2>/dev/null && echo \"${d%/}\" && break; done) || echo 'none'; " +
                // MongoDB SSD
                $"echo '---MONGO_BOOT_SCRIPT---'; test -f {OnBootDir}/06-mongodb-ssd-offload.sh && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_MOUNTPOINT---'; mountpoint -q /data/unifi/data/db 2>/dev/null && echo 'mounted' || echo 'not-mounted'; " +
                "echo '---MONGO_FINDMNT---'; findmnt -no SOURCE /data/unifi/data/db 2>/dev/null || echo 'N/A'; " +
                "echo '---MONGO_SERVICE---'; systemctl is-active unifi-mongodb 2>/dev/null || echo 'inactive'; " +
                "echo '---MONGO_SSD_SIZE---'; du -sh /volume1/unifi-db /volume/*/unifi-db 2>/dev/null | head -1 | cut -f1 || echo 'N/A'; " +
                // MongoDB backup
                $"echo '---MONGO_BACKUP_SCRIPT---'; test -f {OnBootDir}/07-mongodb-ssd-backup.sh && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_BACKUP_CRON---'; test -f /etc/cron.d/mongodb-ssd-backup && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_HELPER_DIR---'; test -d /data/unifi-db-ssd && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_SSD_COPY---'; ls -d /volume1/unifi-db /volume1/unifi-db-backup /volume/*/unifi-db /volume/*/unifi-db-backup 2>/dev/null | grep -q . && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_EMMC_BACKUP---'; test -d /data/unifi/data/db-backup && echo 'exists' || echo 'missing'; " +
                // Database backend. Network 11.0.81 moved the app from MongoDB to PostgreSQL; the mode
                // is only on the running process's command line. Every psql is bounded by timeout 5.
                // Network 11 ships as unifi-native and can leave a versionless unifi package behind.
                "echo '---NETWORK_VERSION---'; v=$(dpkg-query -W -f='${Version}' unifi-native 2>/dev/null); [ -n \"$v\" ] || v=$(dpkg-query -W -f='${Version}' unifi 2>/dev/null); echo \"${v:-N/A}\"; " +
                "echo '---UNIFI_ACTIVE---'; systemctl is-active unifi 2>/dev/null || echo 'inactive'; " +
                "echo '---DB_MODE---'; p=$(pgrep -o -x unifi); [ -n \"$p\" ] && tr '\\0' '\\n' < /proc/$p/cmdline | sed -n 's/^-Dunifi\\.active\\.db\\.mode=//p' | head -1; echo; " +
                "echo '---MONGOD_RUNNING---'; pgrep -x mongod >/dev/null && echo 'yes' || echo 'no'; " +
                "echo '---PG_APPS_UNIT---'; systemctl is-active postgresql@14-apps 2>/dev/null || echo 'inactive'; " +
                "echo '---PG_NETWORK_TABLES---'; timeout 5 runuser -u postgres -- psql -X -w -h /var/run/postgresql -p 5434 -d unifi-network -Atc \"SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog','information_schema')\" 2>/dev/null || echo 'N/A'; " +
                "echo '---PG_OTHER_DBS---'; timeout 5 runuser -u postgres -- psql -X -w -h /var/run/postgresql -p 5434 -d postgres -Atc \"SELECT count(*) FROM pg_database WHERE datname NOT IN ('postgres','template0','template1','unifi-network')\" 2>/dev/null || echo 'N/A'; " +
                // PostgreSQL SSD
                $"echo '---PG_BOOT_SCRIPT---'; test -f {OnBootDir}/08-postgresql-ssd-offload.sh && echo 'exists' || echo 'missing'; " +
                "echo '---PG_FINDMNT---'; findmnt -no SOURCE /data/postgresql/14/apps/data 2>/dev/null || echo 'N/A'; " +
                "echo '---PG_MARKER---'; cat /data/unifi-pg-ssd/14-apps.ssd-active 2>/dev/null || echo 'missing'; " +
                $"echo '---PG_SSD_SIZE---'; du -sh /volume1/{PgSsdDirName} /volume/*/{PgSsdDirName} 2>/dev/null | head -1 | cut -f1 || echo 'N/A'; " +
                $"echo '---PG_BACKUP_SCRIPT---'; test -f {OnBootDir}/09-postgresql-ssd-backup.sh && echo 'exists' || echo 'missing'; " +
                "echo '---PG_BACKUP_CRON---'; test -f /etc/cron.d/postgresql-ssd-backup && echo 'exists' || echo 'missing'; " +
                "echo '---PG_BACKUP_AT---'; for a in /volume1/unifi-pg-backup/unifi-pg.tar /volume/*/unifi-pg-backup/unifi-pg.tar; do [ -f \"$a\" ] && timeout 5 tar -xOf \"$a\" ./completed-at 2>/dev/null && break; done || echo 'N/A'; " +
                // Journald volatile
                $"echo '---JOURNALD_BOOT_SCRIPT---'; test -f {OnBootDir}/10-journald-volatile.sh && echo 'exists' || echo 'missing'; " +
                "echo '---JOURNALD_STORAGE---'; grep '^Storage=' /etc/systemd/journald.conf 2>/dev/null | cut -d= -f2 || echo 'N/A'; " +
                "echo '---JOURNALD_FWD---'; grep '^ForwardToSyslog=' /etc/systemd/journald.conf 2>/dev/null | cut -d= -f2 || echo 'N/A'; " +
                "echo '---SYSLOG_EMMC_ROUTES---'; DESTS=$(grep -rh '^destination .* file(\"/var/log' /etc/syslog-ng/conf.d/*.conf 2>/dev/null | grep -v '/var/log/ulog' | sed -n 's/^destination \\([^ ]*\\) .*/\\1/p'); F=0; for d in $DESTS; do F=$((F+$(grep -rc \"^log.*destination($d)\" /etc/syslog-ng/conf.d/*.conf 2>/dev/null | cut -d: -f2 | awk '{s+=$1}END{print s+0}'))); done; echo $F; " +
                "echo '---THREAT_LOG_ROUTE---'; grep -c '^log.*d_idsips_threat' /etc/syslog-ng/conf.d/threat_log.conf 2>/dev/null || echo '0'; " +
                // SFP SGMII+ Port 6 (eth5 / uniphy2)
                $"echo '---SFP_PORT6_BOOT_SCRIPT---'; test -f {OnBootDir}/19-sfp-sgmiiplus-eth5.sh && echo 'exists' || echo 'missing'; " +
                $"echo '---SFP_PORT6_MODULE_FILE---'; test -f {SfpModuleDir}/force_uniphy2_sgmiiplus.ko && echo 'exists' || echo 'missing'; " +
                "echo '---SFP_PORT6_MODULE_LOADED---'; lsmod | grep -q force_uniphy2_sgmiiplus && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SFP_PORT6_CLOCK_RATE---'; cat /sys/kernel/debug/clk/uniphy2_gcc_tx_clk/clk_rate 2>/dev/null || echo 'N/A'; " +
                // Raw MMIO reads can hard-reset hardware without this SerDes, so each runs only under
                // the same condition the parser reads its value under (boot script or module present).
                $"echo '---SFP_PORT6_SERDES_REG---'; if [ -f {OnBootDir}/19-sfp-sgmiiplus-eth5.sh ] || lsmod | grep -q force_uniphy2_sgmiiplus; then busybox devmem 0x07A20218 32 2>/dev/null || echo 'N/A'; else echo 'N/A'; fi; " +
                "echo '---SFP_PORT6_ETH5_SPEED---'; ethtool eth5 2>/dev/null | grep Speed | awk '{print $2}' || echo 'N/A'; " +
                "echo '---SFP_PORT6_LOG---'; tail -3 /var/log/sfp-sgmiiplus-eth5.log 2>/dev/null || echo 'no log'; " +
                // SFP SGMII+ Port 7 (eth6 / uniphy1)
                $"echo '---SFP_BOOT_SCRIPT---'; test -f {OnBootDir}/20-sfp-sgmiiplus.sh && echo 'exists' || echo 'missing'; " +
                $"echo '---SFP_MODULE_FILE---'; test -f {SfpModuleDir}/force_uniphy1_sgmiiplus.ko && echo 'exists' || echo 'missing'; " +
                "echo '---SFP_QCA_SSDK---'; lsmod | grep -q qca_ssdk && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SFP_MODULE_LOADED---'; lsmod | grep -q force_uniphy1_sgmiiplus && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SFP_CLOCK_RATE---'; cat /sys/kernel/debug/clk/uniphy1_gcc_tx_clk/clk_rate 2>/dev/null || echo 'N/A'; " +
                $"echo '---SFP_SERDES_REG---'; if [ -f {OnBootDir}/20-sfp-sgmiiplus.sh ] || lsmod | grep -q force_uniphy1_sgmiiplus; then busybox devmem 0x07A10218 32 2>/dev/null || echo 'N/A'; else echo 'N/A'; fi; " +
                "echo '---SFP_ETH6_SPEED---'; ethtool eth6 2>/dev/null | grep Speed | awk '{print $2}' || echo 'N/A'; " +
                "echo '---SFP_LOG---'; tail -3 /var/log/sfp-sgmiiplus.log 2>/dev/null || echo 'no log'";

            var result = await RunCommandAsync(combinedCommand, TimeSpan.FromSeconds(25));
            if (!result.success)
            {
                status.Error = result.output;
                return status;
            }

            ParseStatusOutput(result.output, status, DateTime.UtcNow);
            await ApplyStoredSettingsAsync(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking performance tweaks status");
            status.Error = ex.Message;
        }

        return status;
    }

    /// <summary>
    /// Fills <paramref name="status"/> from the combined probe output. Static so tests can feed
    /// captured gateway output without SSH or a database.
    /// </summary>
    internal static void ParseStatusOutput(string output, PerfTweaksStatus status, DateTime nowUtc)
    {
        var sections = ParseDelimitedOutput(output);

        // UDM Boot
        status.UdmBootInstalled = GetSection(sections, "UDM_BOOT_CHECK").Contains("installed");
        status.UdmBootEnabled = GetSection(sections, "UDM_BOOT_ENABLED").Trim() == "enabled";

        // Gateway model - format via product DB for canonical SKU names
        var rawModel = GetSection(sections, "GATEWAY_MODEL").Trim();
        status.GatewayModel = UniFiProductDatabase.GetProductNameFromShortname(rawModel);
        var modelLower = rawModel.ToLowerInvariant();
        status.IsSupportedGateway = modelLower is "ucg-fiber" or "ucgf" or "ucgfiber"
            or "uxg-fiber" or "uxgfiber"
            or "ucg-max" or "ucgmax"
            or "uxg-max" or "uxgmax" or "uxgb";

        // Firmware version
        var fwRaw = GetSection(sections, "FIRMWARE_VERSION").Trim();
        status.FirmwareVersion = fwRaw;
        var maxFirmware = MaxSupportedFirmwareFor(modelLower);
        status.MaxSupportedFirmware = maxFirmware.ToString();
        if (Version.TryParse(fwRaw, out var fwVersion))
            status.FirmwareSupported = fwVersion <= maxFirmware;
        else
            status.FirmwareSupported = false;

        // SSD availability
        var ssdVolume = GetSection(sections, "SSD_VOLUME").Trim();
        status.SsdAvailable = ssdVolume != "none" && !string.IsNullOrEmpty(ssdVolume);
        status.SsdMountPath = status.SsdAvailable ? ssdVolume : null;

        // Fan control
        var fanStatus = new TweakDeploymentStatus { Id = "fan-control" };
        fanStatus.BootScriptDeployed = GetSection(sections, "FAN_BOOT_SCRIPT").Contains("exists");
        var fanLogExists = !GetSection(sections, "FAN_LOG").Contains("no log");
        fanStatus.RuntimeDetected = fanLogExists;
        if (fanStatus.BootScriptDeployed || fanLogExists)
        {
            fanStatus.IsActive = fanStatus.BootScriptDeployed;
            var pwm = GetSection(sections, "FAN_PWM").Trim();
            var rpm = GetSection(sections, "FAN_RPM").Trim();
            var fanDaemon = GetSection(sections, "FAN_DAEMON").Trim();
            if (string.IsNullOrEmpty(fanDaemon)) fanDaemon = "uhwd";
            var fanDaemonActive = GetSection(sections, "FAN_DAEMON_STATUS").Trim() == "active";
            fanStatus.HealthChecks.Add(new("Fan Speed", rpm != "N/A" ? $"{rpm} RPM (PWM {pwm})" : "N/A", fanDaemonActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

            var cpuDieTemp = GetSection(sections, "CPU_DIE_TEMP").Trim();
            if (int.TryParse(cpuDieTemp, out var cpuDie))
                fanStatus.HealthChecks.Add(new("CPU Die Temp", $"{cpuDie} C", HealthCheckStatus.Ok));

            var tempsRaw = GetSection(sections, "FAN_TEMPS").Trim();
            if (!string.IsNullOrEmpty(tempsRaw))
            {
                var tempParts = tempsRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var tempValues = new List<string>();
                foreach (var part in tempParts)
                {
                    var kv = part.Trim().Split(':');
                    if (kv.Length == 2 && int.TryParse(kv[1], out var tempC))
                        tempValues.Add($"{tempC} C");
                }
                if (tempValues.Any())
                    fanStatus.HealthChecks.Add(new("Board Temps", string.Join(" / ", tempValues), HealthCheckStatus.Ok));
            }

            fanStatus.HealthChecks.Add(new($"{fanDaemon} Service", fanDaemonActive ? "Running" : "Not running", fanDaemonActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

            var fanLog = GetSection(sections, "FAN_LOG").Trim();
            if (fanLog.Contains("ERROR"))
                fanStatus.HealthChecks.Add(new("Last Run", "Error - check log", HealthCheckStatus.Error));
            else if (fanLog.Contains("Done"))
                fanStatus.HealthChecks.Add(new("Last Run", "OK", HealthCheckStatus.Ok));
        }
        status.Tweaks["fan-control"] = fanStatus;

        // MongoDB SSD
        var mongoStatus = new TweakDeploymentStatus { Id = "mongodb-ssd" };
        mongoStatus.BootScriptDeployed = GetSection(sections, "MONGO_BOOT_SCRIPT").Contains("exists");
        var mongoMounted = GetSection(sections, "MONGO_MOUNTPOINT").Trim() == "mounted";
        mongoStatus.RuntimeDetected = mongoMounted;
        if (mongoStatus.BootScriptDeployed || mongoMounted)
        {
            mongoStatus.IsActive = mongoStatus.BootScriptDeployed && mongoMounted;
            var source = GetSection(sections, "MONGO_FINDMNT").Trim();
            var mongoActive = GetSection(sections, "MONGO_SERVICE").Trim() == "active";
            var ssdSize = GetSection(sections, "MONGO_SSD_SIZE").Trim();
            mongoStatus.HealthChecks.Add(new("Bind Mount", mongoMounted ? "Active" : "Not mounted", mongoMounted ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            if (mongoMounted && source != "N/A")
                mongoStatus.HealthChecks.Add(new("Source", source, HealthCheckStatus.Ok));
            mongoStatus.HealthChecks.Add(new("MongoDB", mongoActive ? "Running" : "Not running", mongoActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            if (ssdSize != "N/A")
                mongoStatus.HealthChecks.Add(new("SSD Usage", ssdSize, HealthCheckStatus.Ok));

            var backupDeployed = GetSection(sections, "MONGO_BACKUP_SCRIPT").Contains("exists");
            var backupCron = GetSection(sections, "MONGO_BACKUP_CRON").Contains("exists");
            mongoStatus.HealthChecks.Add(new("Backup", backupDeployed && backupCron ? "Active (daily SSD + weekly eMMC)" : "Not configured", backupDeployed ? HealthCheckStatus.Ok : HealthCheckStatus.Warning));
        }
        status.Tweaks["mongodb-ssd"] = mongoStatus;

        // Database backend
        var networkVersion = GetSection(sections, "NETWORK_VERSION").Trim();
        status.NetworkVersion = networkVersion is "" or "N/A" ? null : networkVersion;
        status.DatabaseBackend = ClassifyBackend(
            networkVersion,
            GetSection(sections, "UNIFI_ACTIVE").Trim(),
            GetSection(sections, "DB_MODE").Trim(),
            GetSection(sections, "PG_APPS_UNIT").Trim(),
            GetSection(sections, "PG_NETWORK_TABLES").Trim());
        status.MongodRunning = GetSection(sections, "MONGOD_RUNNING").Trim() == "yes";
        status.PgOtherDatabases = int.TryParse(GetSection(sections, "PG_OTHER_DBS").Trim(), out var otherDbs) ? otherDbs : null;

        // MongoDB artifacts left by 06/07, which the postgresql-ssd deploy decommissions first
        status.MongoArtifactsPresent = mongoStatus.BootScriptDeployed
            || GetSection(sections, "MONGO_BACKUP_SCRIPT").Contains("exists")
            || GetSection(sections, "MONGO_BACKUP_CRON").Contains("exists")
            || mongoMounted
            || GetSection(sections, "MONGO_HELPER_DIR").Contains("exists")
            || GetSection(sections, "MONGO_SSD_COPY").Contains("exists")
            || GetSection(sections, "MONGO_EMMC_BACKUP").Contains("exists");

        // PostgreSQL SSD
        var pgStatus = new TweakDeploymentStatus { Id = "postgresql-ssd" };
        pgStatus.BootScriptDeployed = GetSection(sections, "PG_BOOT_SCRIPT").Contains("exists");
        var pgSource = GetSection(sections, "PG_FINDMNT").Trim();
        var pgBound = pgSource.EndsWith($"[/{PgSsdDirName}]", StringComparison.Ordinal);
        var markerRaw = GetSection(sections, "PG_MARKER").Trim();
        var markerPresent = markerRaw.Length > 0 && markerRaw != "missing";
        var markerFields = markerRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var markerTarget = markerFields.Length >= 2 ? markerFields[1] : null;
        var expectedTarget = status.SsdMountPath != null ? $"{status.SsdMountPath}/{PgSsdDirName}" : null;
        var markerOk = markerPresent && markerTarget != null && markerTarget == expectedTarget;
        pgStatus.RuntimeDetected = pgBound || markerPresent;
        if (pgStatus.BootScriptDeployed || pgStatus.RuntimeDetected)
        {
            pgStatus.IsActive = pgStatus.BootScriptDeployed && pgBound && markerOk;
            var pgUnitActive = GetSection(sections, "PG_APPS_UNIT").Trim() == "active";
            var pgSsdSize = GetSection(sections, "PG_SSD_SIZE").Trim();
            pgStatus.HealthChecks.Add(new("Bind Mount", pgBound ? "Active" : "Not mounted", pgBound ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            if (pgBound)
                pgStatus.HealthChecks.Add(new("Source", pgSource, HealthCheckStatus.Ok));
            pgStatus.HealthChecks.Add(new("Authority Marker", markerOk ? "Present" : markerPresent ? $"Names {markerTarget ?? "nothing"}" : "Missing", markerOk ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            pgStatus.HealthChecks.Add(new("PostgreSQL", pgUnitActive ? "Running" : "Not running", pgUnitActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            if (pgSsdSize is not ("" or "N/A"))
                pgStatus.HealthChecks.Add(new("SSD Usage", pgSsdSize, HealthCheckStatus.Ok));
            // The Network app can start mongod on demand in PostgreSQL mode, so this is a detail only.
            if (status.MongodRunning)
                pgStatus.HealthChecks.Add(new("On-demand mongod", "Running", HealthCheckStatus.Ok));

            var pgBackupDeployed = GetSection(sections, "PG_BACKUP_SCRIPT").Contains("exists");
            var pgBackupCron = GetSection(sections, "PG_BACKUP_CRON").Contains("exists");
            pgStatus.HealthChecks.Add(BackupHealth(pgBackupDeployed && pgBackupCron, GetSection(sections, "PG_BACKUP_AT").Trim(), nowUtc));

            // 08 never repairs these states on its own, so they need a person.
            if (pgBound && !markerPresent)
                pgStatus.IssueDescription = "PostgreSQL is bind-mounted from the SSD, but the authority marker is missing. Manual recovery is needed.";
            else if (markerPresent && !pgBound)
                pgStatus.IssueDescription = "The authority marker is present, but PostgreSQL is not bind-mounted from the SSD. UniFi Network may be running on older eMMC data.";
            else if (markerPresent && !markerOk)
                pgStatus.IssueDescription = $"The authority marker names {markerTarget ?? "no path"}, but the SSD copy is expected at {expectedTarget ?? "an SSD volume"}.";
        }
        status.Tweaks["postgresql-ssd"] = pgStatus;

        // Journald volatile
        var journaldStatus = new TweakDeploymentStatus { Id = "journald-volatile" };
        journaldStatus.BootScriptDeployed = GetSection(sections, "JOURNALD_BOOT_SCRIPT").Contains("exists");
        var storageVal = GetSection(sections, "JOURNALD_STORAGE").Trim();
        var fwdVal = GetSection(sections, "JOURNALD_FWD").Trim();
        var journaldConfigured = storageVal == "volatile" && fwdVal == "no";
        journaldStatus.RuntimeDetected = journaldConfigured;
        if (journaldStatus.BootScriptDeployed || journaldConfigured)
        {
            var syslogEmmcRoutes = GetSection(sections, "SYSLOG_EMMC_ROUTES").Trim();
            int.TryParse(syslogEmmcRoutes, out var emmcRouteCount);
            var threatRouteVal = GetSection(sections, "THREAT_LOG_ROUTE").Trim();
            int.TryParse(threatRouteVal, out var threatRouteCount);

            journaldStatus.IsActive = journaldStatus.BootScriptDeployed && journaldConfigured && emmcRouteCount == 0;
            journaldStatus.HealthChecks.Add(new("journald Storage", storageVal == "volatile" ? "Volatile (RAM)" : $"{storageVal} (eMMC)", storageVal == "volatile" ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            journaldStatus.HealthChecks.Add(new("Syslog Forward", fwdVal == "no" ? "Disabled" : "Enabled", fwdVal == "no" ? HealthCheckStatus.Ok : HealthCheckStatus.Warning));
            journaldStatus.HealthChecks.Add(new("eMMC Log Routes", emmcRouteCount == 0 ? "All disabled" : $"{emmcRouteCount} still active", emmcRouteCount == 0 ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            journaldStatus.HealthChecks.Add(new("IDS/IPS Threat Pipeline", threatRouteCount > 0 ? "Active" : "Not found", threatRouteCount > 0 ? HealthCheckStatus.Ok : HealthCheckStatus.Warning));
        }
        status.Tweaks["journald-volatile"] = journaldStatus;

        // qca-ssdk is shared between both SFP ports
        var sfpQcaSsdkLoaded = GetSection(sections, "SFP_QCA_SSDK").Trim() == "loaded";
        status.SfpQcaSsdkMissing = !sfpQcaSsdkLoaded;

        // SFP SGMII+ Port 6 (eth5 / uniphy2)
        var sfpPort6Status = new TweakDeploymentStatus { Id = "sfp-sgmiiplus-port6" };
        sfpPort6Status.BootScriptDeployed = GetSection(sections, "SFP_PORT6_BOOT_SCRIPT").Contains("exists");
        var sfpPort6ModuleExists = GetSection(sections, "SFP_PORT6_MODULE_FILE").Contains("exists");
        var sfpPort6ModuleLoaded = GetSection(sections, "SFP_PORT6_MODULE_LOADED").Trim() == "loaded";
        var port6ClockRate = GetSection(sections, "SFP_PORT6_CLOCK_RATE").Trim();
        var port6SerdesReg = GetSection(sections, "SFP_PORT6_SERDES_REG").Trim().ToLowerInvariant();
        var port6EthSpeed = GetSection(sections, "SFP_PORT6_ETH5_SPEED").Trim();
        status.SfpPort6ModuleAlreadyLoaded = sfpPort6ModuleLoaded;
        sfpPort6Status.RuntimeDetected = sfpPort6ModuleLoaded;

        if (sfpPort6Status.BootScriptDeployed || sfpPort6ModuleLoaded)
        {
            var isSgmiiPlus = port6SerdesReg.EndsWith("50");
            var isSgmii = port6SerdesReg.EndsWith("30");
            var is25g = port6ClockRate == "312500000" && isSgmiiPlus;
            sfpPort6Status.IsActive = sfpPort6Status.BootScriptDeployed && sfpPort6ModuleExists && is25g;

            sfpPort6Status.HealthChecks.Add(new("SFP Kernel Module", sfpPort6ModuleLoaded ? "Loaded" : "Not loaded", sfpPort6ModuleLoaded ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            sfpPort6Status.HealthChecks.Add(new("qca-ssdk", sfpQcaSsdkLoaded ? "Loaded" : "Missing (required)", sfpQcaSsdkLoaded ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            sfpPort6Status.HealthChecks.Add(new("Module File", sfpPort6ModuleExists ? $"{SfpModuleDir}/" : "Missing", sfpPort6ModuleExists ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

            if (port6ClockRate != "N/A")
            {
                var clockLabel = port6ClockRate == "312500000" ? "312.5 MHz (2.5 Gbps)" : port6ClockRate == "125000000" ? "125 MHz (1 Gbps)" : $"{port6ClockRate} Hz";
                sfpPort6Status.HealthChecks.Add(new("Clock Rate", clockLabel, port6ClockRate == "312500000" ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            }

            if (port6SerdesReg != "n/a")
            {
                var regDisplay = FormatHexRegister(port6SerdesReg);
                var regLabel = isSgmiiPlus ? $"{regDisplay} (SGMII+)" : isSgmii ? $"{regDisplay} (SGMII)" : regDisplay;
                sfpPort6Status.HealthChecks.Add(new("SerDes Register", regLabel, isSgmiiPlus ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            }

            if (port6EthSpeed != "N/A" && port6EthSpeed != "Unknown!")
                sfpPort6Status.HealthChecks.Add(new("eth5 Speed", FormatLinkSpeed(port6EthSpeed), port6EthSpeed.Contains("2500") ? HealthCheckStatus.Ok : HealthCheckStatus.Warning));
            else if (port6EthSpeed == "Unknown!")
                sfpPort6Status.HealthChecks.Add(new("eth5 Speed", "No link", HealthCheckStatus.Ok));

            if (sfpPort6ModuleLoaded && !is25g && port6ClockRate != "N/A")
            {
                sfpPort6Status.IssueDescription = "Module loaded but clock/register mismatch - link may not be running at 2.5 Gbps.";
            }
        }
        status.Tweaks["sfp-sgmiiplus-port6"] = sfpPort6Status;

        // SFP SGMII+ Port 7 (eth6 / uniphy1)
        var sfpStatus = new TweakDeploymentStatus { Id = "sfp-sgmiiplus" };
        sfpStatus.BootScriptDeployed = GetSection(sections, "SFP_BOOT_SCRIPT").Contains("exists");
        var sfpModuleExists = GetSection(sections, "SFP_MODULE_FILE").Contains("exists");
        var sfpModuleLoaded = GetSection(sections, "SFP_MODULE_LOADED").Trim() == "loaded";
        var clockRate = GetSection(sections, "SFP_CLOCK_RATE").Trim();
        var serdesReg = GetSection(sections, "SFP_SERDES_REG").Trim().ToLowerInvariant();
        var ethSpeed = GetSection(sections, "SFP_ETH6_SPEED").Trim();
        status.SfpModuleAlreadyLoaded = sfpModuleLoaded;
        sfpStatus.RuntimeDetected = sfpModuleLoaded;

        if (sfpStatus.BootScriptDeployed || sfpModuleLoaded)
        {
            var isSgmiiPlus = serdesReg.EndsWith("50");
            var isSgmii = serdesReg.EndsWith("30");
            var is25g = clockRate == "312500000" && isSgmiiPlus;
            sfpStatus.IsActive = sfpStatus.BootScriptDeployed && sfpModuleExists && is25g;

            sfpStatus.HealthChecks.Add(new("SFP Kernel Module", sfpModuleLoaded ? "Loaded" : "Not loaded", sfpModuleLoaded ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            sfpStatus.HealthChecks.Add(new("qca-ssdk", sfpQcaSsdkLoaded ? "Loaded" : "Missing (required)", sfpQcaSsdkLoaded ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            sfpStatus.HealthChecks.Add(new("Module File", sfpModuleExists ? $"{SfpModuleDir}/" : "Missing", sfpModuleExists ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

            if (clockRate != "N/A")
            {
                var clockLabel = clockRate == "312500000" ? "312.5 MHz (2.5 Gbps)" : clockRate == "125000000" ? "125 MHz (1 Gbps)" : $"{clockRate} Hz";
                sfpStatus.HealthChecks.Add(new("Clock Rate", clockLabel, clockRate == "312500000" ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            }

            if (serdesReg != "n/a")
            {
                var regDisplay = FormatHexRegister(serdesReg);
                var regLabel = isSgmiiPlus ? $"{regDisplay} (SGMII+)" : isSgmii ? $"{regDisplay} (SGMII)" : regDisplay;
                sfpStatus.HealthChecks.Add(new("SerDes Register", regLabel, isSgmiiPlus ? HealthCheckStatus.Ok : HealthCheckStatus.Error));
            }

            if (ethSpeed != "N/A" && ethSpeed != "Unknown!")
                sfpStatus.HealthChecks.Add(new("eth6 Speed", FormatLinkSpeed(ethSpeed), ethSpeed.Contains("2500") ? HealthCheckStatus.Ok : HealthCheckStatus.Warning));
            else if (ethSpeed == "Unknown!")
                sfpStatus.HealthChecks.Add(new("eth6 Speed", "No link", HealthCheckStatus.Ok));

            if (sfpModuleLoaded && !is25g && clockRate != "N/A")
            {
                sfpStatus.IssueDescription = "Module loaded but clock/register mismatch - link may not be running at 2.5 Gbps.";
            }
        }
        status.Tweaks["sfp-sgmiiplus"] = sfpStatus;

        // Check boot script versions against our embedded copies
        var remoteHashes = new Dictionary<string, string>();
        var hashesRaw = GetSection(sections, "SCRIPT_HASHES").Trim();
        foreach (var line in hashesRaw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(':');
            if (parts.Length == 2)
                remoteHashes[parts[0]] = parts[1];
        }

        foreach (var (tweakId, tweak) in status.Tweaks)
        {
            if (!tweak.BootScriptDeployed) continue;
            // A stale 06/07 on a PostgreSQL gateway is retired by postgresql-ssd, not updated.
            if (tweakId == "mongodb-ssd" && status.DatabaseBackend == DatabaseBackend.PostgreSql) continue;

            var scriptNames = new List<string>();
            if (BootScriptFiles.GetValueOrDefault(tweakId) is { } primary)
                scriptNames.Add(primary);
            if (CompanionScripts.GetValueOrDefault(tweakId) is { } companions)
                scriptNames.AddRange(companions);

            foreach (var scriptName in scriptNames)
            {
                if (remoteHashes.TryGetValue(scriptName, out var remoteHash) &&
                    ExpectedHashes.Value.TryGetValue(scriptName, out var expectedHash) &&
                    remoteHash != expectedHash)
                {
                    tweak.ScriptOutdated = true;
                    tweak.HealthChecks.Add(new("Boot Script", "Update available", HealthCheckStatus.Warning));
                    break;
                }
            }
        }
    }

    /// <summary>Applies the site's stored PerfTweakSettings (manual deploys, decommission record).</summary>
    private async Task ApplyStoredSettingsAsync(PerfTweaksStatus status)
    {
        // Load manually-deployed state from DB and adjust health checks
        await using var db = CreateSiteDb();
        var manualTweaks = await db.PerfTweakSettings.ToListAsync();
        status.MongoDecommissioned = manualTweaks.Any(m => m.TweakId == MongoDecommissionedSettingId);
        foreach (var manual in manualTweaks.Where(m => m.IsManuallyDeployed))
        {
            if (!status.Tweaks.TryGetValue(manual.TweakId, out var tweak))
            {
                tweak = new TweakDeploymentStatus { Id = manual.TweakId };
                status.Tweaks[manual.TweakId] = tweak;
            }

            tweak.IsManuallyDeployed = true;
            if (!tweak.BootScriptDeployed && !tweak.IsActive)
                tweak.IsActive = true;

            // For manual deploys, downgrade file-existence checks from Error to Ok -
            // the user may have their own scripts with different names/paths
            for (int i = 0; i < tweak.HealthChecks.Count; i++)
            {
                var hc = tweak.HealthChecks[i];
                if (hc.Status == HealthCheckStatus.Error &&
                    (hc.Label == "Module File" || hc.Label == "Boot Script"))
                {
                    tweak.HealthChecks[i] = hc with
                    {
                        Value = hc.Value + " (OK for manual deploy)",
                        Status = HealthCheckStatus.Ok
                    };
                }
            }
        }
    }

    public async Task<(bool success, string message, List<string> steps)> DeployTweakAsync(
        string tweakId, IProgress<string>? progress = null)
    {
        var steps = new List<string>();
        void Report(string step) { steps.Add(step); progress?.Report(step); }

        if (!_licenseState.IsSiteOperational(_siteContext.Slug))
        {
            _logger.LogWarning("Performance tweak deploy refused: site {Site} is license-restricted", _siteContext.Slug);
            return (false, Licensing.LicenseGuard.RestrictedMessage, steps);
        }

        try
        {
            if (tweakId is "sfp-sgmiiplus-port6" or "sfp-sgmiiplus")
            {
                var otherTweakId = tweakId == "sfp-sgmiiplus-port6" ? "sfp-sgmiiplus" : "sfp-sgmiiplus-port6";
                var otherModuleName = tweakId == "sfp-sgmiiplus-port6" ? "force_uniphy1_sgmiiplus" : "force_uniphy2_sgmiiplus";
                var checkOther = await RunCommandAsync($"echo '---OTHER---'; lsmod | grep -q {otherModuleName} && echo 'loaded' || echo 'not-loaded'");
                var otherSections = ParseDelimitedOutput(checkOther.output);
                if (GetSection(otherSections, "OTHER").Trim() == "loaded")
                    return (false, $"Cannot deploy: the other SFP+ SGMII+ patch ({otherTweakId}) is currently loaded. Only one can be active at a time. Remove it first.", steps);

                var (moduleName, uniphyName) = tweakId == "sfp-sgmiiplus-port6"
                    ? ("force_uniphy2_sgmiiplus", "uniphy2")
                    : ("force_uniphy1_sgmiiplus", "uniphy1");
                return await DeploySfpTweakAsync(tweakId, moduleName, uniphyName, progress);
            }

            if (tweakId == "postgresql-ssd")
                return await DeployPostgresSsdAsync(progress);

            var scriptName = BootScriptFiles.GetValueOrDefault(tweakId);
            if (scriptName == null)
                return (false, $"Unknown tweak: {tweakId}", steps);

            var scriptContent = ReadEmbeddedResource(scriptName);
            if (scriptContent == null)
                return (false, $"Embedded resource not found: {scriptName}", steps);

            Report($"Deploying {scriptName}...");
            var b64 = GatewayFile.ToBase64(scriptContent);
            var deployCmd = $"echo '{b64}' | base64 -d > {OnBootDir}/{scriptName} && chmod +x {OnBootDir}/{scriptName} && echo 'deployed'";
            var result = await RunCommandAsync(deployCmd);
            if (!result.success || !result.output.Contains("deployed"))
                return (false, $"Failed to deploy script: {result.output}", steps);

            // If deploying mongodb-ssd, also deploy the backup script
            if (tweakId == "mongodb-ssd")
            {
                var backupName = BootScriptFiles["mongodb-backup"];
                var backupContent = ReadEmbeddedResource(backupName);
                if (backupContent != null)
                {
                    Report($"Deploying {backupName} (backup companion)...");
                    var b64Backup = GatewayFile.ToBase64(backupContent);
                    var backupCmd = $"echo '{b64Backup}' | base64 -d > {OnBootDir}/{backupName} && chmod +x {OnBootDir}/{backupName} && echo 'deployed'";
                    await RunCommandAsync(backupCmd);
                }
            }

            using var networkRestart = tweakId == "mongodb-ssd" ? NetworkRestartWindow() : null;
            Report($"Running {scriptName}...");
            var runResult = await RunCommandAsync($"{OnBootDir}/{scriptName} 2>&1", TimeSpan.FromMinutes(5));
            if (!runResult.success)
            {
                Report($"Warning: Script returned non-zero exit. Output: {runResult.output}");
            }
            else
            {
                Report("Script completed successfully.");
            }

            // Run backup companion too if mongodb-ssd
            if (tweakId == "mongodb-ssd")
            {
                var backupName = BootScriptFiles["mongodb-backup"];
                Report($"Running {backupName}...");
                await RunCommandAsync($"{OnBootDir}/{backupName} 2>&1", TimeSpan.FromMinutes(2));
                Report("Backup setup complete.");
            }

            Report("Verifying deployment...");
            var verifyResult = await RunCommandAsync($"test -f {OnBootDir}/{scriptName} && echo 'verified'");
            if (verifyResult.output.Contains("verified"))
                Report("Done.");
            else
                Report("Warning: verification failed.");

            await PersistDeployedStateAsync(tweakId);
            if (tweakId == "mongodb-ssd")
                await RemoveSettingAsync(MongoDecommissionedSettingId);
            return (true, "Deployed successfully", steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy tweak {TweakId}", tweakId);
            return (false, ex.Message, steps);
        }
    }

    /// <summary>
    /// Decommissions the MongoDB SSD tweak when anything of it is left, then runs 08 and 09. 08 exits 0
    /// on its skip paths (no SSD, no unifi-network database), so success needs the bind verified after.
    /// </summary>
    private async Task<(bool success, string message, List<string> steps)> DeployPostgresSsdAsync(IProgress<string>? progress)
    {
        var steps = new List<string>();
        void Report(string step) { steps.Add(step); progress?.Report(step); }
        var warnings = new List<string>();

        Report("Checking the database backend...");
        var status = await CheckAllStatusAsync();
        if (status.Error != null)
            return (false, $"Could not read the gateway status: {status.Error}", steps);
        if (status.DatabaseBackend != DatabaseBackend.PostgreSql)
            return (false, "UniFi Network is not confirmed to run on PostgreSQL. Nothing was changed.", steps);
        if (!status.SsdAvailable)
            return (false, "No SSD volume detected. Nothing was changed.", steps);
        if (status.PgOtherDatabases != 0)
            return (false, status.PgOtherDatabases == null
                ? "Could not list the databases in the PostgreSQL cluster. Nothing was changed."
                : "Shared PostgreSQL cluster: not supported. Nothing was changed.", steps);

        using var networkRestart = NetworkRestartWindow();
        if (status.MongoArtifactsPresent)
        {
            Report("Retiring the MongoDB SSD tweak. UniFi Network stops for about 1 to 2 minutes...");
            var decom = await RunDecommissionAsync("--decommission");
            Report($"MongoDB decommission: {decom.Line}");
            if (!decom.Ok)
                return (false, $"MongoDB decommission failed{(decom.Step is { } failedStep ? $" at step {failedStep}" : "")}: {decom.Reason} PostgreSQL was not changed.", steps);
            warnings.AddRange(DecommissionWarnings(decom));
            if (!decom.Noop)
            {
                // The gateway no longer has the MongoDB tweak, whatever happens to 08 next.
                await RemoveSettingAsync("mongodb-ssd");
                await PersistDeployedStateAsync(MongoDecommissionedSettingId);
            }
        }

        foreach (var name in new[] { BootScriptFiles["postgresql-ssd"], BootScriptFiles["postgresql-backup"] })
        {
            Report($"Deploying {name}...");
            if (!await WriteGatewayScriptAsync(OnBootDir, name, ReadEmbeddedResource(name)))
                return (false, $"Failed to deploy {name}.", steps);
        }

        var offloadName = BootScriptFiles["postgresql-ssd"];
        Report($"Running {offloadName}. UniFi Network stops briefly while PostgreSQL moves to the SSD...");
        // Executed, never sourced: 08 refuses to run sourced.
        var run08 = await RunCommandAsync($"{OnBootDir}/{offloadName} 2>&1", TimeSpan.FromMinutes(10));
        if (!run08.success)
            return (false, $"{offloadName} failed. The boot hooks stay in place, so the next boot retries.\n{Tail(run08.output)}", steps);

        var backupName = BootScriptFiles["postgresql-backup"];
        Report($"Running {backupName} (initial backup)...");
        var run09 = await RunCommandAsync($"{OnBootDir}/{backupName} 2>&1", TimeSpan.FromMinutes(10));
        if (!run09.success)
        {
            Report($"Warning: {backupName} exited nonzero. Its cron retries each night.");
            warnings.Add($"The initial backup did not complete. {backupName} retries each night.");
        }

        Report("Verifying deployment...");
        var verify = await CheckAllStatusAsync();
        if (verify.Error != null || verify.Tweaks.GetValueOrDefault("postgresql-ssd")?.IsActive != true)
            return (false, $"{offloadName} finished, but PostgreSQL is not bind-mounted from the SSD.\n{Tail(run08.output)}", steps);

        await PersistDeployedStateAsync("postgresql-ssd");
        Report("Done.");
        return (true, warnings.Count > 0 ? "Deployed with warnings: " + string.Join(" ", warnings) : "Deployed successfully", steps);
    }

    /// <summary>Writes the embedded decommission script to PerfTweaksDir and executes it in the given mode.</summary>
    private async Task<DecommissionResult> RunDecommissionAsync(string mode)
    {
        if (!await WriteGatewayScriptAsync(PerfTweaksDir, DecommissionScript, ReadEmbeddedResource(DecommissionScript)))
            return new(false, null, null, 0, false, null, false, null, "Could not write the decommission script to the gateway.", "");
        // Waits up to 10 min for unifi.service to start, plus the copy time.
        var run = await RunCommandAsync($"{PerfTweaksDir}/{DecommissionScript} {mode} 2>&1", TimeSpan.FromMinutes(15));
        return ParseDecommissionResult(run.output, run.success);
    }

    /// <summary>Writes a script with mode 0700. Executable matters: udm-boot sources a non-executable .sh.</summary>
    private async Task<bool> WriteGatewayScriptAsync(string dir, string fileName, string? content)
    {
        if (content == null) return false;
        var b64 = GatewayFile.ToBase64(content);
        var result = await RunCommandAsync($"mkdir -p {dir} && echo '{b64}' | base64 -d > {dir}/{fileName} && chmod 0700 {dir}/{fileName} && echo 'deployed'");
        return result.success && result.output.Contains("deployed");
    }

    private async Task RemoveSettingAsync(string tweakId)
    {
        await using var db = CreateSiteDb();
        var setting = await db.PerfTweakSettings.FirstOrDefaultAsync(s => s.TweakId == tweakId);
        if (setting != null)
        {
            db.PerfTweakSettings.Remove(setting);
            await db.SaveChangesAsync();
        }
    }

    private async Task<(bool success, string message, List<string> steps)> DeploySfpTweakAsync(
        string tweakId, string moduleName, string uniphyName, IProgress<string>? progress = null)
    {
        var steps = new List<string>();
        void Report(string step) { steps.Add(step); progress?.Report(step); }

        try
        {
            Report("Checking prerequisites...");
            var checkResult = await RunCommandAsync(
                $"echo '---MODULE---'; lsmod | grep -q {moduleName} && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SSDK---'; lsmod | grep -q qca_ssdk && echo 'loaded' || echo 'not-loaded'");
            var checkSections = ParseDelimitedOutput(checkResult.output);

            if (GetSection(checkSections, "MODULE").Trim() == "loaded")
                return (false, "SFP module is already loaded. Use 'Mark as Manually Deployed' for monitoring.", steps);

            if (GetSection(checkSections, "SSDK").Trim() != "loaded")
                return (false, "qca-ssdk kernel module is not loaded. This is a required dependency for the SFP SGMII+ patch.", steps);

            Report("Deploying kernel module to /data/sfp-sgmiiplus/...");
            var koBytes = ReadEmbeddedResourceBytes($"{moduleName}.ko");
            if (koBytes == null)
                return (false, "Kernel module not found in embedded resources", steps);

            var b64Ko = Convert.ToBase64String(koBytes);
            var koCmd = $"mkdir -p {SfpModuleDir} && echo '{b64Ko}' | base64 -d > {SfpModuleDir}/{moduleName}.ko && echo 'deployed'";
            var koResult = await RunCommandAsync(koCmd);
            if (!koResult.success || !koResult.output.Contains("deployed"))
                return (false, $"Failed to deploy kernel module: {koResult.output}", steps);

            var scriptName = BootScriptFiles[tweakId];
            var scriptContent = ReadEmbeddedResource(scriptName);
            if (scriptContent == null)
                return (false, $"Boot script not found: {scriptName}", steps);

            Report($"Deploying {scriptName}...");
            var b64Script = GatewayFile.ToBase64(scriptContent);
            var scriptCmd = $"echo '{b64Script}' | base64 -d > {OnBootDir}/{scriptName} && chmod +x {OnBootDir}/{scriptName} && echo 'deployed'";
            var scriptResult = await RunCommandAsync(scriptCmd);
            if (!scriptResult.success || !scriptResult.output.Contains("deployed"))
                return (false, $"Failed to deploy boot script: {scriptResult.output}", steps);

            Report("Loading kernel module...");
            var loadResult = await RunCommandAsync($"{OnBootDir}/{scriptName} 2>&1", TimeSpan.FromSeconds(30));

            Report("Verifying...");
            var verifyResult = await RunCommandAsync(
                $"echo '---MOD---'; lsmod | grep -q {moduleName} && echo 'loaded' || echo 'not-loaded'; " +
                $"echo '---CLK---'; cat /sys/kernel/debug/clk/{uniphyName}_gcc_tx_clk/clk_rate 2>/dev/null || echo 'N/A'");
            var verifySections = ParseDelimitedOutput(verifyResult.output);
            var modLoaded = GetSection(verifySections, "MOD").Trim() == "loaded";
            var clkOk = GetSection(verifySections, "CLK").Trim() == "312500000";

            if (modLoaded && clkOk)
                Report($"Verified: Module loaded, {uniphyName} at 312.5 MHz (2.5 Gbps).");
            else if (modLoaded)
                Report("Module loaded but clock rate not at expected value. Check logs.");
            else
            {
                var logName = tweakId == "sfp-sgmiiplus-port6" ? "sfp-sgmiiplus-eth5" : "sfp-sgmiiplus";
                Report($"Warning: Module may not have loaded correctly. Check /var/log/{logName}.log");
            }

            Report("Done.");
            await PersistDeployedStateAsync(tweakId);
            return (true, "SFP SGMII+ patch deployed", steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy SFP tweak {TweakId}", tweakId);
            return (false, ex.Message, steps);
        }
    }

    public async Task<(bool success, string message)> RemoveTweakAsync(string tweakId, PerfTweaksStatus? status = null)
    {
        try
        {
            var scriptName = BootScriptFiles.GetValueOrDefault(tweakId);
            if (scriptName == null)
                return (false, $"Unknown tweak: {tweakId}");

            if (tweakId == "mongodb-ssd")
            {
                // The decommission script in Remove mode: it aborts while mongod runs or the umount
                // fails, stages the newest copy before the swap, and keeps the SSD copy and backups.
                using var networkRestart = NetworkRestartWindow();
                var decom = await RunDecommissionAsync("--remove");
                if (!decom.Ok)
                    return (false, $"Remove failed{(decom.Step is { } failedStep ? $" at step {failedStep}" : "")}: {decom.Reason}");
                await RemoveSettingAsync(tweakId);
                var warnings = DecommissionWarnings(decom);
                return (true, warnings.Count > 0 ? "Removed with warnings: " + string.Join(" ", warnings) : "Removed");
            }

            if (tweakId == "postgresql-ssd")
            {
                if (!await WriteGatewayScriptAsync(PerfTweaksDir, PgRevertScript, PgRevertScriptContent.Replace("\r\n", "\n")))
                    return (false, "Could not write the revert script to the gateway.");
                using var networkRestart = NetworkRestartWindow();
                var revert = await RunCommandAsync($"{PerfTweaksDir}/{PgRevertScript} 2>&1", TimeSpan.FromMinutes(10));
                if (!revert.success || !revert.output.Contains("removed"))
                    return (false, $"Remove failed. If it stopped UniFi Network first, the Network app stays stopped for investigation.\n{Tail(revert.output)}");
                await RemoveSettingAsync(tweakId);
                return (true, "Removed");
            }

            string removeCmd;

            if (tweakId == "fan-control")
            {
                // Remove boot script and log file. On UCG-Fiber/UXG-Fiber, restore stock
                // PID setpoints via SDB (restarting the fan daemon does NOT clear them).
                // On UCG-Max we don't have confirmed stock values, so just remove and
                // inform the user to reboot.
                //
                // The restart must target whichever daemon owns the PID loop: uhwd on
                // 5.1.x and earlier, ufcd on 6.0.x. Restarting the wrong one leaves the
                // gateway running the tuned setpoints until its next reboot, so a
                // "successful" removal would silently fail to revert anything.
                var modelLower = (status?.GatewayModel ?? "").Replace("-", "").ToLowerInvariant();
                var canResetSdb = modelLower is "ucgfiber" or "uxgfiber";

                if (canResetSdb)
                {
                    var resetScript = """
                        import threading, time
                        from ustd.statusdb.sdb_client import SDBClient
                        c = SDBClient()
                        t = threading.Thread(target=c.run, daemon=True)
                        t.start()
                        time.sleep(1)
                        fan = c.get("config.fan")
                        pid = fan.get("PID", {})
                        stock = {"cpu": 100, "hdd": 68, "rtl8372": 109, "rtl8261": 103}
                        for k, v in stock.items():
                            if k in pid:
                                pid[k][0] = v
                        fan["standby"] = 20
                        c.update("config.fan", fan)
                        time.sleep(1)
                        """;
                    var resetB64 = GatewayFile.ToBase64(resetScript);
                    removeCmd = $"rm -f {OnBootDir}/{scriptName}; " +
                        $"echo '{resetB64}' | base64 -d | python3 2>/dev/null; " +
                        "systemctl restart \"$(systemctl cat ufcd.service >/dev/null 2>&1 && echo ufcd || echo uhwd)\" 2>/dev/null; " +
                        "rm -f /var/log/fan-control-tuning.log; echo 'removed'";
                }
                else
                {
                    removeCmd = $"rm -f {OnBootDir}/{scriptName}; " +
                        "rm -f /var/log/fan-control-tuning.log; echo 'removed_needs_reboot'";
                }
            }
            else if (tweakId == "journald-volatile")
            {
                // Restore journald.conf and syslog-ng routes, restart both services.
                // The overlay changes persist across reboots - just deleting the boot script
                // does NOT revert them.
                removeCmd =
                    $"rm -f {OnBootDir}/{scriptName}; " +
                    "sed -i 's/^Storage=volatile/Storage=persistent/' /etc/systemd/journald.conf 2>/dev/null; " +
                    "sed -i 's/^ForwardToSyslog=no/ForwardToSyslog=yes/' /etc/systemd/journald.conf 2>/dev/null; " +
                    "systemctl restart systemd-journald 2>/dev/null; " +
                    "sed -i 's/^#log /log /' /etc/syslog-ng/conf.d/*.conf 2>/dev/null; " +
                    "systemctl restart syslog-ng 2>/dev/null; " +
                    "echo 'removed'";
            }
            else if (tweakId == "sfp-sgmiiplus-port6")
            {
                removeCmd =
                    $"rm -f {OnBootDir}/{scriptName} && " +
                    "rmmod force_uniphy2_sgmiiplus 2>/dev/null; " +
                    $"rm -f {SfpModuleDir}/force_uniphy2_sgmiiplus.ko; " +
                    "rm -f /var/log/sfp-sgmiiplus-eth5.log; " +
                    "echo 'removed'";
            }
            else if (tweakId == "sfp-sgmiiplus")
            {
                removeCmd =
                    $"rm -f {OnBootDir}/{scriptName} && " +
                    "rmmod force_uniphy1_sgmiiplus 2>/dev/null; " +
                    $"rm -f {SfpModuleDir}/force_uniphy1_sgmiiplus.ko; " +
                    "rm -f /var/log/sfp-sgmiiplus.log; " +
                    "echo 'removed'";
            }
            else
            {
                removeCmd = $"rm -f {OnBootDir}/{scriptName} && echo 'removed'";
            }

            var result = await RunCommandAsync(removeCmd, TimeSpan.FromMinutes(5));

            // Clear manual flag
            await using var db = CreateSiteDb();
            var setting = await db.PerfTweakSettings.FirstOrDefaultAsync(s => s.TweakId == tweakId);
            if (setting != null)
            {
                db.PerfTweakSettings.Remove(setting);
                await db.SaveChangesAsync();
            }

            var removed = result.output.Contains("removed");
            if (result.output.Contains("removed_needs_reboot"))
                return (removed, "Removed. Reboot your gateway to restore stock fan settings.");
            return (removed, removed ? "Removed" : result.output);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove tweak {TweakId}", tweakId);
            return (false, ex.Message);
        }
    }

    public async Task SetManuallyDeployedAsync(string tweakId, bool isManual)
    {
        await using var db = CreateSiteDb();
        var setting = await db.PerfTweakSettings.FirstOrDefaultAsync(s => s.TweakId == tweakId);

        if (isManual)
        {
            if (setting == null)
            {
                setting = new PerfTweakSetting { TweakId = tweakId, IsManuallyDeployed = true };
                db.PerfTweakSettings.Add(setting);
            }
            else
            {
                setting.IsManuallyDeployed = true;
            }
        }
        else
        {
            if (setting != null)
            {
                db.PerfTweakSettings.Remove(setting);
            }
        }

        await db.SaveChangesAsync();
    }

    private async Task PersistDeployedStateAsync(string tweakId)
    {
        await using var db = CreateSiteDb();
        var existing = await db.PerfTweakSettings.FirstOrDefaultAsync(s => s.TweakId == tweakId);
        if (existing == null)
        {
            db.PerfTweakSettings.Add(new PerfTweakSetting { TweakId = tweakId, IsManuallyDeployed = false });
            await db.SaveChangesAsync();
        }
    }

    // TODO: depend on IUdmBootService directly instead of routing udm-boot install through
    // SqmDeploymentService. udm-boot is shared gateway boot infrastructure (see
    // NetworkOptimizer.Web.Services.Ssh.UdmBootService); this delegation chain
    // (PerfTweaks -> SQM -> UdmBootService) is incidental coupling.
    public async Task<(bool success, string message)> InstallUdmBootAsync()
    {
        return await _sqmDeployment.InstallUdmBootAsync();
    }

    private static string? ReadEmbeddedResource(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    private static byte[]? ReadEmbeddedResourceBytes(string fileName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null) return null;

        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static Dictionary<string, string> ParseDelimitedOutput(string output)
    {
        var sections = new Dictionary<string, string>();
        var lines = output.Split('\n');
        string? currentKey = null;
        var currentValue = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("---") && trimmed.EndsWith("---") && trimmed.Length > 6)
            {
                if (currentKey != null)
                    sections[currentKey] = string.Join("\n", currentValue);
                currentKey = trimmed.Trim('-');
                currentValue.Clear();
            }
            else if (currentKey != null)
            {
                currentValue.Add(line);
            }
        }

        if (currentKey != null)
            sections[currentKey] = string.Join("\n", currentValue);

        return sections;
    }

    private static string GetSection(Dictionary<string, string> sections, string key)
        => sections.TryGetValue(key, out var value) ? value : "";

    private static string FormatHexRegister(string raw)
    {
        if (string.IsNullOrEmpty(raw) || !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return raw;
        var hex = raw[2..].TrimStart('0');
        if (hex.Length == 0) hex = "0";
        return "0x" + hex;
    }

    /// <summary>
    /// PostgreSQL needs positive proof: the running app in PostgreSQL mode with the 14/apps cluster
    /// up and populated. MongoDB is a pre-11 Network or any other mode value. The rest is unknown.
    /// </summary>
    internal static DatabaseBackend ClassifyBackend(
        string networkVersion, string unifiActive, string dbMode, string pgAppsUnit, string pgNetworkTables)
    {
        if (dbMode == "postgresql" && unifiActive == "active" && pgAppsUnit == "active"
            && int.TryParse(pgNetworkTables, out var tables) && tables > 0)
            return DatabaseBackend.PostgreSql;

        var dot = networkVersion.IndexOf('.');
        if (dot > 0 && int.TryParse(networkVersion[..dot], out var major) && major < 11)
            return DatabaseBackend.MongoDb;
        if (dbMode.Length > 0 && dbMode != "postgresql")
            return DatabaseBackend.MongoDb;

        return DatabaseBackend.Indeterminate;
    }

    private static readonly TimeSpan BackupMaxAge = TimeSpan.FromHours(48);

    private static HealthCheckResult BackupHealth(bool configured, string completedAt, DateTime nowUtc)
    {
        if (!configured)
            return new("Backup", "Not configured", HealthCheckStatus.Warning);
        if (!DateTime.TryParse(completedAt, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var at))
            return new("Backup", "Active, no completed backup yet", HealthCheckStatus.Warning);
        var value = $"Active, last backup {at:yyyy-MM-dd HH:mm} UTC";
        return new("Backup", value, nowUtc - at > BackupMaxAge ? HealthCheckStatus.Warning : HealthCheckStatus.Ok);
    }

    private static readonly System.Text.RegularExpressions.Regex ResultField =
        new("""(\w+)=(?:"([^"]*)"|(\S+))""", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses the decommission script's last non-empty output line. A missing RESULT= line (timeout,
    /// lost session) or a nonzero exit is an error.
    /// </summary>
    internal static DecommissionResult ParseDecommissionResult(string output, bool exitedZero)
    {
        var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        if (!last.StartsWith("RESULT=", StringComparison.Ordinal))
            return new(false, null, null, 0, false, null, false, null, "The script ended without a RESULT line.", last);

        var f = new Dictionary<string, string>();
        foreach (System.Text.RegularExpressions.Match m in ResultField.Matches(last))
            f[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

        var ok = f.GetValueOrDefault("RESULT") == "ok" && exitedZero;
        return new(
            ok,
            f.GetValueOrDefault("mode"),
            f.GetValueOrDefault("copied"),
            long.TryParse(f.GetValueOrDefault("bytes"), out var bytes) ? bytes : 0,
            f.GetValueOrDefault("unclean") == "1",
            f.GetValueOrDefault("cleanup"),
            f.GetValueOrDefault("noop") == "1",
            int.TryParse(f.GetValueOrDefault("step"), out var step) ? step : null,
            f.GetValueOrDefault("reason") ?? (ok ? null : "The script exited nonzero."),
            last);
    }

    /// <summary>User-facing warnings for a successful decommission result.</summary>
    internal static List<string> DecommissionWarnings(DecommissionResult r)
    {
        var warnings = new List<string>();
        if (r.Unclean)
            warnings.Add("The newest MongoDB copy was not shut down cleanly. It was kept, and MongoDB recovers it from its journal on the next start.");
        if (r.Cleanup == "incomplete")
            warnings.Add("Some old MongoDB copies could not be deleted. Check the gateway's syslog (tag mongodb-ssd-decommission).");
        return warnings;
    }

    private static string Tail(string output, int lines = 15) =>
        string.Join("\n", output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(lines));

    private static string FormatLinkSpeed(string ethtoolSpeed)
    {
        var numeric = ethtoolSpeed.Replace("Mb/s", "").Trim();
        if (int.TryParse(numeric, out var mbps))
        {
            return mbps % 1000 == 0
                ? $"{mbps / 1000} Gbps"
                : $"{mbps / 1000.0:0.#} Gbps";
        }
        return ethtoolSpeed;
    }
}

public class PerfTweaksStatus
{
    public bool UdmBootInstalled { get; set; }
    public bool UdmBootEnabled { get; set; }
    public string? GatewayModel { get; set; }
    public bool IsSupportedGateway { get; set; }
    public string? FirmwareVersion { get; set; }
    public bool FirmwareSupported { get; set; }

    /// <summary>Verified firmware ceiling for this gateway's model, for the unsupported-firmware notice.</summary>
    public string? MaxSupportedFirmware { get; set; }
    public bool SsdAvailable { get; set; }
    public string? SsdMountPath { get; set; }
    public bool SfpModuleAlreadyLoaded { get; set; }
    public bool SfpPort6ModuleAlreadyLoaded { get; set; }
    public bool SfpQcaSsdkMissing { get; set; }

    /// <summary>Which database the UniFi Network app runs on; picks the MongoDB or PostgreSQL SSD card.</summary>
    public DatabaseBackend DatabaseBackend { get; set; } = DatabaseBackend.Indeterminate;

    /// <summary>Installed UniFi Network package version, or null when it could not be read.</summary>
    public string? NetworkVersion { get; set; }

    /// <summary>A mongod process is up. Network 11 can start one on demand, so this is a detail only.</summary>
    public bool MongodRunning { get; set; }

    /// <summary>Databases in the 14/apps cluster besides unifi-network; null when psql gave no answer.</summary>
    public int? PgOtherDatabases { get; set; }

    /// <summary>Anything 06/07 left behind: hooks, cron, helper, bind mount, or SSD copies.</summary>
    public bool MongoArtifactsPresent { get; set; }

    /// <summary>This app decommissioned the MongoDB SSD tweak on this site's gateway.</summary>
    public bool MongoDecommissioned { get; set; }

    public string? Error { get; set; }
    public Dictionary<string, TweakDeploymentStatus> Tweaks { get; set; } = new();

    /// <summary>
    /// The gateway runs on PostgreSQL with the MongoDB SSD tweak still deployed and the PostgreSQL
    /// SSD tweak not deployed, so the new tweak replaces the old one.
    /// </summary>
    public bool PostgresReplacementAvailable =>
        DatabaseBackend == DatabaseBackend.PostgreSql
        && Tweaks.GetValueOrDefault("mongodb-ssd") is { } mongo && (mongo.BootScriptDeployed || mongo.RuntimeDetected)
        && Tweaks.GetValueOrDefault("postgresql-ssd")?.BootScriptDeployed != true;
}

/// <summary>The database the UniFi Network app runs on.</summary>
public enum DatabaseBackend
{
    /// <summary>Network is upgrading, stopped on Network 11, or PostgreSQL did not answer.</summary>
    Indeterminate,
    MongoDb,
    PostgreSql
}

/// <summary>The last line of mongodb-ssd-decommission.sh, parsed.</summary>
public sealed record DecommissionResult(
    bool Ok, string? Mode, string? Copied, long Bytes, bool Unclean, string? Cleanup, bool Noop,
    int? Step, string? Reason, string Line);

public class TweakDeploymentStatus
{
    public string Id { get; set; } = "";
    public bool BootScriptDeployed { get; set; }
    public bool RuntimeDetected { get; set; }
    public bool IsActive { get; set; }
    public bool IsManuallyDeployed { get; set; }
    public bool ScriptOutdated { get; set; }
    public string? IssueDescription { get; set; }
    public List<HealthCheckResult> HealthChecks { get; set; } = new();
}

public record HealthCheckResult(string Label, string Value, HealthCheckStatus Status);

public enum HealthCheckStatus { Ok, Warning, Error }
