namespace NetworkOptimizer.Web.Services.Ssh;

/// <summary>
/// Installs and checks the generic udm-boot mechanism on a UniFi gateway: a systemd
/// oneshot unit that runs every executable script in <c>/data/on_boot.d</c> at boot and
/// persists across firmware updates. This is shared gateway infrastructure used by any
/// feature that deploys self-contained boot scripts (Adaptive SQM, Monitoring
/// Interfaces, etc.) - it is NOT specific to any one feature.
/// </summary>
public interface IUdmBootService
{
    /// <summary>Returns true when the udm-boot systemd unit is present on the gateway.</summary>
    Task<bool> IsInstalledAsync();

    /// <summary>
    /// Install the udm-boot systemd unit (idempotent), enabling scripts in
    /// /data/on_boot.d/ to run on boot.
    /// </summary>
    Task<(bool success, string message)> InstallAsync();
}

/// <inheritdoc />
public class UdmBootService : IUdmBootService
{
    private readonly ILogger<UdmBootService> _logger;
    private readonly IGatewaySshService _gatewaySsh;

    public UdmBootService(ILogger<UdmBootService> logger, GatewaySshRegistry gatewaySshRegistry)
    {
        _logger = logger;
        _gatewaySsh = gatewaySshRegistry.GetDefault();
    }

    public async Task<bool> IsInstalledAsync()
    {
        var result = await _gatewaySsh.RunCommandAsync(
            "test -f /etc/systemd/system/udm-boot.service && echo 'installed' || echo 'missing'");
        return result.success && result.output.Contains("installed");
    }

    public async Task<(bool success, string message)> InstallAsync()
    {
        var settings = await _gatewaySsh.GetSettingsAsync();

        try
        {
            _logger.LogInformation("Installing udm-boot on gateway {Host}", settings.Host);

            // Create the udm-boot service file directly (works on all UDM/UCG devices).
            // This matches the upstream unifios-utilities version exactly.
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

            // Write service file via base64 decode, enable and start.
            // Use --no-block so we don't wait for boot scripts to finish (they can take a while).
            var installCmd = $"echo {base64Content} | base64 -d > /etc/systemd/system/udm-boot.service && " +
                "mkdir -p /data/on_boot.d && " +
                "systemctl daemon-reload && " +
                "systemctl enable udm-boot && " +
                "systemctl start --no-block udm-boot && " +
                "echo udm-boot_installed_successfully";
            var result = await _gatewaySsh.RunCommandAsync(installCmd);

            if (result.success && result.output.Contains("udm-boot_installed_successfully"))
            {
                _logger.LogInformation("udm-boot installed successfully on {Host}", settings.Host);
                return (true, "udm-boot installed successfully. Scripts in /data/on_boot.d/ will now run on boot.");
            }

            _logger.LogError("udm-boot installation failed: {Output}", result.output);
            return (false, $"Installation failed: {result.output}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install udm-boot");
            return (false, $"Error: {ex.Message}");
        }
    }
}
