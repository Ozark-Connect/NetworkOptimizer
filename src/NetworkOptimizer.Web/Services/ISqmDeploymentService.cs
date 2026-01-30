using SqmConfig = NetworkOptimizer.Sqm.Models.SqmConfiguration;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for deploying SQM scripts to UniFi gateways via SSH.
/// Follows the same SSH execution pattern as Iperf3SpeedTestService.
/// </summary>
public interface ISqmDeploymentService
{
    /// <summary>
    /// Test SSH connection to the gateway.
    /// </summary>
    /// <returns>A tuple indicating success and a descriptive message.</returns>
    Task<(bool success, string message)> TestConnectionAsync();

    /// <summary>
    /// Install udm-boot package on the gateway.
    /// This enables scripts in /data/on_boot.d/ to run automatically on boot
    /// and persist across firmware updates.
    /// </summary>
    /// <returns>A tuple indicating success and a descriptive message.</returns>
    Task<(bool success, string message)> InstallUdmBootAsync();

    /// <summary>
    /// Check if SQM scripts are already deployed on the gateway.
    /// </summary>
    /// <returns>A <see cref="SqmDeploymentStatus"/> object with detailed deployment status.</returns>
    Task<SqmDeploymentStatus> CheckDeploymentStatusAsync();

    /// <summary>
    /// Deploy SQM scripts to the gateway.
    /// </summary>
    /// <param name="config">SQM configuration for this WAN.</param>
    /// <param name="baseline">Optional hourly baseline data.</param>
    /// <param name="initialDelaySeconds">Delay before first speedtest (default 60s, use higher values for additional WANs to stagger).</param>
    /// <returns>A <see cref="SqmDeploymentResult"/> with deployment outcome and steps taken.</returns>
    Task<SqmDeploymentResult> DeployAsync(SqmConfig config, Dictionary<string, string>? baseline = null, int initialDelaySeconds = 60);

    /// <summary>
    /// Deploy SQM Monitor script. Uses TcMonitorPort from gateway settings.
    /// Exposes all SQM data (TC rates, speedtest results, ping data) via HTTP.
    /// </summary>
    /// <param name="wan1Interface">Physical interface name for WAN1 (e.g., "ifbeth4").</param>
    /// <param name="wan1Name">Friendly name for WAN1 (e.g., "Yelcot").</param>
    /// <param name="wan2Interface">Physical interface name for WAN2 (e.g., "ifbeth0").</param>
    /// <param name="wan2Name">Friendly name for WAN2 (e.g., "Starlink").</param>
    /// <returns>A tuple with success status and optional warning message if the service didn't start correctly.</returns>
    Task<(bool success, string? warning)> DeploySqmMonitorAsync(string wan1Interface, string wan1Name, string wan2Interface, string wan2Name);

    /// <summary>
    /// Remove SQM scripts from the gateway.
    /// </summary>
    /// <param name="includeTcMonitor">If true, also removes the TC Monitor service.</param>
    /// <returns>A tuple indicating success and a list of steps performed.</returns>
    Task<(bool success, List<string> steps)> RemoveAsync(bool includeTcMonitor = true);

    /// <summary>
    /// Trigger the SQM adjustment speedtest script on the gateway.
    /// This runs the deployed script which does baseline blending and TC adjustment.
    /// </summary>
    /// <param name="wanName">The name of the WAN to trigger adjustment for.</param>
    /// <returns>A tuple indicating success and a descriptive message.</returns>
    Task<(bool success, string message)> TriggerSqmAdjustmentAsync(string wanName);

    /// <summary>
    /// Get the last N lines of the SQM log for a specific WAN connection.
    /// Useful for debugging failed speedtests or checking adjustment history.
    /// </summary>
    /// <param name="wanName">The WAN connection name.</param>
    /// <param name="lines">Number of lines to retrieve (default 50).</param>
    /// <returns>Success status and log output or error message.</returns>
    Task<(bool success, string output)> GetWanLogsAsync(string wanName, int lines = 50);

    /// <summary>
    /// Get SQM status for all WANs by parsing gateway logs.
    /// </summary>
    /// <returns>A list of <see cref="SqmWanStatus"/> objects with per-WAN status information.</returns>
    Task<List<SqmWanStatus>> GetSqmWanStatusAsync();
}
