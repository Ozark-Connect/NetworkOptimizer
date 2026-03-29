using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services;

public class WanSteerDeploymentService
{
    private const string RemoteDir = "/data/wan-steer";
    private const string RemoteBinaryPath = "/data/wan-steer/wansteer";
    private const string RemoteConfigPath = "/data/wan-steer/config.json";
    private const string RemoteStatusPath = "/tmp/wan-steer-status.json";
    private const string RemoteLogPath = "/data/wan-steer/wansteer.log";
    private const string BootScriptPath = "/data/on_boot.d/25-wan-steer.sh";
    private const string LocalBinaryName = "wansteer-linux-arm64";

    private readonly ILogger<WanSteerDeploymentService> _logger;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly SshClientService _sshClient;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly ISqmService _sqmService;
    private readonly IServiceProvider _serviceProvider;

    public WanSteerDeploymentService(
        ILogger<WanSteerDeploymentService> logger,
        IGatewaySshService gatewaySsh,
        SshClientService sshClient,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        ISqmService sqmService,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _gatewaySsh = gatewaySsh;
        _sshClient = sshClient;
        _dbFactory = dbFactory;
        _sqmService = sqmService;
        _serviceProvider = serviceProvider;
    }

    public async Task<WanSteerStatus> GetStatusAsync()
    {
        var status = new WanSteerStatus();

        try
        {
            var combinedCommand =
                "echo '---PROCESS---'; pgrep -x wansteer > /dev/null 2>&1 && echo running || echo stopped; " +
                "echo '---STATUS---'; cat /tmp/wan-steer-status.json 2>/dev/null || echo '{}'; echo; " +
                "echo '---VERSION---'; /data/wan-steer/wansteer -version 2>/dev/null || echo 'not installed'; " +
                "echo '---BINARY---'; test -x /data/wan-steer/wansteer && echo 'exists' || echo 'missing'";

            var result = await _gatewaySsh.RunCommandAsync(combinedCommand, TimeSpan.FromSeconds(15));
            var sections = ParseDelimitedOutput(result.output);

            status.IsRunning = result.success && GetSection(sections, "PROCESS").Trim() == "running";
            status.StatusJson = GetSection(sections, "STATUS").Trim();
            if (status.StatusJson == "{}") status.StatusJson = null;

            var versionOutput = GetSection(sections, "VERSION").Trim();
            status.Version = versionOutput != "not installed" ? versionOutput : null;

            status.BinaryDeployed = result.success && GetSection(sections, "BINARY").Trim() == "exists";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get WAN Steering status");
        }

        return status;
    }

    public async Task<(bool Success, string? Error)> DeployAsync(
        IProgress<string>? progress, CancellationToken ct = default)
    {
        try
        {
            // Stop existing daemon before binary upload (can't overwrite a running executable)
            // SIGTERM first for clean shutdown, then SIGKILL if it doesn't die in 2 seconds
            progress?.Report("Stopping existing daemon...");
            await _gatewaySsh.RunCommandAsync(
                "pkill -x wansteer 2>/dev/null; sleep 2; pkill -0 -x wansteer 2>/dev/null && pkill -9 -x wansteer; sleep 1",
                TimeSpan.FromSeconds(15), ct);

            // Deploy binary
            progress?.Report("Deploying binary...");
            var (deploySuccess, deployError) = await DeployBinaryAsync(ct);
            if (!deploySuccess)
                return (false, deployError);

            // Discover WANs
            progress?.Report("Discovering WAN interfaces...");
            var wans = await DiscoverWanInterfacesAsync();
            if (wans.Count == 0)
                return (false, "No WAN interfaces discovered. Ensure the UniFi controller is connected and WANs are configured.");

            // Generate and upload config
            progress?.Report("Uploading configuration...");
            var configJson = await GenerateConfigJsonAsync(wans);
            var base64Config = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(configJson));
            var uploadResult = await _gatewaySsh.RunCommandAsync(
                $"mkdir -p {RemoteDir} && echo {base64Config} | base64 -d > {RemoteConfigPath}",
                TimeSpan.FromSeconds(15), ct);

            if (!uploadResult.success)
                return (false, $"Failed to upload config: {uploadResult.output}");

            // Deploy boot script
            progress?.Report("Installing boot script...");
            var bootScript = GenerateBootScript();
            var base64Boot = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(bootScript));
            var bootResult = await _gatewaySsh.RunCommandAsync(
                $"echo {base64Boot} | base64 -d > {BootScriptPath} && chmod +x {BootScriptPath}",
                TimeSpan.FromSeconds(15), ct);

            if (!bootResult.success)
                return (false, $"Failed to install boot script: {bootResult.output}");

            // Start daemon
            progress?.Report("Starting daemon...");
            var startResult = await _gatewaySsh.RunCommandAsync(
                $"nohup {RemoteBinaryPath} -config {RemoteConfigPath} >> {RemoteLogPath} 2>&1 & sleep 2 && pgrep -x wansteer > /dev/null 2>&1 && echo started || echo failed",
                TimeSpan.FromSeconds(15), ct);

            if (!startResult.success || !startResult.output.Contains("started"))
            {
                // Try to grab the last few lines of the log for diagnostics
                var logResult = await _gatewaySsh.RunCommandAsync(
                    $"tail -5 {RemoteLogPath} 2>/dev/null", TimeSpan.FromSeconds(5));
                var logTail = logResult.success ? logResult.output.Trim() : "";
                var errorMsg = "Daemon failed to start";
                if (!string.IsNullOrEmpty(logTail))
                    errorMsg += $": {logTail}";
                return (false, errorMsg);
            }

            _logger.LogInformation("WAN Steering deployed and started successfully");
            progress?.Report("WAN Steering deployed successfully");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy WAN Steering");
            return (false, ex.Message);
        }
    }

    public async Task StopAsync()
    {
        try
        {
            await _gatewaySsh.RunCommandAsync(
                "pkill -x wansteer 2>/dev/null; sleep 2; pkill -0 -x wansteer 2>/dev/null && pkill -9 -x wansteer; sleep 1",
                TimeSpan.FromSeconds(15));
            _logger.LogInformation("WAN Steering daemon stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop WAN Steering daemon");
        }
    }

    public async Task<(bool Success, string? Error)> ReloadConfigAsync()
    {
        try
        {
            var wans = await DiscoverWanInterfacesAsync();
            if (wans.Count == 0)
                return (false, "No WAN interfaces discovered");

            var configJson = await GenerateConfigJsonAsync(wans);
            var base64Config = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(configJson));
            var uploadResult = await _gatewaySsh.RunCommandAsync(
                $"echo {base64Config} | base64 -d > {RemoteConfigPath}",
                TimeSpan.FromSeconds(15));

            if (!uploadResult.success)
                return (false, $"Failed to upload config: {uploadResult.output}");

            var reloadResult = await _gatewaySsh.RunCommandAsync(
                "pkill -HUP wansteer 2>/dev/null && echo reloaded || echo not_running",
                TimeSpan.FromSeconds(10));

            if (reloadResult.output.Contains("not_running"))
                return (false, "Daemon is not running. Deploy first.");

            _logger.LogInformation("WAN Steering config reloaded");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reload WAN Steering config");
            return (false, ex.Message);
        }
    }

    public async Task<(bool Success, string? Error)> RemoveAsync()
    {
        try
        {
            // Clean up iptables rules first (in case SIGKILL is needed and daemon can't clean up itself)
            await _gatewaySsh.RunCommandAsync(
                $"{RemoteBinaryPath} -cleanup -config {RemoteConfigPath} 2>/dev/null; " +
                "pkill -x wansteer 2>/dev/null; sleep 2; pkill -0 -x wansteer 2>/dev/null && pkill -9 -x wansteer; sleep 1; " +
                $"rm -rf {RemoteDir} && rm -f {BootScriptPath} && rm -f {RemoteStatusPath}",
                TimeSpan.FromSeconds(20));

            _logger.LogInformation("WAN Steering removed from gateway");
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove WAN Steering");
            return (false, ex.Message);
        }
    }

    public async Task<List<WanSteerWanInfo>> DiscoverWanInterfacesAsync()
    {
        var result = new List<WanSteerWanInfo>();

        try
        {
            // Get WAN interfaces from controller for friendly names and interface mappings
            var controllerWans = await _sqmService.GetWanInterfacesFromControllerAsync();
            if (controllerWans.Count == 0)
            {
                _logger.LogWarning("No WAN interfaces from controller");
                return result;
            }

            // Get ip rule show for fwmark mapping
            var ipRuleResult = await _gatewaySsh.RunCommandAsync(
                "ip rule show", TimeSpan.FromSeconds(10));

            if (!ipRuleResult.success)
            {
                _logger.LogWarning("Failed to get ip rules: {Output}", ipRuleResult.output);
                return result;
            }

            // Parse fwmark -> interface mapping from ip rule output
            var fwmarkMap = ParseIpRules(ipRuleResult.output);

            // For each controller WAN, find its fwmark and route table, then get gateway IP
            foreach (var wan in controllerWans)
            {
                var wanInfo = new WanSteerWanInfo
                {
                    Name = wan.Name,
                    Interface = wan.Interface,
                    NetworkGroup = wan.NetworkGroup ?? "WAN"
                };

                // Find matching fwmark entry by interface name
                if (fwmarkMap.TryGetValue(wan.Interface, out var ruleInfo))
                {
                    wanInfo.FWMark = ruleInfo.FWMark;
                    wanInfo.RouteTable = ruleInfo.RouteTable;

                    // Get gateway IP from route table
                    var routeResult = await _gatewaySsh.RunCommandAsync(
                        $"ip route show table {ruleInfo.RouteTable} 2>/dev/null",
                        TimeSpan.FromSeconds(10));

                    if (routeResult.success)
                    {
                        var gwMatch = Regex.Match(routeResult.output, @"default via (\S+)");
                        if (gwMatch.Success)
                            wanInfo.GatewayIp = gwMatch.Groups[1].Value;
                    }
                }
                else
                {
                    _logger.LogDebug("No fwmark found for WAN interface {Interface}", wan.Interface);
                }

                result.Add(wanInfo);
            }

            _logger.LogDebug("Discovered {Count} WAN interfaces for WAN Steering: {WANs}",
                result.Count, string.Join(", ", result.Select(w => $"{w.Name} ({w.Interface}, fwmark={w.FWMark})")));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover WAN interfaces");
        }

        return result;
    }

    public async Task<string> GenerateConfigJsonAsync(List<WanSteerWanInfo> wans)
    {
        // Build WAN interfaces map with sanitized keys
        var wanInterfaces = new Dictionary<string, object>();
        var networkGroupToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? defaultWan = null;

        foreach (var wan in wans)
        {
            var key = SanitizeWanKey(wan.Name);
            wanInterfaces[key] = new
            {
                @interface = wan.Interface,
                gateway = wan.GatewayIp ?? "",
                route_table = wan.RouteTable,
                fwmark = wan.FWMark,
                health_target = wan.HealthTarget
            };
            networkGroupToKey[wan.NetworkGroup] = key;
            defaultWan ??= key;
        }

        // Load traffic classes from DB
        await using var db = await _dbFactory.CreateDbContextAsync();
        var trafficClasses = await db.WanSteerTrafficClasses
            .OrderBy(tc => tc.SortOrder)
            .ToListAsync();

        var trafficClassConfigs = new List<object>();
        foreach (var tc in trafficClasses)
        {
            // Map TargetWanKey (e.g., "WAN2") to the sanitized wan key
            var targetWan = networkGroupToKey.TryGetValue(tc.TargetWanKey, out var mapped)
                ? mapped : SanitizeWanKey(tc.TargetWanKey);

            var match = new Dictionary<string, object>();
            if (tc.DstCidrsJson != null)
            {
                var (cidrs, ranges) = SplitCidrsAndRanges(tc.DstCidrsJson);
                if (cidrs.Count > 0) match["dst_cidrs"] = cidrs;
                if (ranges.Count > 0) match["dst_ranges"] = ranges;
            }
            if (tc.SrcCidrsJson != null)
            {
                var (cidrs, ranges) = SplitCidrsAndRanges(tc.SrcCidrsJson);
                if (cidrs.Count > 0) match["src_cidrs"] = cidrs;
                if (ranges.Count > 0) match["src_ranges"] = ranges;
            }
            if (tc.SrcMacsJson != null)
                match["src_macs"] = JsonSerializer.Deserialize<List<string>>(tc.SrcMacsJson) ?? [];
            if (tc.Protocol != null)
                match["protocol"] = tc.Protocol;
            if (tc.DstPortsJson != null)
                match["dst_ports"] = JsonSerializer.Deserialize<List<string>>(tc.DstPortsJson) ?? [];
            if (tc.SrcPortsJson != null)
                match["src_ports"] = JsonSerializer.Deserialize<List<string>>(tc.SrcPortsJson) ?? [];

            trafficClassConfigs.Add(new
            {
                name = SanitizeWanKey(tc.Name),
                match,
                probability = tc.Probability,
                target_wan = targetWan,
                enabled = tc.Enabled
            });
        }

        var config = new Dictionary<string, object>
        {
            ["wan_interfaces"] = wanInterfaces,
            ["default_wan"] = defaultWan ?? "",
            ["reconcile_interval_seconds"] = 30,
            ["health_check_interval_seconds"] = 10,
            ["health_check_timeout_seconds"] = 3,
            ["health_fail_threshold"] = 3,
            ["health_pass_threshold"] = 2,
            ["status_file"] = RemoteStatusPath,
            ["traffic_classes"] = trafficClassConfigs
        };

        return JsonSerializer.Serialize(config, ConfigJsonOptions);
    }

    private async Task<(bool Success, string? Error)> DeployBinaryAsync(CancellationToken ct)
    {
        try
        {
            var localPath = Path.Combine(AppContext.BaseDirectory, "tools", LocalBinaryName);
            if (!File.Exists(localPath))
            {
                _logger.LogWarning("wansteer binary not found at {Path}", localPath);
                return (false, "WAN Steering binary not found. It may not be included in this build.");
            }

            var settings = await _gatewaySsh.GetSettingsAsync();
            if (string.IsNullOrEmpty(settings.Host) || !settings.HasCredentials)
                return (false, "Gateway SSH not configured");

            // Check if remote binary is already up to date via MD5
            var localHash = ComputeMd5(localPath);
            var hashResult = await _gatewaySsh.RunCommandAsync(
                $"md5sum {RemoteBinaryPath} 2>/dev/null | cut -d' ' -f1",
                TimeSpan.FromSeconds(10), ct);

            if (hashResult.success)
            {
                var remoteHash = hashResult.output.Trim();
                if (string.Equals(localHash, remoteHash, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("wansteer binary already up to date on gateway");
                    return (true, null);
                }
            }

            // Upload via SFTP
            var connection = GetConnectionInfo(settings);

            // Ensure directory exists
            await _gatewaySsh.RunCommandAsync($"mkdir -p {RemoteDir}", TimeSpan.FromSeconds(10), ct);

            _logger.LogInformation("Deploying wansteer binary to gateway {Host}", settings.Host);
            await _sshClient.UploadBinaryAsync(connection, localPath, RemoteBinaryPath, ct);

            // Make executable
            var chmodResult = await _gatewaySsh.RunCommandAsync(
                $"chmod +x {RemoteBinaryPath}", TimeSpan.FromSeconds(10), ct);

            if (!chmodResult.success)
                return (false, $"Failed to set binary permissions: {chmodResult.output}");

            // Verify
            var versionResult = await _gatewaySsh.RunCommandAsync(
                $"{RemoteBinaryPath} -version", TimeSpan.FromSeconds(10), ct);

            if (versionResult.success)
            {
                _logger.LogInformation("wansteer binary deployed successfully: {Version}", versionResult.output.Trim());
                return (true, null);
            }

            return (false, $"Binary deployed but version check failed: {versionResult.output}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deploy wansteer binary to gateway");
            return (false, ex.Message);
        }
    }

    private SshConnectionInfo GetConnectionInfo(GatewaySshSettings settings)
    {
        using var scope = _serviceProvider.CreateScope();
        var credProtection = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.ICredentialProtectionService>();

        string? decryptedPassword = null;
        if (!string.IsNullOrEmpty(settings.Password))
            decryptedPassword = credProtection.Decrypt(settings.Password);

        return SshConnectionInfo.FromGatewaySettings(settings, decryptedPassword);
    }

    private static string ComputeMd5(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = System.Security.Cryptography.MD5.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }

    internal static string GenerateBootScript()
    {
        return """
               #!/bin/sh
               # WAN Steering - start daemon on boot
               # Entire block runs in background so we don't block udm-boot or other scripts.
               # 30s delay lets UniFi finish iptables setup; daemon also uses -w for lock wait.
               (
                   sleep 30
                   if [ -x /data/wan-steer/wansteer ] && [ -f /data/wan-steer/config.json ]; then
                       nohup /data/wan-steer/wansteer -config /data/wan-steer/config.json >> /data/wan-steer/wansteer.log 2>&1 &
                   fi
               ) &
               """;
    }

    /// <summary>Split a JSON array of address entries into CIDRs/IPs and IP ranges.</summary>
    internal static (List<string> Cidrs, List<string> Ranges) SplitCidrsAndRanges(string json)
    {
        var cidrs = new List<string>();
        var ranges = new List<string>();
        var entries = JsonSerializer.Deserialize<List<string>>(json) ?? [];
        foreach (var entry in entries)
        {
            if (entry.Contains('-'))
                ranges.Add(entry);
            else
                cidrs.Add(entry);
        }
        return (cidrs, ranges);
    }

    internal static string SanitizeWanKey(string name)
    {
        return Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    }

    internal static Dictionary<string, (string FWMark, string RouteTable)> ParseIpRules(string output)
    {
        var map = new Dictionary<string, (string FWMark, string RouteTable)>();
        var regex = new Regex(@"fwmark\s+(0x[0-9a-f]+)/0x7e0000\s+lookup\s+(\d+\.(eth\d+|ppp\d+))");

        foreach (var line in output.Split('\n'))
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                var fwmark = match.Groups[1].Value;
                var routeTable = match.Groups[2].Value;
                var iface = match.Groups[3].Value;
                map[iface] = (fwmark, routeTable);
            }
        }

        return map;
    }

    internal static Dictionary<string, string> ParseDelimitedOutput(string output)
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

    internal static string GetSection(Dictionary<string, string> sections, string key)
        => sections.TryGetValue(key, out var value) ? value : "";

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
}

public class WanSteerStatus
{
    public bool IsRunning { get; set; }
    public string? Version { get; set; }
    public string? StatusJson { get; set; }
    public bool BinaryDeployed { get; set; }
}

public class WanSteerWanInfo
{
    public string Name { get; set; } = "";
    public string Interface { get; set; } = "";
    public string NetworkGroup { get; set; } = "";
    public string FWMark { get; set; } = "";
    public string RouteTable { get; set; } = "";
    public string? GatewayIp { get; set; }
    public string HealthTarget { get; set; } = "1.1.1.1";
}
