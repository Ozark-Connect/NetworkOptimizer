using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Ssh;
using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Web.Services;

public class PerfTweaksDeploymentService
{
    private readonly ILogger<PerfTweaksDeploymentService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly SqmDeploymentService _sqmDeployment;

    private const string OnBootDir = "/data/on_boot.d";
    private const string PerfTweaksDir = "/data/perf-tweaks";
    private const string SfpModuleDir = "/data/sfp-sgmiiplus";
    private static readonly Version MaxSupportedFirmware = new(5, 1, 10);

    private static readonly Dictionary<string, string> BootScriptFiles = new()
    {
        ["fan-control"] = "15-fan-control-tuning.sh",
        ["mongodb-ssd"] = "06-mongodb-ssd-offload.sh",
        ["mongodb-backup"] = "07-mongodb-ssd-backup.sh",
        ["journald-volatile"] = "10-journald-volatile.sh",
        ["sfp-sgmiiplus"] = "20-sfp-sgmiiplus.sh"
    };

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
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        SqmDeploymentService sqmDeployment)
    {
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _dbFactory = dbFactory;
        _sqmDeployment = sqmDeployment;
    }

    private Task<(bool success, string output)> RunCommandAsync(string command, TimeSpan? timeout = null)
        => _gatewaySsh.RunCommandAsync(command, timeout);

    public async Task<PerfTweaksStatus> CheckAllStatusAsync()
    {
        var status = new PerfTweaksStatus();

        try
        {
            var combinedCommand =
                // UDM boot
                "echo '---UDM_BOOT_CHECK---'; test -f /etc/systemd/system/udm-boot.service && echo 'installed' || echo 'missing'; " +
                "echo '---UDM_BOOT_ENABLED---'; systemctl is-enabled udm-boot 2>/dev/null || echo 'disabled'; " +
                // Gateway model and firmware
                "echo '---GATEWAY_MODEL---'; ubnt-device-info model_short 2>/dev/null || (grep -i '^shortname=' /proc/ubnthal/system.info 2>/dev/null | cut -d= -f2-) || echo 'unknown'; echo; " +
                "echo '---FIRMWARE_VERSION---'; ubnt-device-info firmware 2>/dev/null || (grep -i '^version=' /etc/os-release 2>/dev/null | cut -d= -f2- | tr -d '\"') || echo 'unknown'; echo; " +
                // Boot script hashes (for version checking)
                $"echo '---SCRIPT_HASHES---'; for s in 15-fan-control-tuning.sh 06-mongodb-ssd-offload.sh 07-mongodb-ssd-backup.sh 10-journald-volatile.sh 20-sfp-sgmiiplus.sh; do [ -f {OnBootDir}/$s ] && echo \"$s:$(md5sum {OnBootDir}/$s | cut -d' ' -f1)\"; done; " +
                // Fan control
                $"echo '---FAN_BOOT_SCRIPT---'; test -f {OnBootDir}/15-fan-control-tuning.sh && echo 'exists' || echo 'missing'; " +
                "echo '---FAN_PWM---'; cat /sys/class/hwmon/hwmon0/pwm1 2>/dev/null || echo 'N/A'; " +
                "echo '---FAN_RPM---'; cat /sys/class/hwmon/hwmon0/fan1_input 2>/dev/null || echo 'N/A'; " +
                "echo '---FAN_TEMPS---'; for f in /sys/class/hwmon/hwmon0/temp*_input; do [ -f \"$f\" ] && echo \"$(basename $f .input):$(($(cat $f)/1000))\"; done; " +
                "echo '---CPU_DIE_TEMP---'; cat /sys/class/thermal/thermal_zone*/temp 2>/dev/null | sort -n | tail -1 | awk '{printf \"%d\", $1/1000}'; echo; " +
                "echo '---FAN_LOG---'; tail -3 /var/log/fan-control-tuning.log 2>/dev/null || echo 'no log'; " +
                "echo '---UHWD_STATUS---'; systemctl is-active uhwd 2>/dev/null || echo 'inactive'; " +
                // SSD availability (for MongoDB SSD tweak gating)
                "echo '---SSD_VOLUME---'; (mountpoint -q /volume1 2>/dev/null && echo '/volume1') || (for d in /volume/*/; do [ -d \"$d\" ] && mountpoint -q \"${d%/}\" 2>/dev/null && echo \"${d%/}\" && break; done) || echo 'none'; " +
                // MongoDB SSD
                $"echo '---MONGO_BOOT_SCRIPT---'; test -f {OnBootDir}/06-mongodb-ssd-offload.sh && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_MOUNTPOINT---'; mountpoint -q /data/unifi/data/db 2>/dev/null && echo 'mounted' || echo 'not-mounted'; " +
                "echo '---MONGO_FINDMNT---'; findmnt -no SOURCE /data/unifi/data/db 2>/dev/null || echo 'N/A'; " +
                "echo '---MONGO_SERVICE---'; systemctl is-active unifi-mongodb 2>/dev/null || echo 'inactive'; " +
                "echo '---MONGO_SSD_SIZE---'; du -sh /volume*/unifi-db 2>/dev/null | head -1 | cut -f1 || echo 'N/A'; " +
                // MongoDB backup
                $"echo '---MONGO_BACKUP_SCRIPT---'; test -f {OnBootDir}/07-mongodb-ssd-backup.sh && echo 'exists' || echo 'missing'; " +
                "echo '---MONGO_BACKUP_CRON---'; test -f /etc/cron.d/mongodb-ssd-backup && echo 'exists' || echo 'missing'; " +
                // Journald volatile
                $"echo '---JOURNALD_BOOT_SCRIPT---'; test -f {OnBootDir}/10-journald-volatile.sh && echo 'exists' || echo 'missing'; " +
                "echo '---JOURNALD_STORAGE---'; grep '^Storage=' /etc/systemd/journald.conf 2>/dev/null | cut -d= -f2 || echo 'N/A'; " +
                "echo '---JOURNALD_FWD---'; grep '^ForwardToSyslog=' /etc/systemd/journald.conf 2>/dev/null | cut -d= -f2 || echo 'N/A'; " +
                "echo '---SYSLOG_EMMC_ROUTES---'; DESTS=$(grep -rh '^destination .* file(\"/var/log' /etc/syslog-ng/conf.d/*.conf 2>/dev/null | grep -v '/var/log/ulog' | sed -n 's/^destination \\([^ ]*\\) .*/\\1/p'); F=0; for d in $DESTS; do F=$((F+$(grep -rc \"^log.*destination($d)\" /etc/syslog-ng/conf.d/*.conf 2>/dev/null | cut -d: -f2 | awk '{s+=$1}END{print s+0}'))); done; echo $F; " +
                "echo '---THREAT_LOG_ROUTE---'; grep -c '^log.*d_idsips_threat' /etc/syslog-ng/conf.d/threat_log.conf 2>/dev/null || echo '0'; " +
                // SFP SGMII+
                $"echo '---SFP_BOOT_SCRIPT---'; test -f {OnBootDir}/20-sfp-sgmiiplus.sh && echo 'exists' || echo 'missing'; " +
                $"echo '---SFP_MODULE_FILE---'; test -f {SfpModuleDir}/force_uniphy1_sgmiiplus.ko && echo 'exists' || echo 'missing'; " +
                "echo '---SFP_QCA_SSDK---'; lsmod | grep -q qca_ssdk && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SFP_MODULE_LOADED---'; lsmod | grep -q force_uniphy1_sgmiiplus && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SFP_CLOCK_RATE---'; cat /sys/kernel/debug/clk/uniphy1_gcc_tx_clk/clk_rate 2>/dev/null || echo 'N/A'; " +
                "echo '---SFP_SERDES_REG---'; busybox devmem 0x07A10218 32 2>/dev/null || echo 'N/A'; " +
                "echo '---SFP_ETH6_SPEED---'; ethtool eth6 2>/dev/null | grep Speed | awk '{print $2}' || echo 'N/A'; " +
                "echo '---SFP_LOG---'; tail -3 /var/log/sfp-sgmiiplus.log 2>/dev/null || echo 'no log'";

            var result = await RunCommandAsync(combinedCommand, TimeSpan.FromSeconds(15));
            if (!result.success)
            {
                status.Error = result.output;
                return status;
            }

            var sections = ParseDelimitedOutput(result.output);

            // UDM Boot
            status.UdmBootInstalled = GetSection(sections, "UDM_BOOT_CHECK").Contains("installed");
            status.UdmBootEnabled = GetSection(sections, "UDM_BOOT_ENABLED").Trim() == "enabled";

            // Gateway model - format via product DB for canonical SKU names
            var rawModel = GetSection(sections, "GATEWAY_MODEL").Trim();
            status.GatewayModel = UniFiProductDatabase.GetProductNameFromShortname(rawModel);
            var modelLower = rawModel.ToLowerInvariant();
            status.IsSupportedGateway = modelLower is "ucg-fiber" or "ucgf" or "ucgfiber"
                or "uxg-fiber" or "uxgfiber"
                or "ucg-max" or "ucgmax";

            // Firmware version
            var fwRaw = GetSection(sections, "FIRMWARE_VERSION").Trim();
            status.FirmwareVersion = fwRaw;
            if (Version.TryParse(fwRaw, out var fwVersion))
                status.FirmwareSupported = fwVersion <= MaxSupportedFirmware;
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
                var uhwdActive = GetSection(sections, "UHWD_STATUS").Trim() == "active";
                fanStatus.HealthChecks.Add(new("Fan Speed", rpm != "N/A" ? $"{rpm} RPM (PWM {pwm})" : "N/A", uhwdActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

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

                fanStatus.HealthChecks.Add(new("uhwd Service", uhwdActive ? "Running" : "Not running", uhwdActive ? HealthCheckStatus.Ok : HealthCheckStatus.Error));

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

            // SFP SGMII+
            var sfpStatus = new TweakDeploymentStatus { Id = "sfp-sgmiiplus" };
            sfpStatus.BootScriptDeployed = GetSection(sections, "SFP_BOOT_SCRIPT").Contains("exists");
            var sfpModuleExists = GetSection(sections, "SFP_MODULE_FILE").Contains("exists");
            var sfpQcaSsdkLoaded = GetSection(sections, "SFP_QCA_SSDK").Trim() == "loaded";
            var sfpModuleLoaded = GetSection(sections, "SFP_MODULE_LOADED").Trim() == "loaded";
            var clockRate = GetSection(sections, "SFP_CLOCK_RATE").Trim();
            var serdesReg = GetSection(sections, "SFP_SERDES_REG").Trim().ToLowerInvariant();
            var ethSpeed = GetSection(sections, "SFP_ETH6_SPEED").Trim();
            status.SfpModuleAlreadyLoaded = sfpModuleLoaded;
            status.SfpQcaSsdkMissing = !sfpQcaSsdkLoaded;
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

                var scriptName = BootScriptFiles.GetValueOrDefault(tweakId);
                if (scriptName == null) continue;

                if (remoteHashes.TryGetValue(scriptName, out var remoteHash) &&
                    ExpectedHashes.Value.TryGetValue(scriptName, out var expectedHash))
                {
                    if (remoteHash != expectedHash)
                    {
                        tweak.ScriptOutdated = true;
                        tweak.HealthChecks.Add(new("Boot Script", "Update available", HealthCheckStatus.Warning));
                    }
                }
            }

            // Load manually-deployed state from DB and adjust health checks
            await using var db = await _dbFactory.CreateDbContextAsync();
            var manualTweaks = await db.PerfTweakSettings.ToListAsync();
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking performance tweaks status");
            status.Error = ex.Message;
        }

        return status;
    }

    public async Task<(bool success, string message, List<string> steps)> DeployTweakAsync(
        string tweakId, IProgress<string>? progress = null)
    {
        var steps = new List<string>();
        void Report(string step) { steps.Add(step); progress?.Report(step); }

        try
        {
            if (tweakId == "sfp-sgmiiplus")
            {
                return await DeploySfpTweakAsync(progress);
            }

            var scriptName = BootScriptFiles.GetValueOrDefault(tweakId);
            if (scriptName == null)
                return (false, $"Unknown tweak: {tweakId}", steps);

            var scriptContent = ReadEmbeddedResource(scriptName);
            if (scriptContent == null)
                return (false, $"Embedded resource not found: {scriptName}", steps);

            Report($"Deploying {scriptName}...");
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptContent));
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
                    var b64Backup = Convert.ToBase64String(Encoding.UTF8.GetBytes(backupContent));
                    var backupCmd = $"echo '{b64Backup}' | base64 -d > {OnBootDir}/{backupName} && chmod +x {OnBootDir}/{backupName} && echo 'deployed'";
                    await RunCommandAsync(backupCmd);
                }
            }

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

            return (true, "Deployed successfully", steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy tweak {TweakId}", tweakId);
            return (false, ex.Message, steps);
        }
    }

    private async Task<(bool success, string message, List<string> steps)> DeploySfpTweakAsync(
        IProgress<string>? progress = null)
    {
        var steps = new List<string>();
        void Report(string step) { steps.Add(step); progress?.Report(step); }

        try
        {
            // Check dependencies
            Report("Checking prerequisites...");
            var checkResult = await RunCommandAsync(
                "echo '---MODULE---'; lsmod | grep -q force_uniphy1_sgmiiplus && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---SSDK---'; lsmod | grep -q qca_ssdk && echo 'loaded' || echo 'not-loaded'");
            var checkSections = ParseDelimitedOutput(checkResult.output);

            if (GetSection(checkSections, "MODULE").Trim() == "loaded")
                return (false, "SFP module is already loaded. Use 'Mark as Manually Deployed' for monitoring.", steps);

            if (GetSection(checkSections, "SSDK").Trim() != "loaded")
                return (false, "qca-ssdk kernel module is not loaded. This is a required dependency for the SFP SGMII+ patch.", steps);

            // Deploy kernel module
            Report("Deploying kernel module to /data/sfp-sgmiiplus/...");
            var koBytes = ReadEmbeddedResourceBytes("force_uniphy1_sgmiiplus.ko");
            if (koBytes == null)
                return (false, "Kernel module not found in embedded resources", steps);

            var b64Ko = Convert.ToBase64String(koBytes);
            var koCmd = $"mkdir -p {SfpModuleDir} && echo '{b64Ko}' | base64 -d > {SfpModuleDir}/force_uniphy1_sgmiiplus.ko && echo 'deployed'";
            var koResult = await RunCommandAsync(koCmd);
            if (!koResult.success || !koResult.output.Contains("deployed"))
                return (false, $"Failed to deploy kernel module: {koResult.output}", steps);

            // Deploy boot script
            var scriptName = BootScriptFiles["sfp-sgmiiplus"];
            var scriptContent = ReadEmbeddedResource(scriptName);
            if (scriptContent == null)
                return (false, $"Boot script not found: {scriptName}", steps);

            Report($"Deploying {scriptName}...");
            var b64Script = Convert.ToBase64String(Encoding.UTF8.GetBytes(scriptContent));
            var scriptCmd = $"echo '{b64Script}' | base64 -d > {OnBootDir}/{scriptName} && chmod +x {OnBootDir}/{scriptName} && echo 'deployed'";
            var scriptResult = await RunCommandAsync(scriptCmd);
            if (!scriptResult.success || !scriptResult.output.Contains("deployed"))
                return (false, $"Failed to deploy boot script: {scriptResult.output}", steps);

            Report("Loading kernel module...");
            var loadResult = await RunCommandAsync($"{OnBootDir}/{scriptName} 2>&1", TimeSpan.FromSeconds(30));

            Report("Verifying...");
            var verifyResult = await RunCommandAsync(
                "echo '---MOD---'; lsmod | grep -q force_uniphy1_sgmiiplus && echo 'loaded' || echo 'not-loaded'; " +
                "echo '---CLK---'; cat /sys/kernel/debug/clk/uniphy1_gcc_tx_clk/clk_rate 2>/dev/null || echo 'N/A'");
            var verifySections = ParseDelimitedOutput(verifyResult.output);
            var modLoaded = GetSection(verifySections, "MOD").Trim() == "loaded";
            var clkOk = GetSection(verifySections, "CLK").Trim() == "312500000";

            if (modLoaded && clkOk)
                Report("Verified: Module loaded, uniphy1 at 312.5 MHz (2.5 Gbps).");
            else if (modLoaded)
                Report("Module loaded but clock rate not at expected value. Check logs.");
            else
                Report("Warning: Module may not have loaded correctly. Check /var/log/sfp-sgmiiplus.log");

            Report("Done.");
            return (true, "SFP SGMII+ patch deployed", steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy SFP tweak");
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

            string removeCmd;

            if (tweakId == "fan-control")
            {
                // Remove boot script and log file. On UCG-Fiber/UXG-Fiber, restore stock
                // PID setpoints via SDB (just restarting uhwd does NOT clear them).
                // On UCG-Max we don't have confirmed stock values, so just remove and
                // inform the user to reboot.
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
                    var resetB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(resetScript));
                    removeCmd = $"rm -f {OnBootDir}/{scriptName}; " +
                        $"echo '{resetB64}' | base64 -d | python3 2>/dev/null; " +
                        "systemctl restart uhwd 2>/dev/null; " +
                        "rm -f /var/log/fan-control-tuning.log; echo 'removed'";
                }
                else
                {
                    removeCmd = $"rm -f {OnBootDir}/{scriptName}; " +
                        "rm -f /var/log/fan-control-tuning.log; echo 'removed_needs_reboot'";
                }
            }
            else if (tweakId == "mongodb-ssd")
            {
                // Clean removal: stop MongoDB cleanly (WiredTiger shutdown), copy SSD
                // data back to eMMC so the next boot has current data, unmount, clean up.
                // systemctl stop unifi-mongodb.service cascades to stop unifi and does
                // a clean mongod --shutdown (WiredTiger journal flush).
                removeCmd =
                    "systemctl stop unifi 2>/dev/null; " +
                    "systemctl stop unifi-mongodb.service 2>/dev/null; " +
                    "i=0; while pgrep -x mongod >/dev/null 2>&1 && [ $i -lt 30 ]; do sleep 1; i=$((i+1)); done; " +
                    "SSD_DB=''; " +
                    "for d in /volume1/unifi-db /volume/*/unifi-db; do " +
                    "  [ -d \"$d\" ] && [ -f \"$d/WiredTiger\" ] && SSD_DB=\"$d\" && break; " +
                    "done; " +
                    "if mountpoint -q /data/unifi/data/db 2>/dev/null; then " +
                    "  umount /data/unifi/data/db; " +
                    "fi; " +
                    "if [ -n \"$SSD_DB\" ]; then " +
                    "  cp -a \"$SSD_DB\"/* /data/unifi/data/db/ 2>/dev/null; " +
                    "fi; " +
                    $"rm -f {OnBootDir}/06-mongodb-ssd-offload.sh; " +
                    $"rm -f {OnBootDir}/07-mongodb-ssd-backup.sh; " +
                    "rm -f /etc/cron.d/mongodb-ssd-backup; " +
                    "rm -rf /data/unifi-db-ssd; " +
                    "systemctl start unifi 2>/dev/null; " +
                    "echo 'removed'";
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
            else if (tweakId == "sfp-sgmiiplus")
            {
                removeCmd =
                    $"rm -f {OnBootDir}/{scriptName} && " +
                    "rmmod force_uniphy1_sgmiiplus 2>/dev/null; " +
                    $"rm -rf {SfpModuleDir}; " +
                    "rm -f /var/log/sfp-sgmiiplus.log; " +
                    "echo 'removed'";
            }
            else
            {
                removeCmd = $"rm -f {OnBootDir}/{scriptName} && echo 'removed'";
            }

            var result = await RunCommandAsync(removeCmd, TimeSpan.FromMinutes(5));

            // Clear manual flag
            await using var db = await _dbFactory.CreateDbContextAsync();
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
        await using var db = await _dbFactory.CreateDbContextAsync();
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
        return reader.ReadToEnd();
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
    public bool SsdAvailable { get; set; }
    public string? SsdMountPath { get; set; }
    public bool SfpModuleAlreadyLoaded { get; set; }
    public bool SfpQcaSsdkMissing { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, TweakDeploymentStatus> Tweaks { get; set; } = new();
}

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
