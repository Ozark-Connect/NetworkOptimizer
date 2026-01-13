# OpenSpeedTest Windows Installer Integration

This document details all changes made to integrate OpenSpeedTest with nginx into the Windows MSI installer.

## Overview

The goal was to bundle OpenSpeedTest (browser-based speed test) with nginx into the Windows installer, with:
- nginx managed as a child process by the main NetworkOptimizer service
- Configuration collected during installation and stored in Windows Registry
- Automatic config.js generation at runtime

## Files Created/Modified

### 1. NginxHostedService.cs (NEW)

**Path:** `src/NetworkOptimizer.Web/Services/NginxHostedService.cs`

This hosted service manages nginx as a child process on Windows.

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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

        // Default install location: C:\Program Files\Ozark Connect\Network Optimizer
        _installFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Ozark Connect", "Network Optimizer");
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
        var saveUrl = ConstructSaveUrl(config);

        // Read template and replace placeholder
        var template = await File.ReadAllTextAsync(templatePath);
        var configJs = template.Replace("{{SAVE_URL}}", saveUrl);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        await File.WriteAllTextAsync(outputPath, configJs);
        _logger.LogInformation("Generated config.js with save URL: {SaveUrl}", saveUrl);
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

    private string ConstructSaveUrl(Dictionary<string, string> config)
    {
        // Priority: REVERSE_PROXIED_HOST_NAME (https) > HOST_NAME (http) > HOST_IP (http)
        config.TryGetValue("REVERSE_PROXIED_HOST_NAME", out var reverseProxy);
        config.TryGetValue("HOST_NAME", out var hostName);
        config.TryGetValue("HOST_IP", out var hostIp);

        string scheme;
        string host;
        string port;

        if (!string.IsNullOrEmpty(reverseProxy))
        {
            // Reverse proxy mode - HTTPS, no port needed
            scheme = "https";
            host = reverseProxy;
            port = "";
        }
        else if (!string.IsNullOrEmpty(hostName))
        {
            // Hostname mode - HTTP with port
            scheme = "http";
            host = hostName;
            port = ":8042";
        }
        else if (!string.IsNullOrEmpty(hostIp))
        {
            // IP mode - HTTP with port
            scheme = "http";
            host = hostIp;
            port = ":8042";
        }
        else
        {
            // Fallback to localhost
            scheme = "http";
            host = "localhost";
            port = ":8042";
        }

        return $"{scheme}://{host}{port}/api/public/speedtest/results";
    }

    private async Task StartNginxAsync(string speedTestFolder, string nginxPath, CancellationToken cancellationToken)
    {
        // Stop any existing nginx process first
        StopNginx();

        // nginx needs to run from its directory
        var startInfo = new ProcessStartInfo
        {
            FileName = nginxPath,
            WorkingDirectory = speedTestFolder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

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
```

### 2. NetworkOptimizer.Web.csproj (MODIFIED)

**Path:** `src/NetworkOptimizer.Web/NetworkOptimizer.Web.csproj`

Add the Windows Services package:

```xml
<PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.0" />
```

### 3. Program.cs (MODIFIED)

**Path:** `src/NetworkOptimizer.Web/Program.cs`

**First change**: Add Windows Service support after `var builder = WebApplication.CreateBuilder(args);`:

```csharp
// Windows Service support (no-op when running as console or on non-Windows)
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "NetworkOptimizer";
    });
}
```

**Second change**: Add these lines after the Iperf3ServerService registration (around line 124):

```csharp
// Register nginx hosted service (Windows only - manages nginx for OpenSpeedTest)
builder.Services.AddHostedService<NginxHostedService>();
```

**Note:** The Windows Service support was already added in the initial installer implementation:
```csharp
// Windows Service support (no-op when running as console or on non-Windows)
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = "NetworkOptimizer";
    });
}
```

### 4. ConfigDialog.wxs (MODIFIED)

**Path:** `src/NetworkOptimizer.Installer/ConfigDialog.wxs`

Fixed WiX v6 syntax issues (empty property values, Publish element conditions):

```xml
<?xml version="1.0" encoding="UTF-8"?>

<!--
  Network Optimizer - Configuration Dialog

  Custom dialog for network configuration during installation.
  Collects HOST_IP, HOST_NAME, and other settings.
-->

<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:ui="http://wixtoolset.org/schemas/v4/wxs/ui">

  <Fragment>
    <!-- Define properties with defaults (Secure=yes allows command-line override) -->
    <Property Id="HOST_IP" Secure="yes" />
    <Property Id="HOST_NAME" Secure="yes" />
    <Property Id="REVERSE_PROXIED_HOST_NAME" Secure="yes" />
    <Property Id="OPENSPEEDTEST_PORT" Value="3005" Secure="yes" />

    <!-- Use WixUI_InstallDir with custom dialog sequence -->
    <ui:WixUI Id="WixUI_InstallDir" InstallDirectory="INSTALLFOLDER" />

    <!-- Custom dialog for network configuration -->
    <UI>
      <Dialog Id="NetworkConfigDlg" Width="370" Height="270" Title="Network Configuration">
        <Control Id="Title" Type="Text" X="15" Y="6" Width="200" Height="15" Transparent="yes" NoPrefix="yes" Text="{\WixUI_Font_Title}Network Configuration" />
        <Control Id="Description" Type="Text" X="25" Y="23" Width="340" Height="20" Transparent="yes" NoPrefix="yes" Text="Configure network settings for speed test result reporting." />
        <Control Id="BannerLine" Type="Line" X="0" Y="44" Width="370" Height="0" />

        <!-- HOST_IP -->
        <Control Id="HostIPLabel" Type="Text" X="20" Y="60" Width="100" Height="15" Text="Server IP Address:" />
        <Control Id="HostIPEdit" Type="Edit" X="130" Y="58" Width="200" Height="18" Property="HOST_IP" />
        <Control Id="HostIPHelp" Type="Text" X="130" Y="78" Width="220" Height="20" Text="e.g., 192.168.1.100 (for path analysis)" NoPrefix="yes" />

        <!-- HOST_NAME -->
        <Control Id="HostNameLabel" Type="Text" X="20" Y="100" Width="100" Height="15" Text="Server Hostname:" />
        <Control Id="HostNameEdit" Type="Edit" X="130" Y="98" Width="200" Height="18" Property="HOST_NAME" />
        <Control Id="HostNameHelp" Type="Text" X="130" Y="118" Width="220" Height="20" Text="e.g., nas, server.local (requires DNS)" NoPrefix="yes" />

        <!-- OPENSPEEDTEST_PORT -->
        <Control Id="PortLabel" Type="Text" X="20" Y="140" Width="100" Height="15" Text="SpeedTest Port:" />
        <Control Id="PortEdit" Type="Edit" X="130" Y="138" Width="60" Height="18" Property="OPENSPEEDTEST_PORT" />
        <Control Id="PortHelp" Type="Text" X="200" Y="140" Width="150" Height="15" Text="Default: 3005" NoPrefix="yes" />

        <!-- REVERSE_PROXIED_HOST_NAME -->
        <Control Id="ProxyLabel" Type="Text" X="20" Y="170" Width="110" Height="15" Text="Reverse Proxy Host:" />
        <Control Id="ProxyEdit" Type="Edit" X="130" Y="168" Width="200" Height="18" Property="REVERSE_PROXIED_HOST_NAME" />
        <Control Id="ProxyHelp" Type="Text" X="130" Y="188" Width="220" Height="20" Text="e.g., optimizer.example.com (if using HTTPS proxy)" NoPrefix="yes" />

        <!-- Info text -->
        <Control Id="InfoText" Type="Text" X="20" Y="210" Width="330" Height="25" Text="Leave fields blank to use automatic detection. Speed test results will be sent to the main application on port 8042." NoPrefix="yes" />

        <!-- Bottom line and buttons -->
        <Control Id="BottomLine" Type="Line" X="0" Y="234" Width="370" Height="0" />
        <Control Id="Back" Type="PushButton" X="180" Y="243" Width="56" Height="17" Text="&amp;Back">
          <Publish Event="NewDialog" Value="InstallDirDlg" />
        </Control>
        <Control Id="Next" Type="PushButton" X="236" Y="243" Width="56" Height="17" Default="yes" Text="&amp;Next">
          <Publish Event="NewDialog" Value="VerifyReadyDlg" />
        </Control>
        <Control Id="Cancel" Type="PushButton" X="304" Y="243" Width="56" Height="17" Cancel="yes" Text="Cancel">
          <Publish Event="SpawnDialog" Value="CancelDlg" />
        </Control>
      </Dialog>

      <!-- Insert our dialog after InstallDirDlg -->
      <Publish Dialog="InstallDirDlg" Control="Next" Event="NewDialog" Value="NetworkConfigDlg" Order="2" Condition="1" />
      <Publish Dialog="VerifyReadyDlg" Control="Back" Event="NewDialog" Value="NetworkConfigDlg" Order="2" Condition="NOT Installed" />
    </UI>

  </Fragment>

</Wix>
```

### 5. SpeedTestComponent.wxs (MODIFIED)

**Path:** `src/NetworkOptimizer.Installer/SpeedTestComponent.wxs`

Changed from IniFile to Windows Registry for configuration storage:

```xml
<?xml version="1.0" encoding="UTF-8"?>

<!--
  Network Optimizer - SpeedTest Component (OpenSpeedTest + nginx)

  Bundles nginx for Windows and OpenSpeedTest static files.
  nginx is managed as a child process by the main NetworkOptimizer service.
-->

<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs"
     xmlns:fw="http://wixtoolset.org/schemas/v4/wxs/firewall">

  <Fragment>
    <!-- SpeedTest directory under main install folder -->
    <DirectoryRef Id="INSTALLFOLDER">
      <Directory Id="SpeedTestFolder" Name="SpeedTest">
        <Directory Id="SpeedTestConfFolder" Name="conf" />
        <Directory Id="SpeedTestLogsFolder" Name="logs" />
        <Directory Id="SpeedTestHtmlFolder" Name="html">
          <Directory Id="SpeedTestAssetsFolder" Name="assets">
            <Directory Id="SpeedTestCssFolder" Name="css" />
            <Directory Id="SpeedTestJsFolder" Name="js" />
            <Directory Id="SpeedTestFontsFolder" Name="fonts" />
            <Directory Id="SpeedTestImagesFolder" Name="images">
              <Directory Id="SpeedTestIconsFolder" Name="icons" />
            </Directory>
          </Directory>
        </Directory>
      </Directory>
    </DirectoryRef>

    <ComponentGroup Id="SpeedTestComponents">

      <!-- nginx executable and core files -->
      <!-- Note: nginx is managed as a child process by the main NetworkOptimizer service -->
      <Component Id="NginxExecutable" Directory="SpeedTestFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789001" Bitness="always64">
        <File Id="NginxExe"
              Source="$(var.SpeedTestDir)nginx\nginx.exe"
              KeyPath="yes" />

        <!-- Firewall exception for SpeedTest (port 3005) -->
        <fw:FirewallException
            Id="SpeedTestFirewall"
            Name="Network Optimizer SpeedTest"
            Port="3005"
            Protocol="tcp"
            Scope="any"
            Profile="all" />
      </Component>

      <!-- nginx conf folder -->
      <Component Id="NginxConf" Directory="SpeedTestConfFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789002" Bitness="always64">
        <File Id="NginxConfFile"
              Source="$(var.InstallerDir)SpeedTest\nginx.conf"
              Name="nginx.conf"
              KeyPath="yes" />
        <File Id="NginxMimeTypes"
              Source="$(var.SpeedTestDir)nginx\conf\mime.types"
              Name="mime.types" />
      </Component>

      <!-- nginx logs folder (empty, created for nginx) -->
      <Component Id="NginxLogsFolder" Directory="SpeedTestLogsFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789003" Bitness="always64">
        <CreateFolder />
        <RemoveFolder Id="RemoveLogsFolder" On="uninstall" />
      </Component>

      <!-- Startup script and config template -->
      <Component Id="SpeedTestScripts" Directory="SpeedTestFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789004" Bitness="always64">
        <File Id="StartSpeedTestPs1"
              Source="$(var.InstallerDir)SpeedTest\Start-SpeedTest.ps1"
              KeyPath="yes" />
        <File Id="ConfigJsTemplate"
              Source="$(var.InstallerDir)SpeedTest\config.js.template" />
      </Component>

      <!-- Configuration stored in Windows Registry (cleaner than config files for Windows) -->
      <Component Id="ConfigRegistry" Directory="SpeedTestFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789005" Bitness="always64">
        <RegistryKey Root="HKLM" Key="SOFTWARE\Ozark Connect\Network Optimizer">
          <RegistryValue Type="string" Name="HOST_IP" Value="[HOST_IP]" />
          <RegistryValue Type="string" Name="HOST_NAME" Value="[HOST_NAME]" />
          <RegistryValue Type="string" Name="REVERSE_PROXIED_HOST_NAME" Value="[REVERSE_PROXIED_HOST_NAME]" />
          <RegistryValue Type="string" Name="OPENSPEEDTEST_PORT" Value="[OPENSPEEDTEST_PORT]" KeyPath="yes" />
        </RegistryKey>
      </Component>

    </ComponentGroup>

    <!-- OpenSpeedTest HTML files - harvested from src/OpenSpeedTest -->
    <ComponentGroup Id="SpeedTestHtmlFiles">
      <Component Id="SpeedTestIndexHtml" Directory="SpeedTestHtmlFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789010" Bitness="always64">
        <File Id="IndexHtml" Source="$(var.OpenSpeedTestDir)index.html" KeyPath="yes" />
        <File Id="HostedHtml" Source="$(var.OpenSpeedTestDir)hosted.html" />
        <File Id="DownloadingFile" Source="$(var.OpenSpeedTestDir)downloading" />
        <File Id="UploadFile" Source="$(var.OpenSpeedTestDir)upload" />
      </Component>
    </ComponentGroup>

    <!-- OpenSpeedTest CSS files -->
    <ComponentGroup Id="SpeedTestCssFiles">
      <Component Id="SpeedTestCss" Directory="SpeedTestCssFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789011" Bitness="always64">
        <File Id="AppCss" Source="$(var.OpenSpeedTestDir)assets\css\app.css" KeyPath="yes" />
        <File Id="DarkmodeCss" Source="$(var.OpenSpeedTestDir)assets\css\darkmode.css" />
        <File Id="OzarkOverridesCss" Source="$(var.OpenSpeedTestDir)assets\css\ozark-overrides.css" />
      </Component>
    </ComponentGroup>

    <!-- OpenSpeedTest JS files -->
    <ComponentGroup Id="SpeedTestJsFiles">
      <Component Id="SpeedTestJs" Directory="SpeedTestJsFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789012" Bitness="always64">
        <File Id="AppJs" Source="$(var.OpenSpeedTestDir)assets\js\app-2.5.4.js" KeyPath="yes" />
        <File Id="AppMinJs" Source="$(var.OpenSpeedTestDir)assets\js\app-2.5.4.min.js" />
        <File Id="DarkmodeJs" Source="$(var.OpenSpeedTestDir)assets\js\darkmode.js" />
        <File Id="GeolocationJs" Source="$(var.OpenSpeedTestDir)assets\js\geolocation.js" />
        <!-- config.js is generated at runtime, but we need the template -->
      </Component>
    </ComponentGroup>

    <!-- OpenSpeedTest Images -->
    <ComponentGroup Id="SpeedTestImageFiles">
      <Component Id="SpeedTestImages" Directory="SpeedTestImagesFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789013" Bitness="always64">
        <File Id="AppSvg" Source="$(var.OpenSpeedTestDir)assets\images\app.svg" KeyPath="yes" />
        <File Id="OzarkLogoSvg" Source="$(var.OpenSpeedTestDir)assets\images\ozark-connect-logo.svg" />
      </Component>
    </ComponentGroup>

    <!-- OpenSpeedTest Icons -->
    <ComponentGroup Id="SpeedTestIconFiles">
      <Component Id="SpeedTestIcons" Directory="SpeedTestIconsFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789014" Bitness="always64">
        <File Id="FaviconIco" Source="$(var.OpenSpeedTestDir)assets\images\icons\favicon.ico" KeyPath="yes" />
        <File Id="Favicon16" Source="$(var.OpenSpeedTestDir)assets\images\icons\favicon-16x16.png" />
        <File Id="Favicon32" Source="$(var.OpenSpeedTestDir)assets\images\icons\favicon-32x32.png" />
        <File Id="AppleTouchIcon" Source="$(var.OpenSpeedTestDir)assets\images\icons\apple-touch-icon.png" />
        <File Id="AndroidChrome192" Source="$(var.OpenSpeedTestDir)assets\images\icons\android-chrome-192x192.png" />
        <File Id="AndroidChrome512" Source="$(var.OpenSpeedTestDir)assets\images\icons\android-chrome-512x512.png" />
        <File Id="LauncherIcon1x" Source="$(var.OpenSpeedTestDir)assets\images\icons\launcher-icon-1x.png" />
        <File Id="LauncherIcon2x" Source="$(var.OpenSpeedTestDir)assets\images\icons\launcher-icon-2x.png" />
        <File Id="LauncherIcon3x" Source="$(var.OpenSpeedTestDir)assets\images\icons\launcher-icon-3x.png" />
        <File Id="LauncherIcon4x" Source="$(var.OpenSpeedTestDir)assets\images\icons\launcher-icon-4x.png" />
        <File Id="Mstile150" Source="$(var.OpenSpeedTestDir)assets\images\icons\mstile-150x150.png" />
        <File Id="SafariPinnedTab" Source="$(var.OpenSpeedTestDir)assets\images\icons\safari-pinned-tab.svg" />
        <File Id="WebManifest" Source="$(var.OpenSpeedTestDir)assets\images\icons\site.webmanifest" />
        <File Id="BrowserConfig" Source="$(var.OpenSpeedTestDir)assets\images\icons\browserconfig.xml" />
      </Component>
    </ComponentGroup>

    <!-- OpenSpeedTest Fonts -->
    <ComponentGroup Id="SpeedTestFontFiles">
      <Component Id="SpeedTestFonts" Directory="SpeedTestFontsFolder" Guid="C1D2E3F4-A5B6-7890-CDEF-123456789015" Bitness="always64">
        <File Id="Roboto500Woff2" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-500.woff2" KeyPath="yes" />
        <File Id="Roboto500Woff" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-500.woff" />
        <File Id="Roboto500Ttf" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-500.ttf" />
        <File Id="Roboto500Eot" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-500.eot" />
        <File Id="Roboto500Svg" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-500.svg" />
        <File Id="RobotoRegularWoff2" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-regular.woff2" />
        <File Id="RobotoRegularWoff" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-regular.woff" />
        <File Id="RobotoRegularTtf" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-regular.ttf" />
        <File Id="RobotoRegularEot" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-regular.eot" />
        <File Id="RobotoRegularSvg" Source="$(var.OpenSpeedTestDir)assets\fonts\roboto-v30-latin-regular.svg" />
      </Component>
    </ComponentGroup>

  </Fragment>

</Wix>
```

### 6. Download-Nginx.ps1 (EXISTS - run manually if needed)

**Path:** `src/NetworkOptimizer.Installer/SpeedTest/Download-Nginx.ps1`

This script downloads nginx for Windows. Run it manually before building:

```powershell
cd src/NetworkOptimizer.Installer
powershell -ExecutionPolicy Bypass -File "SpeedTest\Download-Nginx.ps1"
```

### 7. config.js.template (SHOULD EXIST)

**Path:** `src/NetworkOptimizer.Installer/SpeedTest/config.js.template`

Template file for OpenSpeedTest configuration:

```javascript
// OpenSpeedTest Configuration
// Generated at runtime by NetworkOptimizer service
window.OPENSPEEDTEST_CONFIG = {
    saveUrl: "{{SAVE_URL}}"
};
```

### 8. nginx.conf (SHOULD EXIST)

**Path:** `src/NetworkOptimizer.Installer/SpeedTest/nginx.conf`

nginx configuration for Windows:

```nginx
worker_processes  1;

events {
    worker_connections  1024;
}

http {
    include       mime.types;
    default_type  application/octet-stream;
    sendfile        on;
    keepalive_timeout  65;

    server {
        listen       3005;
        server_name  localhost;

        # OpenSpeedTest requires HTTP/1.1
        proxy_http_version 1.1;

        location / {
            root   html;
            index  index.html;
        }

        # Large file handling for speed tests
        client_max_body_size 35m;

        # Disable buffering for accurate speed measurement
        proxy_buffering off;
        proxy_request_buffering off;
    }
}
```

### 9. Start-SpeedTest.ps1 (SHOULD EXIST)

**Path:** `src/NetworkOptimizer.Installer/SpeedTest/Start-SpeedTest.ps1`

PowerShell startup script (legacy, not used when running as service):

```powershell
# Start-SpeedTest.ps1
# Starts nginx for OpenSpeedTest
# Note: When running as Windows Service, NginxHostedService manages this automatically

param(
    [string]$InstallDir = $PSScriptRoot
)

$ErrorActionPreference = "Stop"

$nginxPath = Join-Path $InstallDir "nginx.exe"
$confPath = Join-Path $InstallDir "conf\nginx.conf"

if (-not (Test-Path $nginxPath)) {
    Write-Error "nginx.exe not found at $nginxPath"
    exit 1
}

Write-Host "Starting nginx for OpenSpeedTest..."
Start-Process -FilePath $nginxPath -ArgumentList "-c", $confPath -WorkingDirectory $InstallDir -NoNewWindow
Write-Host "nginx started on port 3005"
```

## Build Steps

1. **Download nginx** (if not already present):
   ```powershell
   cd src/NetworkOptimizer.Installer
   powershell -ExecutionPolicy Bypass -File "SpeedTest\Download-Nginx.ps1"
   ```

2. **Build the installer**:
   ```powershell
   .\scripts\build-installer.ps1
   ```

3. **Output**: `publish/NetworkOptimizer-{version}-win-x64.msi` (~86 MB)

## Configuration Flow

1. **Installation**: User enters HOST_IP, HOST_NAME, etc. in installer dialog
2. **Registry**: Values stored at `HKLM\SOFTWARE\Ozark Connect\Network Optimizer`
3. **Service Start**: NginxHostedService reads registry + environment variables
4. **Config Generation**: Generates `config.js` with proper save URL
5. **nginx Start**: nginx started as child process serving OpenSpeedTest on port 3005

## Key Design Decisions

1. **nginx as child process** (not Windows service): nginx.exe doesn't support Windows service control messages natively. Managing it as a child process is cleaner.

2. **Windows Registry** (not config files): Standard Windows pattern for installer-set configuration. Cleaner than INI files for this use case.

3. **Environment variable override**: Allows overriding registry values via environment variables for flexibility.

4. **config.js generation at runtime**: Ensures save URL is always current based on configuration.
