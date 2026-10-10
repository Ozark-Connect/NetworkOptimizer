using System.Diagnostics;
using System.Security.Cryptography;
using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Threats.Waf;
using NetworkOptimizer.Web.Services.Auditing;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages Traefik as a child process for HTTPS reverse proxying.
/// Active on Windows only when the Traefik feature is installed (traefik.exe present).
/// Generates static and dynamic configs from templates using registry values on each startup.
/// CF_DNS_API_TOKEN is injected via process environment variable, never written to disk.
/// With TRAEFIK_WAF_MODE set to detect or block, it also runs netopt-waf and wires it to Threat Intelligence.
/// </summary>
public class TraefikHostedService : IHostedService, IDisposable
{
    private readonly ILogger<TraefikHostedService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IServiceProvider _services;
    private Process? _traefikProcess;
    private Process? _wafProcess;
    private readonly string _installFolder;
    private bool _disposed;

    /// <summary>Where the bundled netopt-waf listens; the template's waf middleware points here.</summary>
    internal const string LocalWafUrl = "http://127.0.0.1:8044";

    public TraefikHostedService(ILogger<TraefikHostedService> logger, IConfiguration configuration, IServiceProvider services)
    {
        _logger = logger;
        _configuration = configuration;
        _services = services;
        _installFolder = AppContext.BaseDirectory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogDebug("TraefikHostedService: Not running on Windows, skipping");
            return;
        }

        var traefikFolder = Path.Combine(_installFolder, "Traefik");
        var traefikExe = Path.Combine(traefikFolder, "traefik.exe");

        if (!File.Exists(traefikExe))
        {
            _logger.LogDebug("TraefikHostedService: traefik.exe not found at {Path}, Traefik feature not installed", traefikExe);
            return;
        }

        // Require ACME email - without it, Traefik can't get certificates
        var acmeEmail = _configuration["TRAEFIK_ACME_EMAIL"];
        if (string.IsNullOrEmpty(acmeEmail))
        {
            _logger.LogInformation("TraefikHostedService: TRAEFIK_ACME_EMAIL not configured, skipping Traefik startup");
            return;
        }

        try
        {
            // The WAF starts first, so the router only lists it once it is actually answering.
            var wafRunning = await StartWafAsync(traefikFolder, cancellationToken);
            await GenerateConfigsAsync(traefikFolder, wafRunning);
            await StartTraefikAsync(traefikFolder, traefikExe, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start Traefik");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopTraefik();
        StopWaf();
        return Task.CompletedTask;
    }

    private async Task GenerateConfigsAsync(string traefikFolder, bool wafRunning)
    {
        var templatesFolder = Path.Combine(traefikFolder, "templates");
        var dynamicFolder = Path.Combine(traefikFolder, "dynamic");
        var acmeFolder = Path.Combine(traefikFolder, "acme");
        var logsFolder = Path.Combine(traefikFolder, "logs");

        // Ensure directories exist
        Directory.CreateDirectory(dynamicFolder);
        Directory.CreateDirectory(acmeFolder);
        Directory.CreateDirectory(logsFolder);

        // Generate static config (traefik.yml)
        var staticTemplate = Path.Combine(templatesFolder, "traefik.yml.template");
        if (File.Exists(staticTemplate))
        {
            var template = await File.ReadAllTextAsync(staticTemplate);
            var config = template
                .Replace("{{LISTEN_IP}}", GetConfigValue("TRAEFIK_LISTEN_IP", "0.0.0.0"))
                .Replace("{{ACME_EMAIL}}", GetConfigValue("TRAEFIK_ACME_EMAIL", ""))
                .Replace("{{DYNAMIC_DIR}}", dynamicFolder.Replace("\\", "/"))
                .Replace("{{ACME_STORAGE_PATH}}", Path.Combine(acmeFolder, "acme.json").Replace("\\", "/"))
                .Replace("{{ACCESS_LOG_PATH}}", Path.Combine(logsFolder, "access.log").Replace("\\", "/"))
                .Replace("{{LOG_LEVEL}}", GetConfigValue("TRAEFIK_LOG_LEVEL", "INFO"));

            await File.WriteAllTextAsync(Path.Combine(traefikFolder, "traefik.yml"), config);
            _logger.LogInformation("Generated traefik.yml");
        }
        else
        {
            _logger.LogWarning("traefik.yml.template not found at {Path}", staticTemplate);
        }

        // Generate dynamic config (dynamic/config.yml)
        var dynamicTemplate = Path.Combine(templatesFolder, "config.yml.template");
        if (File.Exists(dynamicTemplate))
        {
            var template = await File.ReadAllTextAsync(dynamicTemplate);
            var config = template
                .Replace("{{OPTIMIZER_HOSTNAME}}", GetConfigValue("TRAEFIK_OPTIMIZER_HOSTNAME", "optimizer.example.com"))
                .Replace("{{SPEEDTEST_HOSTNAME}}", GetConfigValue("TRAEFIK_SPEEDTEST_HOSTNAME", "speedtest.example.com"))
                .Replace("{{SPEEDTEST_PORT}}", GetConfigValue("OPENSPEEDTEST_PORT", "3005"))
                // Same key the app reads to bind the agent tunnel listener (Program.cs).
                .Replace("{{TUNNEL_PORT}}", GetConfigValue("AgentTunnel:Port", "8043"))
                // A router listing a WAF that is not running answers 500, so add it only when it is.
                .Replace("# {{WAF_MIDDLEWARE}}", wafRunning ? "- waf" : "# waf (set TRAEFIK_WAF_MODE to enable)");

            await File.WriteAllTextAsync(Path.Combine(dynamicFolder, "config.yml"), config);
            _logger.LogInformation("Generated dynamic/config.yml");
        }
        else
        {
            _logger.LogWarning("config.yml.template not found at {Path}", dynamicTemplate);
        }
    }

    private string GetConfigValue(string key, string defaultValue)
    {
        var value = _configuration[key];
        return string.IsNullOrEmpty(value) ? defaultValue : value;
    }

    private async Task StartTraefikAsync(string traefikFolder, string traefikExe, CancellationToken cancellationToken)
    {
        StopTraefik();

        var configFile = Path.Combine(traefikFolder, "traefik.yml");
        if (!File.Exists(configFile))
        {
            _logger.LogError("TraefikHostedService: traefik.yml not found at {Path}", configFile);
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = traefikExe,
            Arguments = $"--configFile=\"{configFile}\"",
            WorkingDirectory = traefikFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Inject CF_DNS_API_TOKEN via process environment - never written to disk
        var cfToken = _configuration["TRAEFIK_CF_DNS_API_TOKEN"];
        if (!string.IsNullOrEmpty(cfToken))
        {
            startInfo.Environment["CF_DNS_API_TOKEN"] = cfToken;
        }
        else
        {
            _logger.LogWarning("TraefikHostedService: TRAEFIK_CF_DNS_API_TOKEN not set, certificate issuance will fail");
        }

        _logger.LogInformation("Starting Traefik with config: {Config}", configFile);

        _traefikProcess = new Process { StartInfo = startInfo };

        _traefikProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("traefik: {Output}", e.Data);
        };

        _traefikProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogWarning("traefik error: {Error}", e.Data);
        };

        _traefikProcess.Start();
        _traefikProcess.BeginOutputReadLine();
        _traefikProcess.BeginErrorReadLine();

        // Wait briefly to check if Traefik started successfully
        await Task.Delay(1000, cancellationToken);

        if (_traefikProcess.HasExited)
        {
            _logger.LogError("Traefik exited immediately with code {ExitCode}", _traefikProcess.ExitCode);
            _traefikProcess = null;
        }
        else
        {
            _logger.LogInformation("Traefik started successfully (PID: {Pid}) on ports 80/443", _traefikProcess.Id);
        }
    }

    /// <summary>
    /// Starts the bundled netopt-waf when TRAEFIK_WAF_MODE is detect or block and the binary is installed.
    /// Returns true only when the process is up.
    /// </summary>
    private async Task<bool> StartWafAsync(string traefikFolder, CancellationToken cancellationToken)
    {
        StopWaf();

        var mode = GetConfigValue("TRAEFIK_WAF_MODE", "off").Trim().ToLowerInvariant();
        if (mode is not ("detect" or "block"))
            return false;

        var wafExe = Path.Combine(traefikFolder, "netopt-waf.exe");
        if (!File.Exists(wafExe))
        {
            _logger.LogWarning("TRAEFIK_WAF_MODE is {Mode} but netopt-waf.exe is not installed at {Path}", mode, wafExe);
            return false;
        }

        var token = await EnsureLocalWafConnectionAsync();
        var rulesDir = Path.Combine(traefikFolder, "waf-rules");
        Directory.CreateDirectory(rulesDir);

        var startInfo = new ProcessStartInfo
        {
            FileName = wafExe,
            WorkingDirectory = traefikFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["WAF_LISTEN"] = new Uri(LocalWafUrl).Authority;
        startInfo.Environment["WAF_MODE"] = mode;
        startInfo.Environment["WAF_RULES_DIR"] = rulesDir;
        if (token != null)
            startInfo.Environment["WAF_API_TOKEN"] = token;

        _wafProcess = new Process { StartInfo = startInfo };
        // netopt-waf logs everything (start-up and events) through the Go log package, which writes stderr.
        _wafProcess.OutputDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogDebug("netopt-waf: {Output}", e.Data); };
        _wafProcess.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) _logger.LogDebug("netopt-waf: {Output}", e.Data); };
        _wafProcess.Start();
        _wafProcess.BeginOutputReadLine();
        _wafProcess.BeginErrorReadLine();

        // Loading the Core Rule Set takes a moment; the router must not point at a WAF that died on start.
        await Task.Delay(1500, cancellationToken);
        if (_wafProcess.HasExited)
        {
            _logger.LogError("netopt-waf exited immediately with code {ExitCode}; the app router runs without it", _wafProcess.ExitCode);
            _wafProcess.Dispose();
            _wafProcess = null;
            return false;
        }

        _logger.LogInformation("netopt-waf started in {Mode} mode (PID: {Pid})", mode, _wafProcess.Id);
        return true;
    }

    /// <summary>
    /// Points Threat Intelligence at the bundled WAF and returns its API token. Reuses the stored token
    /// when the stored URL is already the local WAF; never replaces a WAF the admin configured elsewhere.
    /// </summary>
    private async Task<string?> EnsureLocalWafConnectionAsync()
    {
        try
        {
            var settings = _services.GetRequiredService<SystemSettingsService>();
            var credentials = _services.GetRequiredService<ICredentialProtectionService>();

            var storedUrl = await settings.GetGlobalAsync(WafSettingKeys.Url);
            var storedToken = await settings.GetGlobalAsync(WafSettingKeys.Token);
            var isLocal = string.Equals(storedUrl?.TrimEnd('/'), LocalWafUrl, StringComparison.OrdinalIgnoreCase);

            if (isLocal && !string.IsNullOrEmpty(storedToken))
                return credentials.IsEncrypted(storedToken) ? credentials.Decrypt(storedToken) : storedToken;

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            if (!string.IsNullOrEmpty(storedUrl) && !isLocal)
            {
                _logger.LogInformation("Threat Intelligence reads a WAF at {Url}; the bundled WAF runs unconnected", storedUrl);
                return token;
            }

            await settings.SetGlobalAsync(WafSettingKeys.Url, LocalWafUrl);
            await settings.SetGlobalAsync(WafSettingKeys.Token, credentials.Encrypt(token));
            await settings.SetGlobalAsync(WafSettingKeys.Enabled, "true");
            _services.GetService<IAuditLogger>()?.Log(AuditEventBuilder.FromSystem(
                AuditCategories.Settings,
                AuditActions.SettingsChanged,
                targetType: "setting",
                targetName: WafSettingKeys.Url,
                details: new { change = "bundled_waf_connected", reason = "TRAEFIK_WAF_MODE started the bundled netopt-waf" }));
            return token;
        }
        catch (Exception ex)
        {
            // The WAF still filters without a token; only the events API stays off.
            _logger.LogWarning(ex, "Could not connect Threat Intelligence to the bundled WAF");
            return null;
        }
    }

    private void StopWaf()
    {
        try
        {
            if (_wafProcess is { HasExited: false })
            {
                _logger.LogInformation("Stopping netopt-waf (PID: {Pid})", _wafProcess.Id);
                _wafProcess.Kill(entireProcessTree: true);
                _wafProcess.WaitForExit(5000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping netopt-waf");
        }
        finally
        {
            _wafProcess?.Dispose();
            _wafProcess = null;
        }
    }

    private void StopTraefik()
    {
        try
        {
            if (_traefikProcess is { HasExited: false })
            {
                _logger.LogInformation("Stopping Traefik (PID: {Pid})", _traefikProcess.Id);
                _traefikProcess.Kill(entireProcessTree: true);
                _traefikProcess.WaitForExit(5000);
            }

            _logger.LogInformation("Traefik stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping Traefik");
        }
        finally
        {
            _traefikProcess?.Dispose();
            _traefikProcess = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopTraefik();
        StopWaf();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
