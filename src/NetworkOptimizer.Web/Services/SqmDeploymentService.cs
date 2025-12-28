using Microsoft.Extensions.Logging;
using NetworkOptimizer.Sqm;
using NetworkOptimizer.Sqm.Models;
using NetworkOptimizer.Storage.Interfaces;
using NetworkOptimizer.Storage.Models;
using System.Text;
using SqmConfig = NetworkOptimizer.Sqm.Models.SqmConfiguration;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for deploying SQM scripts to UniFi gateways via SSH.
/// Follows the same SSH execution pattern as Iperf3SpeedTestService.
/// </summary>
public class SqmDeploymentService
{
    private readonly ILogger<SqmDeploymentService> _logger;
    private readonly UniFiSshService _sshService;
    private readonly IServiceProvider _serviceProvider;

    // Gateway paths
    private const string OnBootDir = "/data/on_boot.d";
    private const string SqmDir = "/data/sqm";
    private const string TcMonitorDir = "/data/tc-monitor";

    public SqmDeploymentService(
        ILogger<SqmDeploymentService> logger,
        UniFiSshService sshService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _sshService = sshService;
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// Get gateway SSH settings
    /// </summary>
    private async Task<GatewaySshSettings?> GetGatewaySettingsAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISpeedTestRepository>();
        return await repository.GetGatewaySshSettingsAsync();
    }

    /// <summary>
    /// Test SSH connection to the gateway
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionAsync()
    {
        var settings = await GetGatewaySettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway SSH not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "Gateway SSH credentials not configured");
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        return await _sshService.TestConnectionAsync(device);
    }

    /// <summary>
    /// Install udm-boot package on the gateway.
    /// This enables scripts in /data/on_boot.d/ to run automatically on boot
    /// and persist across firmware updates.
    /// </summary>
    public async Task<(bool success, string message)> InstallUdmBootAsync()
    {
        var settings = await GetGatewaySettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return (false, "Gateway SSH not configured");
        }

        if (!settings.HasCredentials)
        {
            return (false, "Gateway SSH credentials not configured");
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
            _logger.LogInformation("Installing udm-boot on gateway {Host}", settings.Host);

            // Create the udm-boot service file directly (works on all UDM/UCG devices)
            var serviceContent = @"[Unit]
Description=Run On Startup UDM 2.x and above
Wants=network-online.target
After=network-online.target
StartLimitIntervalSec=500
StartLimitBurst=1

[Service]
Type=oneshot
ExecStart=bash -c 'mkdir -p /data/on_boot.d && find -L /data/on_boot.d -mindepth 1 -maxdepth 1 -type f -name ""*.sh"" -print0 | sort -z | xargs -0 -r -n 1 -- bash'
RemainAfterExit=true

[Install]
WantedBy=multi-user.target";

            // Write service file, enable and start
            var installCmd = $@"
cat > /etc/systemd/system/udm-boot.service << 'SERVICEEOF'
{serviceContent}
SERVICEEOF
mkdir -p /data/on_boot.d && \
systemctl daemon-reload && \
systemctl enable udm-boot && \
systemctl start udm-boot && \
echo 'udm-boot installed successfully'
";
            var result = await _sshService.RunCommandWithDeviceAsync(device, installCmd);

            if (result.success && result.output.Contains("udm-boot installed successfully"))
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
    public async Task<SqmDeploymentStatus> CheckDeploymentStatusAsync()
    {
        var settings = await GetGatewaySettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            return new SqmDeploymentStatus { Error = "Gateway SSH not configured" };
        }

        var device = new DeviceSshConfiguration
        {
            Host = settings.Host,
            SshUsername = settings.Username,
            SshPassword = settings.Password,
            SshPrivateKeyPath = settings.PrivateKeyPath
        };

        var status = new SqmDeploymentStatus();

        try
        {
            // Check for udm-boot (required for on_boot.d scripts to run on boot)
            // Check for service file (supports both manual install and deb package)
            var udmBootCheck = await _sshService.RunCommandWithDeviceAsync(device,
                "test -f /etc/systemd/system/udm-boot.service && echo 'installed' || echo 'missing'");
            status.UdmBootInstalled = udmBootCheck.success && udmBootCheck.output.Contains("installed");

            // Check if udm-boot is enabled
            var udmBootEnabled = await _sshService.RunCommandWithDeviceAsync(device,
                "systemctl is-enabled udm-boot 2>/dev/null || echo 'disabled'");
            status.UdmBootEnabled = udmBootEnabled.success && udmBootEnabled.output.Trim() == "enabled";

            // Check for SQM boot scripts (new pattern: 20-sqm-{name}.sh)
            var sqmBootCheck = await _sshService.RunCommandWithDeviceAsync(device,
                $"ls {OnBootDir}/20-sqm-*.sh 2>/dev/null | wc -l");
            var bootScriptCount = 0;
            if (sqmBootCheck.success && int.TryParse(sqmBootCheck.output.Trim(), out bootScriptCount))
            {
                status.SpeedtestScriptDeployed = bootScriptCount > 0;
                status.PingScriptDeployed = bootScriptCount > 0; // Both are in the same boot script now
            }

            // Check for deployed SQM scripts in /data/sqm/
            var sqmScriptsCheck = await _sshService.RunCommandWithDeviceAsync(device,
                $"ls {SqmDir}/*-speedtest.sh 2>/dev/null | wc -l");
            if (sqmScriptsCheck.success && int.TryParse(sqmScriptsCheck.output.Trim(), out int sqmScriptCount))
            {
                status.SpeedtestScriptDeployed = status.SpeedtestScriptDeployed || sqmScriptCount > 0;
            }

            // Check for SQM Monitor (check both new sqm-monitor and old tc-monitor)
            var sqmMonitorCheck = await _sshService.RunCommandWithDeviceAsync(device,
                $"test -f {OnBootDir}/20-sqm-monitor.sh && echo 'exists' || (test -f {OnBootDir}/20-tc-monitor.sh && echo 'exists' || echo 'missing')");
            status.TcMonitorDeployed = sqmMonitorCheck.success && sqmMonitorCheck.output.Contains("exists");

            // Check if SQM Monitor is running (check both new sqm-monitor and old tc-monitor)
            var sqmMonitorRunning = await _sshService.RunCommandWithDeviceAsync(device,
                "systemctl is-active sqm-monitor 2>/dev/null || systemctl is-active tc-monitor 2>/dev/null || echo 'inactive'");
            status.TcMonitorRunning = sqmMonitorRunning.success && sqmMonitorRunning.output.Trim() == "active";

            // Check for cron jobs
            var cronCheck = await _sshService.RunCommandWithDeviceAsync(device,
                "crontab -l 2>/dev/null | grep -c sqm || echo '0'");
            if (cronCheck.success && int.TryParse(cronCheck.output.Trim(), out int cronCount))
            {
                status.CronJobsConfigured = cronCount;
            }

            // Check for speedtest CLI
            var speedtestCliCheck = await _sshService.RunCommandWithDeviceAsync(device,
                "which speedtest >/dev/null 2>&1 && echo 'installed' || echo 'missing'");
            status.SpeedtestCliInstalled = speedtestCliCheck.success && speedtestCliCheck.output.Contains("installed");

            // Check for bc (math utility)
            var bcCheck = await _sshService.RunCommandWithDeviceAsync(device,
                "which bc >/dev/null 2>&1 && echo 'installed' || echo 'missing'");
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
    /// Deploy SQM scripts to the gateway
    /// </summary>
    /// <param name="config">SQM configuration for this WAN</param>
    /// <param name="baseline">Optional hourly baseline data</param>
    /// <param name="initialDelaySeconds">Delay before first speedtest (default 60s, use higher values for additional WANs to stagger)</param>
    public async Task<SqmDeploymentResult> DeployAsync(SqmConfig config, Dictionary<string, string>? baseline = null, int initialDelaySeconds = 60)
    {
        var result = new SqmDeploymentResult();
        var steps = new List<string>();

        var settings = await GetGatewaySettingsAsync();
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
            _logger.LogInformation("Deploying SQM with config: {Summary}", config.GetParameterSummary());

            // Step 1: Create directories
            steps.Add("Creating directories...");
            var mkdirResult = await _sshService.RunCommandWithDeviceAsync(device,
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
                var success = await DeployScriptAsync(device, filename, content);
                if (!success)
                {
                    throw new Exception($"Failed to deploy {filename}");
                }
            }

            // Step 4: Run the boot script to set up everything
            steps.Add("Running boot script (installs deps, creates scripts, configures cron)...");
            var setupResult = await _sshService.RunCommandWithDeviceAsync(device,
                $"chmod +x {OnBootDir}/{bootScriptName} && {OnBootDir}/{bootScriptName}");

            if (!setupResult.success)
            {
                _logger.LogWarning("Boot script returned: {Output}", setupResult.output);
                // Don't fail deployment, script is in place for next boot
            }

            result.Success = true;
            result.Steps = steps;
            result.Message = $"SQM deployed for {config.ConnectionName} ({config.Interface})";
            _logger.LogInformation("SQM deployment completed for {Name} ({Interface})",
                config.ConnectionName, config.Interface);
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
    private async Task<bool> DeployScriptAsync(DeviceSshConfiguration device, string filename, string content)
    {
        // All SQM scripts now go to on_boot.d (self-contained boot scripts)
        var targetPath = $"{OnBootDir}/{filename}";

        // Use base64 encoding to safely transfer script content (avoids shell quoting issues)
        var base64Content = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));
        var writeCmd = $"echo '{base64Content}' | base64 -d > '{targetPath}'";
        var writeResult = await _sshService.RunCommandWithDeviceAsync(device, writeCmd);

        if (!writeResult.success)
        {
            _logger.LogError("Failed to write {File}: {Error}", filename, writeResult.output);
            return false;
        }

        // Make executable
        var chmodResult = await _sshService.RunCommandWithDeviceAsync(device, $"chmod +x '{targetPath}'");
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
    public async Task<bool> DeploySqmMonitorAsync(string wan1Interface, string wan1Name, string wan2Interface, string wan2Name)
    {
        var settings = await GetGatewaySettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
            _logger.LogError("Gateway SSH not configured");
            return false;
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
            var success = await DeployScriptAsync(device, "20-sqm-monitor.sh", sqmMonitorScript);
            if (!success)
            {
                return false;
            }

            // Run the script to set up SQM monitor (also cleans up old tc-monitor)
            var runResult = await _sshService.RunCommandWithDeviceAsync(device,
                $"{OnBootDir}/20-sqm-monitor.sh");

            if (!runResult.success)
            {
                _logger.LogWarning("SQM Monitor setup returned: {Output}", runResult.output);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy SQM Monitor");
            return false;
        }
    }

    /// <summary>
    /// Remove SQM scripts from the gateway
    /// </summary>
    public async Task<(bool success, List<string> steps)> RemoveAsync(bool includeTcMonitor = true)
    {
        var steps = new List<string>();
        var settings = await GetGatewaySettingsAsync();
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
            // Remove SQM-related cron jobs
            steps.Add("Removing SQM cron jobs...");
            await _sshService.RunCommandWithDeviceAsync(device,
                "crontab -l 2>/dev/null | grep -v '/data/sqm/' | crontab -");

            // Remove boot scripts (new format: 20-sqm-{name}.sh)
            steps.Add("Removing SQM boot scripts...");
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -f {OnBootDir}/20-sqm-*.sh");

            // Remove legacy boot scripts (old format)
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -f {OnBootDir}/21-sqm-*.sh");

            // Remove SQM directory with all scripts and data
            steps.Add("Removing SQM data directory...");
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -rf {SqmDir}");

            // Remove legacy data files
            await _sshService.RunCommandWithDeviceAsync(device,
                "rm -f /data/sqm-*.sh /data/sqm-*.txt /data/sqm-scripts");

            // Remove SQM Monitor if requested (handles both old tc-monitor and new sqm-monitor)
            if (includeTcMonitor)
            {
                steps.Add("Stopping SQM Monitor service...");
                // Stop both old and new services
                await _sshService.RunCommandWithDeviceAsync(device,
                    "systemctl stop sqm-monitor tc-monitor 2>/dev/null; systemctl disable sqm-monitor tc-monitor 2>/dev/null");

                steps.Add("Removing SQM Monitor...");
                // Remove both old and new boot scripts and directories
                await _sshService.RunCommandWithDeviceAsync(device,
                    $"rm -f {OnBootDir}/20-sqm-monitor.sh {OnBootDir}/20-tc-monitor.sh");
                await _sshService.RunCommandWithDeviceAsync(device,
                    $"rm -rf /data/sqm-monitor {TcMonitorDir}");
                await _sshService.RunCommandWithDeviceAsync(device,
                    "rm -f /etc/systemd/system/sqm-monitor.service /etc/systemd/system/tc-monitor.service && systemctl daemon-reload");
            }

            steps.Add("SQM removal complete");
            _logger.LogInformation("SQM scripts removed (TC Monitor: {TcMonitor})", includeTcMonitor);
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
    public async Task<(bool success, string message)> TriggerSqmAdjustmentAsync(string wanName)
    {
        var settings = await GetGatewaySettingsAsync();
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
            // Normalize wan name for script path (lowercase, replace spaces with dashes)
            var scriptName = wanName.ToLowerInvariant().Replace(" ", "-");
            var scriptPath = $"/data/sqm/{scriptName}-speedtest.sh";

            _logger.LogInformation("Triggering SQM adjustment script: {Script}", scriptPath);

            // Check if script exists
            var checkResult = await _sshService.RunCommandWithDeviceAsync(device, $"test -f {scriptPath} && echo 'exists'");
            if (!checkResult.success || !checkResult.output.Contains("exists"))
            {
                return (false, $"SQM script not found: {scriptPath}");
            }

            // Run the script (speedtest can take up to 60 seconds)
            var result = await _sshService.RunCommandWithDeviceAsync(device, scriptPath);

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
    public async Task<SpeedtestResult?> RunSpeedtestAsync(SqmConfig config)
    {
        var settings = await GetGatewaySettingsAsync();
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

            var result = await _sshService.RunCommandWithDeviceAsync(device, cmd);
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
    public async Task<List<SqmWanStatus>> GetSqmWanStatusAsync()
    {
        var result = new List<SqmWanStatus>();

        var settings = await GetGatewaySettingsAsync();
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
            var logListResult = await _sshService.RunCommandWithDeviceAsync(device,
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
                var logResult = await _sshService.RunCommandWithDeviceAsync(device,
                    $"tail -50 /var/log/sqm-{wanName}.log 2>/dev/null");

                if (logResult.success && !string.IsNullOrWhiteSpace(logResult.output))
                {
                    ParseSqmLog(logResult.output, status);
                }

                // Get current rate from result file
                var resultFileResult = await _sshService.RunCommandWithDeviceAsync(device,
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
        // Normalize names for log file lookup (lowercase, dashes for spaces)
        var wan1LogName = wan1Name.ToLowerInvariant().Replace(" ", "-");
        var wan2LogName = wan2Name.ToLowerInvariant().Replace(" ", "-");

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
        sb.AppendLine($"WAN1_NAME=\"{wan1Name}\"");
        sb.AppendLine($"WAN1_LOG_NAME=\"{wan1LogName}\"");
        sb.AppendLine($"WAN2_INTERFACE=\"{wan2Interface}\"");
        sb.AppendLine($"WAN2_NAME=\"{wan2Name}\"");
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
        sb.AppendLine("# Create systemd service");
        sb.AppendLine("cat > \"$SERVICE_FILE\" << 'SERVICE_EOF'");
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=SQM Monitor HTTP Server");
        sb.AppendLine("After=network.target");
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
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        sb.AppendLine("SERVICE_EOF");
        sb.AppendLine();
        sb.AppendLine("# Stop old tc-monitor if running");
        sb.AppendLine("systemctl stop tc-monitor 2>/dev/null || true");
        sb.AppendLine("systemctl disable tc-monitor 2>/dev/null || true");
        sb.AppendLine("rm -f /etc/systemd/system/tc-monitor.service 2>/dev/null || true");
        sb.AppendLine("rm -rf /data/tc-monitor 2>/dev/null || true");
        sb.AppendLine();
        sb.AppendLine("systemctl daemon-reload");
        sb.AppendLine("systemctl enable \"$SERVICE_NAME\"");
        sb.AppendLine("systemctl restart \"$SERVICE_NAME\"");
        sb.AppendLine();
        sb.AppendLine("if systemctl is-active --quiet \"$SERVICE_NAME\"; then");
        sb.AppendLine("    echo \"$(date): SQM Monitor started on port $PORT\" >> \"$LOG_FILE\"");
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
}
