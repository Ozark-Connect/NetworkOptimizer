using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for managing gateway SSH settings and running iperf3 speed tests.
/// The gateway typically has different SSH credentials than other UniFi devices.
/// </summary>
public interface IGatewaySpeedTestService
{
    /// <summary>
    /// Gets whether a speed test is currently running for a site.
    /// </summary>
    bool IsTestRunning(int siteId);

    /// <summary>
    /// Get the gateway SSH settings for a site (creates default if none exist).
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="forceRefresh">If true, bypasses cache and loads fresh from database.</param>
    /// <returns>The gateway SSH settings.</returns>
    Task<GatewaySshSettings> GetSettingsAsync(int siteId, bool forceRefresh = false);

    /// <summary>
    /// Save gateway SSH settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="settings">The settings to save.</param>
    /// <returns>The saved settings.</returns>
    Task<GatewaySshSettings> SaveSettingsAsync(int siteId, GatewaySshSettings settings);

    /// <summary>
    /// Test SSH connection to the gateway for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(int siteId);

    /// <summary>
    /// Run an SSH command on the gateway for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="command">The command to execute.</param>
    /// <returns>A tuple containing success status and output.</returns>
    Task<(bool success, string output)> RunSshCommandAsync(int siteId, string command);

    /// <summary>
    /// Check if iperf3 is running on the gateway for a site and get its status.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>The iperf3 status information.</returns>
    Task<Iperf3Status> CheckIperf3StatusAsync(int siteId);

    /// <summary>
    /// Start iperf3 server on the gateway for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="port">Optional port to use (defaults to configured port).</param>
    /// <returns>A tuple containing success status and message.</returns>
    Task<(bool success, string message)> StartIperf3ServerAsync(int siteId, int? port = null);

    /// <summary>
    /// Run a speed test from the Docker container to the gateway using system settings for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>The speed test result.</returns>
    Task<GatewaySpeedTestResult> RunSpeedTestAsync(int siteId);

    /// <summary>
    /// Run a speed test from the Docker container to the gateway with specific parameters for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <param name="durationSeconds">Duration of the test in seconds.</param>
    /// <param name="parallelStreams">Number of parallel streams to use.</param>
    /// <returns>The speed test result.</returns>
    Task<GatewaySpeedTestResult> RunSpeedTestAsync(int siteId, int durationSeconds, int parallelStreams);

    /// <summary>
    /// Get the last speed test result for a site.
    /// </summary>
    /// <param name="siteId">The site ID.</param>
    /// <returns>The last result, or null if no test has been run.</returns>
    GatewaySpeedTestResult? GetLastResult(int siteId);
}
