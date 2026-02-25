using System.Diagnostics;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages Traefik as a child process for HTTPS reverse proxying.
/// Active on Windows only when the Traefik feature is installed (traefik.exe present).
/// Generates static and dynamic configs from templates using registry values on each startup.
/// CF_DNS_API_TOKEN is injected via process environment variable, never written to disk.
/// </summary>
public class TraefikHostedService : IHostedService, IDisposable
{
    private readonly ILogger<TraefikHostedService> _logger;
    private readonly IConfiguration _configuration;
    private Process? _traefikProcess;
    private readonly string _installFolder;
    private bool _disposed;

    public TraefikHostedService(ILogger<TraefikHostedService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
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
            await GenerateConfigsAsync(traefikFolder);
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
        return Task.CompletedTask;
    }

    private async Task GenerateConfigsAsync(string traefikFolder)
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
                .Replace("{{SPEEDTEST_PORT}}", GetConfigValue("OPENSPEEDTEST_PORT", "3005"));

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
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
