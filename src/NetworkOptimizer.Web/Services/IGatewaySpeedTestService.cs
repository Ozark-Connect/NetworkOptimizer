using NetworkOptimizer.Storage.Models;

using NetworkOptimizer.Storage.Models.Identity;
using NetworkOptimizer.Web.Services.Gates;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing gateway SSH settings and running iperf3 speed tests.
/// The gateway typically has different SSH credentials than other UniFi devices.
/// </summary>
[MutatingService(SiteScoped = true)]
public interface IGatewaySpeedTestService
{
    /// <summary>
    /// Gets whether a speed test is currently running.
    /// </summary>
    [RequireRole(Roles.Viewer)]
    bool IsTestRunning { get; }

    /// <summary>
    /// Get the gateway SSH settings (creates default if none exist).
    /// </summary>
    /// <param name="forceRefresh">If true, bypasses cache and loads fresh from database.</param>
    /// <returns>The gateway SSH settings.</returns>
    [RequireRole(Roles.Viewer)]
    Task<GatewaySshSettings> GetSettingsAsync(bool forceRefresh = false);

    /// <summary>
    /// Save gateway SSH settings.
    /// </summary>
    /// <param name="settings">The settings to save.</param>
    /// <returns>The saved settings.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "gateway_ssh")]
    Task<GatewaySshSettings> SaveSettingsAsync(GatewaySshSettings settings);

    /// <summary>
    /// Test SSH connection to the gateway.
    /// </summary>
    /// <returns>A tuple containing success status and message.</returns>
    [RequireRole(Roles.Viewer)]
    Task<(bool success, string message)> TestConnectionAsync();

    /// <summary>
    /// Run an SSH command on the gateway.
    /// </summary>
    /// <param name="command">The command to execute.</param>
    /// <returns>A tuple containing success status and output.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "gateway_ssh")]
    Task<(bool success, string output)> RunSshCommandAsync(string command);

    /// <summary>
    /// Check if iperf3 is running on the gateway and get its status.
    /// </summary>
    /// <returns>The iperf3 status information.</returns>
    [RequireRole(Roles.Viewer)]
    Task<Iperf3Status> CheckIperf3StatusAsync();

    /// <summary>
    /// Start iperf3 server on the gateway.
    /// </summary>
    /// <param name="port">Optional port to use (defaults to configured port).</param>
    /// <returns>A tuple containing success status and message.</returns>
    [RequireRole(Roles.Admin)]
    [AuditAction(AuditActions.SettingsChanged, Category = AuditCategories.Settings, TargetType = "iperf3_server")]
    Task<(bool success, string message)> StartIperf3ServerAsync(int? port = null);

    /// <summary>
    /// Run a speed test from the Docker container to the gateway using system settings.
    /// </summary>
    /// <returns>The speed test result.</returns>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SpeedTestRun, TargetType = "gateway_speedtest")]
    Task<GatewaySpeedTestResult> RunSpeedTestAsync();

    /// <summary>
    /// Run a speed test from the Docker container to the gateway with specific parameters.
    /// </summary>
    /// <param name="durationSeconds">Duration of the test in seconds.</param>
    /// <param name="parallelStreams">Number of parallel streams to use.</param>
    /// <returns>The speed test result.</returns>
    [RequireRole(Roles.Operator)]
    [AuditAction(AuditActions.SpeedTestRun, TargetType = "gateway_speedtest")]
    Task<GatewaySpeedTestResult> RunSpeedTestAsync(int durationSeconds, int parallelStreams);

    /// <summary>
    /// Get the last speed test result.
    /// </summary>
    /// <returns>The last result, or null if no test has been run.</returns>
    [RequireRole(Roles.Viewer)]
    GatewaySpeedTestResult? GetLastResult();
}
