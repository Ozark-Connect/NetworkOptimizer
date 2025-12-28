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

            // Check for tc-monitor
            var tcMonitorCheck = await _sshService.RunCommandWithDeviceAsync(device,
                $"test -f {OnBootDir}/20-tc-monitor.sh && echo 'exists' || echo 'missing'");
            status.TcMonitorDeployed = tcMonitorCheck.success && tcMonitorCheck.output.Contains("exists");

            // Check if tc-monitor is running
            var tcMonitorRunning = await _sshService.RunCommandWithDeviceAsync(device,
                "systemctl is-active tc-monitor 2>/dev/null || echo 'inactive'");
            status.TcMonitorRunning = tcMonitorRunning.success && tcMonitorRunning.output.Trim() == "active";

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
    public async Task<SqmDeploymentResult> DeployAsync(SqmConfig config, Dictionary<string, string>? baseline = null)
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
            var generator = new ScriptGenerator(config);
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

        // Write file using heredoc
        var writeCmd = $"cat > '{targetPath}' << 'SQMSCRIPTEOF'\n{content}\nSQMSCRIPTEOF";
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
    /// Deploy TC Monitor script
    /// </summary>
    public async Task<bool> DeployTcMonitorAsync(string wan1Interface, string wan1Name, string wan2Interface, string wan2Name)
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
            // Generate tc-monitor script content
            var tcMonitorScript = GenerateTcMonitorScript(wan1Interface, wan1Name, wan2Interface, wan2Name);

            // Deploy to on_boot.d
            var success = await DeployScriptAsync(device, "20-tc-monitor.sh", tcMonitorScript);
            if (!success)
            {
                return false;
            }

            // Run the script to set up tc-monitor
            var runResult = await _sshService.RunCommandWithDeviceAsync(device,
                $"{OnBootDir}/20-tc-monitor.sh");

            if (!runResult.success)
            {
                _logger.LogWarning("TC Monitor setup returned: {Output}", runResult.output);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy TC Monitor");
            return false;
        }
    }

    /// <summary>
    /// Remove SQM scripts from the gateway
    /// </summary>
    public async Task<bool> RemoveAsync()
    {
        var settings = await GetGatewaySettingsAsync();
        if (settings == null || string.IsNullOrEmpty(settings.Host))
        {
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
            // Remove SQM-related cron jobs
            await _sshService.RunCommandWithDeviceAsync(device,
                "crontab -l 2>/dev/null | grep -v '/data/sqm/' | crontab -");

            // Remove boot scripts (new format: 20-sqm-{name}.sh)
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -f {OnBootDir}/20-sqm-*.sh");

            // Remove legacy boot scripts (old format)
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -f {OnBootDir}/21-sqm-*.sh");

            // Remove SQM directory with all scripts and data
            await _sshService.RunCommandWithDeviceAsync(device,
                $"rm -rf {SqmDir}");

            // Remove legacy data files
            await _sshService.RunCommandWithDeviceAsync(device,
                "rm -f /data/sqm-*.sh /data/sqm-*.txt /data/sqm-scripts");

            _logger.LogInformation("SQM scripts removed");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove SQM scripts");
            return false;
        }
    }

    /// <summary>
    /// Trigger a speedtest on the gateway
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
    /// Generate TC Monitor script content
    /// </summary>
    private string GenerateTcMonitorScript(string wan1Interface, string wan1Name, string wan2Interface, string wan2Name)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine("# UniFi on_boot.d script for TC Monitor");
        sb.AppendLine("# Auto-generated by Network Optimizer");
        sb.AppendLine();
        sb.AppendLine("TC_MONITOR_DIR=\"/data/tc-monitor\"");
        sb.AppendLine("LOG_FILE=\"/var/log/tc-monitor.log\"");
        sb.AppendLine("SERVICE_NAME=\"tc-monitor\"");
        sb.AppendLine("SERVICE_FILE=\"/etc/systemd/system/${SERVICE_NAME}.service\"");
        sb.AppendLine("PORT=\"8088\"");
        sb.AppendLine();
        sb.AppendLine("echo \"$(date): Setting up TC Monitor systemd service...\" >> \"$LOG_FILE\"");
        sb.AppendLine();
        sb.AppendLine("mkdir -p \"$TC_MONITOR_DIR\"");
        sb.AppendLine();
        sb.AppendLine("# Create the TC monitor handler script");
        sb.AppendLine("cat > \"$TC_MONITOR_DIR/tc-monitor.sh\" << 'HANDLER_EOF'");
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine($"WAN1_INTERFACE=\"{wan1Interface}\"");
        sb.AppendLine($"WAN1_NAME=\"{wan1Name}\"");
        sb.AppendLine($"WAN2_INTERFACE=\"{wan2Interface}\"");
        sb.AppendLine($"WAN2_NAME=\"{wan2Name}\"");
        sb.AppendLine();
        sb.AppendLine("get_tc_rate() {");
        sb.AppendLine("    local interface=$1");
        sb.AppendLine("    tc class show dev \"$interface\" 2>/dev/null | grep \"class htb 1:1 root\" | grep -o 'rate [0-9.]*[MGK]bit' | head -n1 | awk '{print $2}'");
        sb.AppendLine("}");
        sb.AppendLine();
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
        sb.AppendLine("wan1_rate=$(get_tc_rate \"$WAN1_INTERFACE\")");
        sb.AppendLine("wan2_rate=$(get_tc_rate \"$WAN2_INTERFACE\")");
        sb.AppendLine("wan1_mbps=$(rate_to_mbps \"$wan1_rate\")");
        sb.AppendLine("wan2_mbps=$(rate_to_mbps \"$wan2_rate\")");
        sb.AppendLine("timestamp=$(date -u +\"%Y-%m-%dT%H:%M:%SZ\")");
        sb.AppendLine();
        sb.AppendLine("cat <<EOF");
        sb.AppendLine("{");
        sb.AppendLine("  \"timestamp\": \"$timestamp\",");
        sb.AppendLine("  \"wan1\": {");
        sb.AppendLine("    \"name\": \"$WAN1_NAME\",");
        sb.AppendLine("    \"interface\": \"$WAN1_INTERFACE\",");
        sb.AppendLine("    \"rate_mbps\": $wan1_mbps,");
        sb.AppendLine("    \"rate_raw\": \"$wan1_rate\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"wan2\": {");
        sb.AppendLine("    \"name\": \"$WAN2_NAME\",");
        sb.AppendLine("    \"interface\": \"$WAN2_INTERFACE\",");
        sb.AppendLine("    \"rate_mbps\": $wan2_mbps,");
        sb.AppendLine("    \"rate_raw\": \"$wan2_rate\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine("EOF");
        sb.AppendLine("HANDLER_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod +x \"$TC_MONITOR_DIR/tc-monitor.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Create HTTP server script");
        sb.AppendLine("cat > \"$TC_MONITOR_DIR/tc-server-nc.sh\" << 'SERVER_EOF'");
        sb.AppendLine("#!/bin/sh");
        sb.AppendLine("PORT=\"${TC_MONITOR_PORT:-8088}\"");
        sb.AppendLine("SCRIPT_DIR=\"$(dirname \"$(readlink -f \"$0\")\")\"");
        sb.AppendLine();
        sb.AppendLine("while true; do");
        sb.AppendLine("    {");
        sb.AppendLine("        echo \"HTTP/1.0 200 OK\"");
        sb.AppendLine("        echo \"Content-Type: application/json\"");
        sb.AppendLine("        echo \"Access-Control-Allow-Origin: *\"");
        sb.AppendLine("        echo \"\"");
        sb.AppendLine("        \"$SCRIPT_DIR/tc-monitor.sh\"");
        sb.AppendLine("    } | nc -l -p \"$PORT\" -q 1 > /dev/null 2>&1");
        sb.AppendLine("    sleep 0.1");
        sb.AppendLine("done");
        sb.AppendLine("SERVER_EOF");
        sb.AppendLine();
        sb.AppendLine("chmod +x \"$TC_MONITOR_DIR/tc-server-nc.sh\"");
        sb.AppendLine();
        sb.AppendLine("# Create systemd service");
        sb.AppendLine("cat > \"$SERVICE_FILE\" << 'SERVICE_EOF'");
        sb.AppendLine("[Unit]");
        sb.AppendLine("Description=TC Monitor HTTP Server");
        sb.AppendLine("After=network.target");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=simple");
        sb.AppendLine("Environment=\"TC_MONITOR_PORT=8088\"");
        sb.AppendLine("ExecStart=/data/tc-monitor/tc-server-nc.sh");
        sb.AppendLine("Restart=always");
        sb.AppendLine("RestartSec=5");
        sb.AppendLine("StandardOutput=append:/var/log/tc-monitor.log");
        sb.AppendLine("StandardError=append:/var/log/tc-monitor.log");
        sb.AppendLine("User=root");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine("WantedBy=multi-user.target");
        sb.AppendLine("SERVICE_EOF");
        sb.AppendLine();
        sb.AppendLine("systemctl daemon-reload");
        sb.AppendLine("systemctl enable \"$SERVICE_NAME\"");
        sb.AppendLine("systemctl restart \"$SERVICE_NAME\"");
        sb.AppendLine();
        sb.AppendLine("if systemctl is-active --quiet \"$SERVICE_NAME\"; then");
        sb.AppendLine("    echo \"$(date): TC Monitor started on port $PORT\" >> \"$LOG_FILE\"");
        sb.AppendLine("else");
        sb.AppendLine("    echo \"$(date): TC Monitor failed to start\" >> \"$LOG_FILE\"");
        sb.AppendLine("    exit 1");
        sb.AppendLine("fi");

        return sb.ToString();
    }
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
