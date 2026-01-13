using System.Diagnostics;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Manages nginx as a child process for serving OpenSpeedTest.
/// Only active on Windows when the SpeedTest feature is installed.
/// </summary>
public class NginxHostedService : IHostedService, IDisposable
{
    private readonly ILogger<NginxHostedService> _logger;
    private readonly IConfiguration _configuration;
    private Process? _nginxProcess;
    private readonly string _installFolder;
    private bool _disposed;

    public NginxHostedService(ILogger<NginxHostedService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;

        // Use the application's base directory (works for any install location)
        _installFolder = AppContext.BaseDirectory;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Only run on Windows
        if (!OperatingSystem.IsWindows())
        {
            _logger.LogDebug("NginxHostedService: Not running on Windows, skipping");
            return;
        }

        var speedTestFolder = Path.Combine(_installFolder, "SpeedTest");
        var nginxPath = Path.Combine(speedTestFolder, "nginx.exe");

        // Check if SpeedTest feature is installed
        if (!File.Exists(nginxPath))
        {
            _logger.LogDebug("NginxHostedService: nginx not found at {Path}, SpeedTest feature not installed", nginxPath);
            return;
        }

        try
        {
            // Generate config.js from template before starting nginx
            await GenerateConfigJsAsync(speedTestFolder);

            // Start nginx
            await StartNginxAsync(speedTestFolder, nginxPath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start nginx for OpenSpeedTest");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopNginx();
        return Task.CompletedTask;
    }

    private async Task GenerateConfigJsAsync(string speedTestFolder)
    {
        var templatePath = Path.Combine(speedTestFolder, "config.js.template");
        var outputPath = Path.Combine(speedTestFolder, "html", "assets", "js", "config.js");

        if (!File.Exists(templatePath))
        {
            _logger.LogWarning("config.js.template not found at {Path}", templatePath);
            return;
        }

        // Read configuration values
        var config = await LoadConfigurationAsync();

        // Construct the save URL based on configuration
        const string apiPath = "/api/public/speedtest/results";
        var saveDataUrl = ConstructSaveDataUrl(config, apiPath);

        // Read template and replace placeholders (matches OpenSpeedTest format)
        var template = await File.ReadAllTextAsync(templatePath);
        var configJs = template
            .Replace("{{SAVE_DATA}}", "true")
            .Replace("{{SAVE_DATA_URL}}", saveDataUrl)
            .Replace("{{API_PATH}}", apiPath);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        await File.WriteAllTextAsync(outputPath, configJs);
        _logger.LogInformation("Generated config.js with save URL: {SaveUrl}", saveDataUrl);
    }

    private Task<Dictionary<string, string>> LoadConfigurationAsync()
    {
        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Load from Windows Registry (set by installer)
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Ozark Connect\Network Optimizer");
                if (key != null)
                {
                    LoadRegistryValue(config, key, "HOST_IP");
                    LoadRegistryValue(config, key, "HOST_NAME");
                    LoadRegistryValue(config, key, "REVERSE_PROXIED_HOST_NAME");
                    LoadRegistryValue(config, key, "OPENSPEEDTEST_PORT");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read configuration from registry");
            }
        }

        // Override with configuration from appsettings/environment variables
        OverrideFromConfiguration(config, "HOST_IP");
        OverrideFromConfiguration(config, "HOST_NAME");
        OverrideFromConfiguration(config, "REVERSE_PROXIED_HOST_NAME");
        OverrideFromConfiguration(config, "OPENSPEEDTEST_PORT");

        return Task.FromResult(config);
    }

    private static void LoadRegistryValue(Dictionary<string, string> config, Microsoft.Win32.RegistryKey key, string name)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var value = key.GetValue(name) as string;
        if (!string.IsNullOrEmpty(value))
        {
            config[name] = value;
        }
    }

    private void OverrideFromConfiguration(Dictionary<string, string> config, string key)
    {
        var value = _configuration[key];
        if (!string.IsNullOrEmpty(value))
        {
            config[key] = value;
        }
    }

    private string ConstructSaveDataUrl(Dictionary<string, string> config, string apiPath)
    {
        // Priority: REVERSE_PROXIED_HOST_NAME (https) > HOST_NAME (http) > HOST_IP (http) > __DYNAMIC__
        // IMPORTANT: Keep this logic in sync with docker/openspeedtest/entrypoint.sh (Docker deployment)
        config.TryGetValue("REVERSE_PROXIED_HOST_NAME", out var reverseProxy);
        config.TryGetValue("HOST_NAME", out var hostName);
        config.TryGetValue("HOST_IP", out var hostIp);

        if (!string.IsNullOrEmpty(reverseProxy))
        {
            // Reverse proxy mode - HTTPS, no port needed
            return $"https://{reverseProxy}{apiPath}";
        }
        else if (!string.IsNullOrEmpty(hostName))
        {
            // Hostname mode - HTTP with port
            return $"http://{hostName}:8042{apiPath}";
        }
        else if (!string.IsNullOrEmpty(hostIp))
        {
            // IP mode - HTTP with port
            return $"http://{hostIp}:8042{apiPath}";
        }
        else
        {
            // No explicit host configured - use dynamic URL (constructed client-side from browser location)
            return "__DYNAMIC__";
        }
    }

    private void CreateNginxTempDirectories(string speedTestFolder)
    {
        // nginx requires these temp directories to exist
        var tempDirs = new[]
        {
            "temp/client_body_temp",
            "temp/proxy_temp",
            "temp/fastcgi_temp",
            "temp/uwsgi_temp",
            "temp/scgi_temp",
            "logs"
        };

        foreach (var dir in tempDirs)
        {
            var fullPath = Path.Combine(speedTestFolder, dir);
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                _logger.LogDebug("Created nginx directory: {Path}", fullPath);
            }
        }
    }

    private async Task StartNginxAsync(string speedTestFolder, string nginxPath, CancellationToken cancellationToken)
    {
        // Stop any existing nginx process first
        StopNginx();

        // Create required temp directories for nginx
        CreateNginxTempDirectories(speedTestFolder);

        // nginx needs explicit config path and prefix
        var confPath = Path.Combine(speedTestFolder, "conf", "nginx.conf");
        var startInfo = new ProcessStartInfo
        {
            FileName = nginxPath,
            Arguments = $"-p \"{speedTestFolder}\" -c \"{confPath}\"",
            WorkingDirectory = speedTestFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _logger.LogInformation("Starting nginx with prefix: {Prefix}, config: {Config}", speedTestFolder, confPath);

        _nginxProcess = new Process { StartInfo = startInfo };

        _nginxProcess.OutputDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogDebug("nginx: {Output}", e.Data);
        };

        _nginxProcess.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                _logger.LogWarning("nginx error: {Error}", e.Data);
        };

        _nginxProcess.Start();
        _nginxProcess.BeginOutputReadLine();
        _nginxProcess.BeginErrorReadLine();

        // Wait briefly to check if nginx started successfully
        await Task.Delay(500, cancellationToken);

        if (_nginxProcess.HasExited)
        {
            _logger.LogError("nginx exited immediately with code {ExitCode}", _nginxProcess.ExitCode);
            _nginxProcess = null;
        }
        else
        {
            _logger.LogInformation("nginx started successfully (PID: {Pid}) serving OpenSpeedTest on port 3005", _nginxProcess.Id);
        }
    }

    private void StopNginx()
    {
        if (_nginxProcess == null || _nginxProcess.HasExited)
            return;

        try
        {
            _logger.LogInformation("Stopping nginx (PID: {Pid})", _nginxProcess.Id);

            // nginx on Windows: send WM_QUIT or kill the process
            // nginx -s stop would be cleaner but requires a separate process
            _nginxProcess.Kill(entireProcessTree: true);
            _nginxProcess.WaitForExit(5000);

            _logger.LogInformation("nginx stopped");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping nginx");
        }
        finally
        {
            _nginxProcess?.Dispose();
            _nginxProcess = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        StopNginx();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
