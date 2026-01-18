using System.Text;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Ssh;
using SqmConfig = NetworkOptimizer.Sqm.Models.SqmConfiguration;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for deploying SQM scripts to UniFi gateways via SSH.
/// Uses IGatewaySshService for gateway SSH operations.
/// </summary>
public class SqmDeploymentService : ISqmDeploymentService
{
    private readonly ILogger<SqmDeploymentService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IServiceProvider _serviceProvider;

    // Gateway paths
    private const string OnBootDir = "/data/on_boot.d";
    private const string SqmDir = "/data/sqm";

    public SqmDeploymentService(
        ILogger<SqmDeploymentService> logger,
        IGatewaySshService gatewaySsh,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Get gateway SSH settings
    /// </summary>
    private Task<GatewaySshSettings> GetGatewaySettingsAsync(int siteId)
        => _gatewaySsh.GetSettingsAsync(siteId);

    /// <summary>
    /// Run SSH command on gateway (shorthand)
    /// </summary>
    private Task<(bool success, string output)> RunCommandAsync(int siteId, string command)
        => _gatewaySsh.RunCommandAsync(siteId, command);

    /// <summary>
    /// Test SSH connection to the gateway
    /// </summary>
    public Task<(bool success, string message)> TestConnectionAsync(int siteId)
        => _gatewaySsh.TestConnectionAsync(siteId);

    /// <summary>
    /// Install udm-boot package on the gateway.
    /// This enables scripts in /data/on_boot.d/ to run automatically on boot
    /// and persist across firmware updates.
    /// </summary>
    public async Task<(bool success, string message)> InstallUdmBootAsync(int siteId)
    {
        var settings = await GetGatewaySettingsAsync(siteId);

        try
        {
            _logger.LogInformation("Installing udm-boot on gateway {Host}", settings.Host);

            // Create the udm-boot service file directly (works on all UDM/UCG devices)
            // This matches the upstream unifios-utilities version exactly
            // Note: In C# verbatim strings, "" produces a single ". The bash escape pattern '"'"'
            // (end single quote, double-quoted single quote, resume single quote) is written as '""'""'
            var serviceContent = @"[Unit]
Description=Run On Startup UDM 2.x and above
Wants=network-online.target
After=network-online.target
StartLimitIntervalSec=500
StartLimitBurst=1

[Service]
Type=oneshot
ExecStart=bash -c 'mkdir -p /data/on_boot.d && find -L /data/on_boot.d -mindepth 1 -maxdepth 1 -type f -print0 | sort -z | xargs -0 -r -n 1 -- sh -c '""'""'if test -x ""$0""; then echo ""%n: running $0""; ""$0""; else case ""$0"" in *.sh) echo ""%n: sourcing $0""; . ""$0"";; *) echo ""%n: ignoring $0"";; esac; fi'""'""''
RemainAfterExit=true

[Install]
WantedBy=multi-user.target
";

            // Use base64 encoding to avoid all shell quoting issues when transferring via SSH
            var base64Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(serviceContent));

            // Write service file via base64 decode, enable and start
            // Use --no-block so we don't wait for boot scripts to finish (they can take a while)
            var installCmd = $"echo {base64Content} | base64 -d > /etc/systemd/system/udm-boot.service && " +
                "mkdir -p /data/on_boot.d && " +
                "systemctl daemon-reload && " +
                "systemctl enable udm-boot && " +
                "systemctl start --no-block udm-boot && " +
                "echo udm-boot_installed_successfully";
            var result = await RunCommandAsync(siteId, installCmd);

            if (result.success && result.output.Contains("udm-boot_installed_successfully"))
            {
                _logger.LogInformation("udm-boot installed successfully on {Host}", settings.Host);
                return (true, "udm-boot installed successfully. Scripts in /data/on_boot.d/ will now run on boot.");
            }
            else
            {
                _logger.LogError("udm-boot installation failed: {Output}", result.output);
                return (false, $"Installation failed: {result.output}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install udm-boot");
            return (false, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if SQM scripts are already deployed
    /// </summary>
    public async Task<SqmDeploymentStatus> CheckDeploymentStatusAsync(int siteId)
    {
        var status = new SqmDeploymentStatus();

        try
        {
            // Launch SSH checks in batches of 4 to avoid overwhelming SSH server
            // Batch 1: udm-boot and script existence checks
            var udmBootCheckTask = RunCommandAsync(siteId,
                "test -f /etc/systemd/system/udm-boot.service && echo 'installed' || echo 'missing'");
            var udmBootEnabledTask = RunCommandAsync(siteId,
                "systemctl is-enabled udm-boot 2>/dev/null || echo 'disabled'");
            var sqmBootCheckTask = RunCommandAsync(siteId,
                $"ls {OnBootDir}/20-sqm-*.sh 2>/dev/null | grep -v 'sqm-monitor' | wc -l");
            var sqmScriptsCheckTask = RunCommandAsync(siteId,
                $"ls {SqmDir}/*-speedtest.sh 2>/dev/null | wc -l");

            await Task.WhenAll(udmBootCheckTask, udmBootEnabledTask, sqmBootCheckTask, sqmScriptsCheckTask);

            // Batch 2: monitor and service status checks
            var sqmMonitorCheckTask = RunCommandAsync(siteId,
                $"test -f {OnBootDir}/20-sqm-monitor.sh && echo 'exists' || echo 'missing'");
            var sqmMonitorRunningTask = RunCommandAsync(siteId,
                "systemctl is-active sqm-monitor 2>/dev/null || echo 'inactive'");
            var watchdogRunningTask = RunCommandAsync(siteId,
                "systemctl is-active sqm-monitor-watchdog.timer 2>/dev/null || echo 'inactive'");
            var cronCheckTask = RunCommandAsync(siteId,
                "crontab -l 2>/dev/null | grep -c sqm || echo '0'");

            await Task.WhenAll(sqmMonitorCheckTask, sqmMonitorRunningTask, watchdogRunningTask, cronCheckTask);

            // Batch 3: dependency checks
            var speedtestCliCheckTask = RunCommandAsync(siteId,
                "which speedtest >/dev/null 2>&1 && echo 'installed' || echo 'missing'");
            var bcCheckTask = RunCommandAsync(siteId,
                "which bc >/dev/null 2>&1 && echo 'installed' || echo 'missing'");

            await Task.WhenAll(speedtestCliCheckTask, bcCheckTask);

            // Process results
            var udmBootCheck = udmBootCheckTask.Result;
            status.UdmBootInstalled = udmBootCheck.success && udmBootCheck.output.Contains("installed");

            var udmBootEnabled = udmBootEnabledTask.Result;
            status.UdmBootEnabled = udmBootEnabled.success && udmBootEnabled.output.Trim() == "enabled";

            var sqmBootCheck = sqmBootCheckTask.Result;
            if (sqmBootCheck.success && int.TryParse(sqmBootCheck.output.Trim(), out int bootScriptCount))
            {
                status.SpeedtestScriptDeployed = bootScriptCount > 0;
                status.PingScriptDeployed = bootScriptCount > 0; // Both are in the same boot script now
            }

            var sqmScriptsCheck = sqmScriptsCheckTask.Result;
            if (sqmScriptsCheck.success && int.TryParse(sqmScriptsCheck.output.Trim(), out int sqmScriptCount))
            {
                status.SpeedtestScriptDeployed = status.SpeedtestScriptDeployed || sqmScriptCount > 0;
            }

            var sqmMonitorCheck = sqmMonitorCheckTask.Result;
            status.TcMonitorDeployed = sqmMonitorCheck.success && sqmMonitorCheck.output.Contains("exists");

            var sqmMonitorRunning = sqmMonitorRunningTask.Result;
            status.TcMonitorRunning = sqmMonitorRunning.success && sqmMonitorRunning.output.Trim() == "active";

            var watchdogRunning = watchdogRunningTask.Result;
            status.WatchdogTimerRunning = watchdogRunning.success && watchdogRunning.output.Trim() == "active";

            var cronCheck = cronCheckTask.Result;
            if (cronCheck.success && int.TryParse(cronCheck.output.Trim(), out int cronCount))
            {
                status.CronJobsConfigured = cronCount;
            }

            var speedtestCliCheck = speedtestCliCheckTask.Result;
            status.SpeedtestCliInstalled = speedtestCliCheck.success && speedtestCliCheck.output.Contains("installed");

            var bcCheck = bcCheckTask.Result;
            status.BcInstalled = bcCheck.success && bcCheck.output.Contains("installed");

            status.IsDeployed = status.SpeedtestScriptDeployed && status.PingScriptDeployed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking SQM deployment status");
            status.Error = ex.Message;
        }

        return status;
    }

    /// <summary>
    /// Clean ALL SQM scripts and cron entries from the gateway.
    /// Call this ONCE before deploying any WANs to handle renamed connections.
    /// </summary>
    public async Task<(bool success, string message)> CleanAllSqmScriptsAsync(int siteId)
    {
        try
        {
            _logger.LogInformation("Cleaning all SQM scripts and cron entries");

            // Remove all SQM boot scripts
            await RunCommandAsync(siteId,
                $"rm -f {OnBootDir}/20-sqm-*.sh {OnBootDir}/21-sqm-*.sh");

            // Remove all SQM data directories
            await RunCommandAsync(siteId,
                $"rm -rf {SqmDir}/*");

            // Remove ALL SQM-related cron entries (catches renamed connections)
            await RunCommandAsync(siteId,
                "crontab -l 2>/dev/null | grep -v -E 'sqm|SQM' | crontab -");

            _logger.LogInformation("Successfully cleaned all SQM scripts and cron entries");
            return (true, "Cleaned all SQM scripts and cron entries");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning SQM scripts");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Lightweight cleanup after a failed deployment.
    /// Removes only the specific WAN's scripts and cron entries, not the full SQM removal.
    /// </summary>
    private async Task CleanupFailedDeploymentAsync(int siteId, string? connectionName, string interfaceName)
    {
        try
        {
            var safeName = Sqm.InputSanitizer.SanitizeConnectionName(connectionName ?? interfaceName);
            _logger.LogInformation("Cleaning up failed deployment for {Name} ({Interface})", connectionName, interfaceName);

            // Remove the boot script for this specific WAN
            await RunCommandAsync(siteId, $"rm -f {OnBootDir}/20-sqm-{safeName}.sh");

            // Remove SQM data directory for this WAN
            await RunCommandAsync(siteId, $"rm -rf {SqmDir}/{safeName}-*.sh {SqmDir}/{safeName}-*.txt");

            // Remove cron entries for this specific WAN (match on connection name)
            await RunCommandAsync(siteId,
                $"crontab -l 2>/dev/null | grep -v '{safeName}' | crontab -");

            _logger.LogInformation("Cleanup completed for {Name}", connectionName);
        }
        catch (Exception ex)
        {
            // Log but don't throw - cleanup is best-effort
            _logger.LogWarning(ex, "Error during cleanup of failed deployment for {Name}", connectionName);
        }
    }

    /// <summary>
    /// Lightweight cleanup after a failed SQM Monitor deployment.
    /// </summary>
    private async Task CleanupFailedSqmMonitorAsync(int siteId)
    {
        try
        {
            _logger.LogInformation("Cleaning up failed SQM Monitor deployment");

            // Remove the boot script
            await RunCommandAsync(siteId, $"rm -f {OnBootDir}/20-sqm-monitor.sh");

            // Stop and disable any partially-created services
            await RunCommandAsync(siteId,
                "systemctl stop sqm-monitor-watchdog.timer sqm-monitor 2>/dev/null; " +
                "systemctl disable sqm-monitor-watchdog.timer sqm-monitor 2>/dev/null");

            // Remove service files and monitor directory
            await RunCommandAsync(siteId, "rm -rf /data/sqm-monitor");
            await RunCommandAsync(siteId,
                "rm -f /etc/systemd/system/sqm-monitor.service " +
                "/etc/systemd/system/sqm-monitor-watchdog.timer /etc/systemd/system/sqm-monitor-watchdog.service");
            await RunCommandAsync(siteId, "systemctl daemon-reload");

            _logger.LogInformation("SQM Monitor cleanup completed");
        }
        catch (Exception ex)
        {
            // Log but don't throw - cleanup is best-effort
            _logger.LogWarning(ex, "Error during cleanup of failed SQM Monitor deployment");
        }
    }

    /// <summary>
    /// Deploy SQM scripts to the gateway
    /// </summary>
    /// <param name="siteId">The site identifier</param>
    /// <param name="config">SQM configuration for this WAN</param>
    /// <param name="baseline">Optional hourly baseline data</param>
    /// <param name="initialDelaySeconds">Delay before first speedtest (default 60s, use higher values for additional WANs to stagger)</param>
    public async Task<SqmDeploymentResult> DeployAsync(int siteId, SqmConfig config, Dictionary<string, string>? baseline = null, int initialDelaySeconds = 60)
    {
        var result = new SqmDeploymentResult();
        var steps = new List<string>();

        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            result.Success = false;
            result.Error = "Gateway SSH not configured";
            return result;
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            // Apply profile-based settings
            config.ApplyProfileSettings();

            // Security: Validate all inputs before script generation to prevent command injection.
            // This is defense-in-depth - the UI also validates before calling DeployAsync,
            // but we validate again here to protect against direct API calls or code changes.
            var manager = new SqmManager(config);
            var validationErrors = manager.ValidateConfiguration();
            if (validationErrors.Count > 0)
            {
                result.Success = false;
                result.Error = $"Configuration validation failed: {string.Join("; ", validationErrors)}";
                _logger.LogWarning("SQM deployment blocked due to validation errors: {Errors}", validationErrors);
                return result;
            }
            _logger.LogInformation("Deploying SQM with config: {Summary}", config.GetParameterSummary());

            // Step 1: Create directories
            steps.Add("Creating directories...");
            var mkdirResult = await RunCommandAsync(siteId,
                $"mkdir -p {OnBootDir} {SqmDir}");
            if (!mkdirResult.success)
            {
                throw new Exception($"Failed to create directories: {mkdirResult.output}");
            }

            // Step 2: Generate the self-contained boot script
            steps.Add("Generating SQM boot script...");
            var generator = new ScriptGenerator(config, initialDelaySeconds);
            baseline ??= GenerateDefaultBaseline(config);
            var scripts = generator.GenerateAllScripts(baseline);
            var bootScriptName = generator.GetBootScriptName();

            // Step 3: Deploy the boot script
            foreach (var (filename, content) in scripts)
            {
                steps.Add($"Deploying {filename}...");
                var success = await DeployScriptAsync(siteId, filename, content);
                if (!success)
                {
                    throw new Exception($"Failed to deploy {filename}");
                }
            }

            // Step 4: Run the boot script to set up everything
            steps.Add("Running boot script (installs deps, creates scripts, configures cron)...");
            var setupResult = await RunCommandAsync(siteId,
                $"chmod +x {OnBootDir}/{bootScriptName} && {OnBootDir}/{bootScriptName}");

            if (!setupResult.success)
            {
                // Log detailed output for debugging
                _logger.LogWarning(
                    "Boot script execution failed for {Name} ({Interface}). Output: {Output}",
                    config.ConnectionName, config.Interface, setupResult.output);

                // Add truncated output to steps for UI visibility
                if (!string.IsNullOrWhiteSpace(setupResult.output))
                {
                    var truncatedOutput = setupResult.output.Length > 300
                        ? setupResult.output[..300] + "..."
                        : setupResult.output;
                    _logger.LogWarning("Boot script output (truncated): {Output}", truncatedOutput);

                    // Split output into lines for better UI display
                    var outputLines = truncatedOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in outputLines.Take(10)) // Limit to 10 lines
                    {
                        steps.Add($"  {line.Trim()}");
                    }
                    if (outputLines.Length > 10)
                    {
                        steps.Add($"  ... ({outputLines.Length - 10} more lines)");
                    }
                }

                // Clean up the failed deployment
                steps.Add("Boot script failed, cleaning up...");
                await CleanupFailedDeploymentAsync(siteId, config.ConnectionName, config.Interface);
                steps.Add("Cleanup complete");

                var logFile = $"/var/log/sqm-{config.ConnectionName?.ToLowerInvariant() ?? config.Interface}.log";
                result.Success = false;
                result.Steps = steps;
                result.Error = $"Boot script did not complete successfully. Check gateway logs at {logFile} for details.";
                return result;
            }

            result.Success = true;
            result.Steps = steps;
            result.Message = $"SQM deployed for {config.ConnectionName} ({config.Interface})";
            _logger.LogInformation("SQM deployment completed for {Name} ({Interface})",
                config.ConnectionName, config.Interface);

            // Invalidate SQM status cache so the new status gets fetched
            SqmService.InvalidateStatusCache(siteId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SQM deployment failed");
            result.Success = false;
            result.Error = ex.Message;
            result.Steps = steps;
        }

        return result;
    }

    /// <summary>
    /// Deploy a single script to the gateway
    /// </summary>
    private async Task<bool> DeployScriptAsync(int siteId, string filename, string content)
    {
        // All SQM scripts now go to on_boot.d (self-contained boot scripts)
        var targetPath = $"{OnBootDir}/{filename}";

        // Normalize line endings to Unix LF (Windows builds may have CRLF)
        var unixContent = content.Replace("\r\n", "\n").Replace("\r", "\n");

        // Use base64 encoding to safely transfer script content (avoids shell quoting issues)
        var base64Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(unixContent));
        var writeCmd = $"echo '{base64Content}' | base64 -d > '{targetPath}'";
        var writeResult = await RunCommandAsync(siteId, writeCmd);

        if (!writeResult.success)
        {
            _logger.LogError("Failed to write {File}: {Error}", filename, writeResult.output);
            return false;
        }

        // Make executable
        var chmodResult = await RunCommandAsync(siteId, $"chmod +x '{targetPath}'");
        if (!chmodResult.success)
        {
            _logger.LogWarning("Failed to chmod {File}: {Error}", filename, chmodResult.output);
        }

        _logger.LogDebug("Deployed {File} to {Path}", filename, targetPath);
        return true;
    }

    /// <summary>
    /// Deploy SQM Monitor script. Uses TcMonitorPort from gateway settings.
    /// Exposes all SQM data (TC rates, speedtest results, ping data) via HTTP.
    /// </summary>
    public async Task<(bool success, string? warning)> DeploySqmMonitorAsync(int siteId, string wan1Interface, string wan1Name, string wan2Interface, string wan2Name)
    {
        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            _logger.LogError("Gateway SSH not configured");
            return (false, null);
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            // Generate SQM monitor script content using port from settings
            var sqmMonitorScript = GenerateSqmMonitorScript(wan1Interface, wan1Name, wan2Interface, wan2Name, settings.TcMonitorPort);

            // Deploy to on_boot.d
            var success = await DeployScriptAsync(siteId, "20-sqm-monitor.sh", sqmMonitorScript);
            if (!success)
            {
                return (false, null);
            }

            // Run the script to set up SQM monitor
            var runResult = await RunCommandAsync(siteId,
                $"{OnBootDir}/20-sqm-monitor.sh");

            if (!runResult.success)
            {
                // Log detailed output for debugging
                _logger.LogWarning(
                    "SQM Monitor setup script did not complete successfully. Output: {Output}",
                    runResult.output);

                // Build error message with truncated output
                var errorMessage = "SQM Monitor script did not complete successfully.";
                if (!string.IsNullOrWhiteSpace(runResult.output))
                {
                    var truncatedOutput = runResult.output.Length > 300
                        ? runResult.output[..300] + "..."
                        : runResult.output;
                    _logger.LogWarning("SQM Monitor script output (truncated): {Output}", truncatedOutput);
                    errorMessage += $" Output: {truncatedOutput}";
                }

                // Clean up the failed deployment
                await CleanupFailedSqmMonitorAsync(siteId);

                return (false, errorMessage);
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy SQM Monitor");
            return (false, null);
        }
    }

    /// <summary>
    /// Remove SQM scripts from the gateway
    /// </summary>
    public async Task<(bool success, List<string> steps)> RemoveAsync(int siteId, bool includeTcMonitor = true)
    {
        var steps = new List<string>();
        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return (false, new List<string> { "Gateway SSH not configured" });
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            // Remove ALL SQM-related cron jobs (catches renamed connections too)
            steps.Add("Removing SQM cron jobs...");
            await RunCommandAsync(siteId,
                "crontab -l 2>/dev/null | grep -v -E 'sqm|SQM' | crontab -");

            // Remove boot scripts (new format: 20-sqm-{name}.sh)
            steps.Add("Removing SQM boot scripts...");
            await RunCommandAsync(siteId,
                $"rm -f {OnBootDir}/20-sqm-*.sh");

            // Remove legacy boot scripts (old format)
            await RunCommandAsync(siteId,
                $"rm -f {OnBootDir}/21-sqm-*.sh");

            // Remove SQM directory with all scripts and data
            steps.Add("Removing SQM data directory...");
            await RunCommandAsync(siteId,
                $"rm -rf {SqmDir}");

            // Remove legacy data files
            await RunCommandAsync(siteId,
                "rm -f /data/sqm-*.sh /data/sqm-*.txt /data/sqm-scripts");

            // Remove SQM Monitor if requested
            if (includeTcMonitor)
            {
                steps.Add("Stopping SQM Monitor service...");
                await RunCommandAsync(siteId,
                    "systemctl stop sqm-monitor-watchdog.timer sqm-monitor 2>/dev/null; " +
                    "systemctl disable sqm-monitor-watchdog.timer sqm-monitor 2>/dev/null");

                steps.Add("Removing SQM Monitor...");
                await RunCommandAsync(siteId,
                    $"rm -f {OnBootDir}/20-sqm-monitor.sh");
                await RunCommandAsync(siteId,
                    "rm -rf /data/sqm-monitor");
                await RunCommandAsync(siteId,
                    "rm -f /etc/systemd/system/sqm-monitor.service " +
                    "/etc/systemd/system/sqm-monitor-watchdog.timer /etc/systemd/system/sqm-monitor-watchdog.service && " +
                    "systemctl daemon-reload");
            }

            steps.Add("SQM removal complete");
            _logger.LogInformation("SQM scripts removed (SQM Monitor: {SqmMonitor})", includeTcMonitor);

            // Invalidate SQM status cache so the "Offline" status gets cached
            SqmService.InvalidateStatusCache(siteId);

            return (true, steps);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove SQM scripts");
            steps.Add($"Error: {ex.Message}");
            return (false, steps);
        }
    }

    /// <summary>
    /// Trigger the SQM adjustment speedtest script on the gateway
    /// This runs the deployed script which does baseline blending and TC adjustment
    /// </summary>
    public async Task<(bool success, string message)> TriggerSqmAdjustmentAsync(int siteId, string wanName)
    {
        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway SSH not configured");
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            // Use same sanitization as deployment to ensure script path matches
            var scriptName = Sqm.InputSanitizer.SanitizeConnectionName(wanName);
            var scriptPath = $"/data/sqm/{scriptName}-speedtest.sh";

            _logger.LogInformation("Triggering SQM adjustment script: {Script}", scriptPath);

            // Check if script exists
            var checkResult = await RunCommandAsync(siteId, $"test -f {scriptPath} && echo 'exists'");
            if (!checkResult.success || !checkResult.output.Contains("exists"))
            {
                return (false, $"SQM script not found: {scriptPath}");
            }

            // Run the script (speedtest can take up to 60 seconds)
            var result = await RunCommandAsync(siteId, scriptPath);

            if (result.success)
            {
                _logger.LogInformation("SQM adjustment completed for {Wan}", wanName);
                return (true, "SQM adjustment completed successfully");
            }
            else
            {
                _logger.LogWarning("SQM adjustment script returned error: {Output}", result.output);
                return (false, $"Script error: {result.output}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to trigger SQM adjustment for {Wan}", wanName);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Trigger a speedtest on the gateway (raw speedtest, not SQM adjustment)
    /// </summary>
    public async Task<SpeedtestResult?> RunSpeedtestAsync(int siteId, SqmConfig config)
    {
        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return null;
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            var cmd = $"speedtest --accept-license --format=json --interface={config.Interface}";
            if (!string.IsNullOrEmpty(config.PreferredSpeedtestServerId))
            {
                cmd += $" --server-id={config.PreferredSpeedtestServerId}";
            }

            var result = await RunCommandAsync(siteId, cmd);
            if (!result.success)
            {
                _logger.LogError("Speedtest failed: {Error}", result.output);
                return null;
            }

            // Parse JSON result
            var json = System.Text.Json.JsonDocument.Parse(result.output);
            var root = json.RootElement;

            return new SpeedtestResult
            {
                Timestamp = DateTime.UtcNow,
                Download = root.GetProperty("download").GetProperty("bandwidth").GetDouble() * 8 / 1_000_000,
                Upload = root.GetProperty("upload").GetProperty("bandwidth").GetDouble() * 8 / 1_000_000,
                Latency = root.GetProperty("ping").GetProperty("latency").GetDouble(),
                Server = root.GetProperty("server").GetProperty("name").GetString() ?? ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run speedtest");
            return null;
        }
    }

    /// <summary>
    /// Generate a baseline based on connection type patterns.
    /// Uses empirical data patterns scaled to the nominal speed.
    /// </summary>
    private Dictionary<string, string> GenerateDefaultBaseline(SqmConfig config)
    {
        // Create a ConnectionProfile to get the hourly baseline pattern
        var profile = new ConnectionProfile
        {
            Type = config.ConnectionType,
            Name = config.ConnectionName ?? "",
            Interface = config.Interface,
            NominalDownloadMbps = config.NominalDownloadSpeed,
            NominalUploadMbps = config.NominalUploadSpeed
        };

        // Get the 168-hour baseline scaled to nominal speed
        return profile.GetHourlyBaseline();
    }

    /// <summary>
    /// Get SQM status for all WANs by parsing gateway logs
    /// </summary>
    public async Task<List<SqmWanStatus>> GetSqmWanStatusAsync(int siteId)
    {
        var result = new List<SqmWanStatus>();

        var settings = await GetGatewaySettingsAsync(siteId);
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return result;
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        try
        {
            // Find all SQM log files
            var logListResult = await RunCommandAsync(siteId,
                "ls /var/log/sqm-*.log 2>/dev/null | xargs -I {} basename {} .log | sed 's/sqm-//'");

            if (!logListResult.success || string.IsNullOrWhiteSpace(logListResult.output))
            {
                return result;
            }

            var wanNames = logListResult.output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var wanName in wanNames)
            {
                var status = new SqmWanStatus { Name = wanName };

                // Get last 50 lines of the log file
                var logResult = await RunCommandAsync(siteId,
                    $"tail -50 /var/log/sqm-{wanName}.log 2>/dev/null");

                if (logResult.success && !string.IsNullOrWhiteSpace(logResult.output))
                {
                    ParseSqmLog(logResult.output, status);
                }

                // Get current rate from result file
                var resultFileResult = await RunCommandAsync(siteId,
                    $"cat /data/sqm/{wanName}-result.txt 2>/dev/null");

                if (resultFileResult.success && !string.IsNullOrWhiteSpace(resultFileResult.output))
                {
                    // Format: "Measured download speed: 206 Mbps"
                    var match = System.Text.RegularExpressions.Regex.Match(
                        resultFileResult.output, @"(\d+)\s*Mbps");
                    if (match.Success && double.TryParse(match.Groups[1].Value, out var rate))
                    {
                        status.CurrentRateMbps = rate;
                    }
                }

                result.Add(status);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get SQM WAN status");
        }

        return result;
    }

    /// <summary>
    /// Parse SQM log output to extract status information
    /// </summary>
    private void ParseSqmLog(string logContent, SqmWanStatus status)
    {
        var lines = logContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Process lines in reverse to find most recent entries
        foreach (var line in lines.Reverse())
        {
            // Parse timestamp: [Fri Dec 27 19:48:02 UTC 2024]
            var timestampMatch = System.Text.RegularExpressions.Regex.Match(
                line, @"\[([A-Za-z]+ [A-Za-z]+ \d+ \d+:\d+:\d+ [A-Z]+ \d+)\]");

            DateTime? timestamp = null;
            if (timestampMatch.Success)
            {
                // Try to parse the date
                if (DateTime.TryParse(timestampMatch.Groups[1].Value, out var parsed))
                {
                    timestamp = parsed;
                }
            }

            // Look for speedtest results: "Measured: 206 Mbps"
            if (status.LastSpeedtestMeasured == null)
            {
                var measuredMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"Measured:\s*(\d+(?:\.\d+)?)\s*Mbps");
                if (measuredMatch.Success && double.TryParse(measuredMatch.Groups[1].Value, out var measured))
                {
                    status.LastSpeedtestMeasured = measured;
                    status.LastSpeedtest = timestamp;
                }
            }

            // Look for adjusted speed: "Adjusted to 196 Mbps"
            if (status.LastSpeedtestAdjusted == null)
            {
                var adjustedMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"Adjusted to\s*(\d+(?:\.\d+)?)\s*Mbps");
                if (adjustedMatch.Success && double.TryParse(adjustedMatch.Groups[1].Value, out var adjusted))
                {
                    status.LastSpeedtestAdjusted = adjusted;
                    if (status.LastSpeedtest == null)
                        status.LastSpeedtest = timestamp;
                }
            }

            // Look for ping adjustment: "Ping adjusted to 195 Mbps (latency: 12.5ms)"
            if (status.LastPingAdjustment == null)
            {
                var pingMatch = System.Text.RegularExpressions.Regex.Match(
                    line, @"Ping adjusted to\s*(\d+(?:\.\d+)?)\s*Mbps\s*\(latency:\s*(\d+(?:\.\d+)?)ms\)");
                if (pingMatch.Success)
                {
                    if (double.TryParse(pingMatch.Groups[1].Value, out var pingRate))
                        status.LastPingRate = pingRate;
                    if (double.TryParse(pingMatch.Groups[2].Value, out var latency))
                        status.LastLatencyMs = latency;
                    status.LastPingAdjustment = timestamp;
                }
            }

            // If we have all the data we need, stop
            if (status.LastSpeedtestMeasured != null && status.LastPingAdjustment != null)
                break;
        }
    }

    /// <summary>
    /// Generate SQM Monitor script content - exposes all SQM data via HTTP
    /// </summary>
    private string GenerateSqmMonitorScript(string wan1Interface, string wan1Name, string wan2Interface, string wan2Name, int port)
    {
        // Security: Sanitize connection names for use in file paths (lowercase, safe chars)
        // Display names use EscapeForShellDoubleQuote to preserve casing while preventing injection
        var wan1LogName = Sqm.InputSanitizer.SanitizeConnectionName(wan1Name);
        var wan2LogName = Sqm.InputSanitizer.SanitizeConnectionName(wan2Name);

        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine("# UniFi on_boot.d script for SQM Monitor");
        sb.AppendLine("# Auto-generated by Network Optimizer");
        sb.AppendLine("# Exposes SQM status, TC rates, and speedtest/ping data via HTTP");
        sb.AppendLine();
        sb.AppendLine("SQM_MONITOR_DIR=\"/data/sqm-monitor\"");
        sb.AppendLine("LOG_FILE=\"/var/log/sqm-monitor.log\"");
        sb.AppendLine("SERVICE_NAME=\"sqm-monitor\"");
        sb.AppendLine("SERVICE_FILE=\"/etc/systemd/system/${SERVICE_NAME}.service\"");
        sb.AppendLine($"PORT=\"{port}\"");
        sb.AppendLine();
        sb.AppendLine("echo \"$(date): Setting up SQM Monitor systemd service...\" >> \"$LOG_FILE\"");
        sb.AppendLine();
        sb.AppendLine("mkdir -p \"$SQM_MONITOR_DIR\"");
        sb.AppendLine();
        sb.AppendLine("# Create the SQM monitor handler script");
        sb.AppendLine("cat > \"$SQM_MONITOR_DIR/sqm-monitor.sh\" << 'HANDLER_EOF'");
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine();
        sb.AppendLine("# WAN Configuration");
        sb.AppendLine($"WAN1_INTERFACE=\"{wan1Interface}\"");
        sb.AppendLine($"WAN1_NAME=\"{Sqm.InputSanitizer.EscapeForShellDoubleQuote(wan1Name)}\"");
        sb.AppendLine($"WAN1_LOG_NAME=\"{wan1LogName}\"");
        sb.AppendLine($"WAN2_INTERFACE=\"{wan2Interface}\"");
        sb.AppendLine($"WAN2_NAME=\"{Sqm.InputSanitizer.EscapeForShellDoubleQuote(wan2Name)}\"");
        sb.AppendLine($"WAN2_LOG_NAME=\"{wan2LogName}\"");
        sb.AppendLine();
        sb.AppendLine("# Get current TC rate for an interface");
        sb.AppendLine("get_tc_rate() {");
        sb.AppendLine("    local interface=$1");
        sb.AppendLine("    tc class show dev \"$interface\" 2>/dev/null | grep \"class htb 1:1 root\" | grep -o 'rate [0-9.]*[MGK]bit' | head -n1 | awk '{print $2}'");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Convert rate to Mbps");
        sb.AppendLine("rate_to_mbps() {");
        sb.AppendLine("    local rate=$1");
        sb.AppendLine("    if echo \"$rate\" | grep -q \"Mbit\"; then");
        sb.AppendLine("        echo \"$rate\" | sed 's/Mbit//'");
        sb.AppendLine("    elif echo \"$rate\" | grep -q \"Gbit\"; then");
        sb.AppendLine("        echo \"$rate\" | sed 's/Gbit//' | awk '{print $1 * 1000}'");
        sb.AppendLine("    elif echo \"$rate\" | grep -q \"Kbit\"; then");
        sb.AppendLine("        echo \"$rate\" | sed 's/Kbit//' | awk '{print $1 / 1000}'");
        sb.AppendLine("    else");
        sb.AppendLine("        echo \"0\"");
        sb.AppendLine("    fi");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Get last speedtest data from log");
        sb.AppendLine("get_speedtest_data() {");
        sb.AppendLine("    local log_name=$1");
        sb.AppendLine("    local log_file=\"/var/log/sqm-${log_name}.log\"");
        sb.AppendLine("    ");
        sb.AppendLine("    if [ ! -f \"$log_file\" ]; then");
        sb.AppendLine("        echo 'null'");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    ");
        sb.AppendLine("    # Find last speedtest entry (look for \"Measured:\" line)");
        sb.AppendLine("    local measured_line=$(grep 'Measured:' \"$log_file\" | tail -1)");
        sb.AppendLine("    local adjusted_line=$(grep 'Adjusted to' \"$log_file\" | tail -1)");
        sb.AppendLine("    ");
        sb.AppendLine("    if [ -z \"$measured_line\" ]; then");
        sb.AppendLine("        echo 'null'");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    ");
        sb.AppendLine("    # Extract timestamp, measured, adjusted");
        sb.AppendLine("    local ts=$(echo \"$measured_line\" | grep -oE '\\[[^]]+\\]' | tr -d '[]')");
        sb.AppendLine("    local measured=$(echo \"$measured_line\" | grep -oE 'Measured: [0-9]+' | awk '{print $2}')");
        sb.AppendLine("    local adjusted=$(echo \"$adjusted_line\" | grep -oE 'Adjusted to [0-9]+' | awk '{print $3}')");
        sb.AppendLine("    ");
        sb.AppendLine("    echo \"{\\\"timestamp\\\": \\\"$ts\\\", \\\"measured_mbps\\\": ${measured:-0}, \\\"adjusted_mbps\\\": ${adjusted:-0}}\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Get last ping adjustment data from log");
        sb.AppendLine("get_ping_data() {");
        sb.AppendLine("    local log_name=$1");
        sb.AppendLine("    local log_file=\"/var/log/sqm-${log_name}.log\"");
        sb.AppendLine("    ");
        sb.AppendLine("    if [ ! -f \"$log_file\" ]; then");
        sb.AppendLine("        echo 'null'");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    ");
        sb.AppendLine("    # Find last ping adjustment entry");
        sb.AppendLine("    local ping_line=$(grep 'Ping adjusted to' \"$log_file\" | tail -1)");
        sb.AppendLine("    ");
        sb.AppendLine("    if [ -z \"$ping_line\" ]; then");
        sb.AppendLine("        echo 'null'");
        sb.AppendLine("        return");
        sb.AppendLine("    fi");
        sb.AppendLine("    ");
        sb.AppendLine("    # Extract timestamp, rate, latency");
        sb.AppendLine("    local ts=$(echo \"$ping_line\" | grep -oE '\\[[^]]+\\]' | tr -d '[]')");
        sb.AppendLine("    local rate=$(echo \"$ping_line\" | grep -oE 'Ping adjusted to [0-9.]+' | awk '{print $4}')");
        sb.AppendLine("    local latency=$(echo \"$ping_line\" | grep -oE 'latency: [0-9.]+' | awk '{print $2}')");
        sb.AppendLine("    ");
        sb.AppendLine("    echo \"{\\\"timestamp\\\": \\\"$ts\\\", \\\"rate_mbps\\\": ${rate:-0}, \\\"latency_ms\\\": ${latency:-0}}\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Get baseline rate from result file");
        sb.AppendLine("get_baseline() {");
        sb.AppendLine("    local log_name=$1");
        sb.AppendLine("    local result_file=\"/data/sqm/${log_name}-result.txt\"");
        sb.AppendLine("    ");
        sb.AppendLine("    if [ -f \"$result_file\" ]; then");
        sb.AppendLine("        grep -oE '[0-9]+' \"$result_file\" | head -1");
        sb.AppendLine("    else");
        sb.AppendLine("        echo \"0\"");
        sb.AppendLine("    fi");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("# Collect all data");
        sb.AppendLine("wan1_rate=$(get_tc_rate \"$WAN1_INTERFACE\")");
        sb.AppendLine("wan2_rate=$(get_tc_rate \"$WAN2_INTERFACE\")");
        sb.AppendLine("wan1_mbps=$(rate_to_mbps \"$wan1_rate\")");
        sb.AppendLine("wan2_mbps=$(rate_to_mbps \"$wan2_rate\")");
        sb.AppendLine("wan1_baseline=$(get_baseline \"$WAN1_LOG_NAME\")");
        sb.AppendLine("wan2_baseline=$(get_baseline \"$WAN2_LOG_NAME\")");
        sb.AppendLine("wan1_speedtest=$(get_speedtest_data \"$WAN1_LOG_NAME\")");
        sb.AppendLine("wan2_speedtest=$(get_speedtest_data \"$WAN2_LOG_NAME\")");
        sb.AppendLine("wan1_ping=$(get_ping_data \"$WAN1_LOG_NAME\")");
        sb.AppendLine("wan2_ping=$(get_ping_data \"$WAN2_LOG_NAME\")");
        sb.AppendLine("timestamp=$(date -u +\"%Y-%m-%dT%H:%M:%SZ\")");
        sb.AppendLine();
        sb.AppendLine("# Check if SQM is active (has TC rules)");
        sb.AppendLine("wan1_active=\"false\"");
        sb.AppendLine("wan2_active=\"false\"");
        sb.AppendLine("[ -n \"$wan1_rate\" ] && wan1_active=\"true\"");
        sb.AppendLine("[ -n \"$wan2_rate\" ] && wan2_active=\"true\"");
        sb.AppendLine();
        sb.AppendLine("# Output JSON");
        sb.AppendLine("cat <<EOF");
        sb.AppendLine("{");
        sb.AppendLine("  \"timestamp\": \"$timestamp\",");
        sb.AppendLine("  \"wan1\": {");
        sb.AppendLine("    \"name\": \"$WAN1_NAME\",");
        sb.AppendLine("    \"interface\": \"$WAN1_INTERFACE\",");
        sb.AppendLine("    \"active\": $wan1_active,");
        sb.AppendLine("    \"current_rate_mbps\": ${wan1_mbps:-0},");
        sb.AppendLine("    \"baseline_mbps\": ${wan1_baseline:-0},");
        sb.AppendLine("    \"last_speedtest\": $wan1_speedtest,");
        sb.AppendLine("    \"last_ping\": $wan1_ping");
        sb.AppendLine("  },");
        sb.AppendLine("  \"wan2\": {");
        sb.AppendLine("    \"name\": \"$WAN2_NAME\",");
        sb.AppendLine("    \"interface\": \"$WAN2_INTERFACE\",");
        sb.AppendLine("    \"active\": $wan2_active,");
        sb.AppendLine("    \"current_rate_mbps\": ${wan2_mbps:-0},");
        sb.AppendLine("    \"baseline_mbps\": ${wan2_baseline:-0},");
        sb.AppendLine("    \"last_speedtest\": $wan2_speedtest,");
        sb.AppendLine("    \"last_ping\": $wan2_ping");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("EOF");
        sb.AppendLine("HANDLER_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod +x \"$SQM_MONITOR_DIR/sqm-monitor.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Create HTTP server script");
        sb.AppendLine("cat > \"$SQM_MONITOR_DIR/sqm-server.sh\" << 'SERVER_EOF'");
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine($"PORT=\"${{SQM_MONITOR_PORT:-{port}}}\"");
        sb.AppendLine("SCRIPT_DIR=\"$(dirname \"$(readlink -f \"$0\")\")\"");
        sb.AppendLine();
        sb.AppendLine("while true; do");
        sb.AppendLine("    {");
        sb.AppendLine("        echo \"HTTP/1.0 200 OK\"");
        sb.AppendLine("        echo \"Content-Type: application/json\"");
        sb.AppendLine("        echo \"Access-Control-Allow-Origin: *\"");
        sb.AppendLine("        echo \"\"");
        sb.AppendLine("        \"$SCRIPT_DIR/sqm-monitor.sh\"");
        sb.AppendLine("    } | nc -l -p \"$PORT\" -q 1 > /dev/null 2>&1");
        sb.AppendLine("    sleep 0.1");
        sb.AppendLine("done");
        sb.AppendLine("SERVER_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod +x \"$SQM_MONITOR_DIR/sqm-server.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Create systemd service with security hardening");
        sb.AppendLine("cat > \"$SERVICE_FILE\" << 'SERVICE_EOF'");
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=SQM Monitor HTTP Server");
        sb.AppendLine("After=network.target");
        sb.AppendLine("Documentation=man:nc(1)");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=simple");
        sb.AppendLine($"Environment=\"SQM_MONITOR_PORT={port}\"");
        sb.AppendLine("ExecStart=/data/sqm-monitor/sqm-server.sh");
        sb.AppendLine("Restart=always");
        sb.AppendLine("RestartSec=5");
        sb.AppendLine("StandardOutput=append:/var/log/sqm-monitor.log");
        sb.AppendLine("StandardError=append:/var/log/sqm-monitor.log");
        sb.AppendLine("User=root");
        sb.AppendLine();
        sb.AppendLine("# Security hardening");
        sb.AppendLine("ProtectSystem=strict");
        sb.AppendLine("ReadWritePaths=/var/log /data/sqm-monitor /data/sqm");
        sb.AppendLine("PrivateTmp=true");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        sb.AppendLine("SERVICE_EOF");
        sb.AppendLine();
        sb.AppendLine("# Create watchdog health check script");
        sb.AppendLine("cat > \"$SQM_MONITOR_DIR/sqm-watchdog.sh\" << 'WATCHDOG_EOF'");
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine("# Health check for SQM Monitor - restarts service if not responding");
        sb.AppendLine($"PORT=\"{port}\"");
        sb.AppendLine("LOG_FILE=\"/var/log/sqm-monitor.log\"");
        sb.AppendLine();
        sb.AppendLine("# Try to connect with 5 second timeout");
        sb.AppendLine("if curl -s --connect-timeout 5 --max-time 10 \"http://127.0.0.1:$PORT\" > /dev/null 2>&1; then");
        sb.AppendLine("    exit 0");
        sb.AppendLine("fi");
        sb.AppendLine();
        sb.AppendLine("# Health check failed - restart the service");
        sb.AppendLine("echo \"$(date): Watchdog detected unresponsive SQM Monitor on port $PORT, restarting...\" >> \"$LOG_FILE\"");
        sb.AppendLine("systemctl restart sqm-monitor");
        sb.AppendLine("WATCHDOG_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod +x \"$SQM_MONITOR_DIR/sqm-watchdog.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Create systemd timer for watchdog (runs every minute)");
        sb.AppendLine("cat > /etc/systemd/system/sqm-monitor-watchdog.timer << 'TIMER_EOF'");
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=SQM Monitor Watchdog Timer");
        sb.AppendLine();
        sb.AppendLine("[Timer]");
        sb.AppendLine("OnBootSec=2min");
        sb.AppendLine("OnUnitActiveSec=1min");
        sb.AppendLine("AccuracySec=30s");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=timers.target");
        sb.AppendLine("TIMER_EOF");
        sb.AppendLine();
        sb.AppendLine("# Create systemd service for watchdog");
        sb.AppendLine("cat > /etc/systemd/system/sqm-monitor-watchdog.service << 'WATCHDOG_SVC_EOF'");
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=SQM Monitor Watchdog Health Check");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=oneshot");
        sb.AppendLine("ExecStart=/data/sqm-monitor/sqm-watchdog.sh");
        sb.AppendLine("WATCHDOG_SVC_EOF");
        sb.AppendLine();
        sb.AppendLine("systemctl daemon-reload");
        sb.AppendLine("systemctl enable \"$SERVICE_NAME\"");
        sb.AppendLine("systemctl restart \"$SERVICE_NAME\"");
        sb.AppendLine();
        sb.AppendLine("# Enable and start the watchdog timer");
        sb.AppendLine("systemctl enable sqm-monitor-watchdog.timer");
        sb.AppendLine("systemctl start sqm-monitor-watchdog.timer");
        sb.AppendLine();
        sb.AppendLine("if systemctl is-active --quiet \"$SERVICE_NAME\"; then");
        sb.AppendLine("    echo \"$(date): SQM Monitor started on port $PORT with watchdog\" >> \"$LOG_FILE\"");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"$(date): SQM Monitor failed to start\" >> \"$LOG_FILE\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");

        return sb.ToString();
    }
}

/// <summary>
/// Per-WAN SQM status from gateway logs
/// </summary>
public class SqmWanStatus
{
    public string Name { get; set; } = "";
    public string Interface { get; set; } = "";
    public double CurrentRateMbps { get; set; }
    public DateTime? LastSpeedtest { get; set; }
    public double? LastSpeedtestMeasured { get; set; }
    public double? LastSpeedtestAdjusted { get; set; }
    public DateTime? LastPingAdjustment { get; set; }
    public double? LastLatencyMs { get; set; }
    public double? LastPingRate { get; set; }
    public bool HasRecentActivity => LastSpeedtest.HasValue || LastPingAdjustment.HasValue;
}

/// <summary>
/// Status of SQM deployment on the gateway
/// </summary>
public class SqmDeploymentStatus
{
    public bool IsDeployed { get; set; }
    public bool UdmBootInstalled { get; set; }
    public bool UdmBootEnabled { get; set; }
    public bool SpeedtestScriptDeployed { get; set; }
    public bool PingScriptDeployed { get; set; }
    public bool TcMonitorDeployed { get; set; }
    public bool TcMonitorRunning { get; set; }
    public bool WatchdogTimerRunning { get; set; }
    public int CronJobsConfigured { get; set; }
    public bool SpeedtestCliInstalled { get; set; }
    public bool BcInstalled { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Result of SQM deployment operation
/// </summary>
public class SqmDeploymentResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public List<string> Steps { get; set; } = new();

    /// <summary>
    /// Non-fatal warnings that occurred during deployment.
    /// Scripts are deployed but may not have activated correctly.
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// True if there are warnings the user should be aware of.
    /// </summary>
    public bool HasWarnings => Warnings.Count > 0;
}
