using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Interface for running iperf3 speed tests to UniFi and other network devices.
/// </summary>
public interface IIperf3SpeedTestService
{
    /// <summary>
    /// Gets the current iperf3 test settings.
    /// </summary>
    /// <returns>The current Iperf3Settings configuration.</returns>
    Task<Iperf3Settings> GetSettingsAsync();

    /// <summary>
    /// Gets all configured devices for speed testing.
    /// </summary>
    /// <returns>A list of all configured DeviceSshConfiguration entries.</returns>
    Task<List<DeviceSshConfiguration>> GetDevicesAsync();

    /// <summary>
    /// Saves a device configuration for speed testing.
    /// </summary>
    /// <param name="device">The device configuration to save.</param>
    /// <returns>The saved device configuration with updated ID if new.</returns>
    Task<DeviceSshConfiguration> SaveDeviceAsync(DeviceSshConfiguration device);

    /// <summary>
    /// Deletes a device configuration.
    /// </summary>
    /// <param name="id">The ID of the device to delete.</param>
    Task DeleteDeviceAsync(int id);

    /// <summary>
    /// Tests SSH connection to a device using global credentials.
    /// </summary>
    /// <param name="host">The hostname or IP address to test.</param>
    /// <returns>A tuple containing success status and a message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(string host);

    /// <summary>
    /// Tests SSH connection to a device using device-specific credentials if configured.
    /// </summary>
    /// <param name="device">The device configuration to test.</param>
    /// <returns>A tuple containing success status and a message.</returns>
    Task<(bool success, string message)> TestConnectionAsync(DeviceSshConfiguration device);

    /// <summary>
    /// Checks if iperf3 is available on a device using global credentials.
    /// </summary>
    /// <param name="host">The hostname or IP address to check.</param>
    /// <returns>A tuple containing availability status and version string.</returns>
    Task<(bool available, string version)> CheckIperf3AvailableAsync(string host);

    /// <summary>
    /// Checks if iperf3 is available on a device using device-specific credentials if configured.
    /// </summary>
    /// <param name="device">The device configuration to check.</param>
    /// <returns>A tuple containing availability status and version string.</returns>
    Task<(bool available, string version)> CheckIperf3AvailableAsync(DeviceSshConfiguration device);

    /// <summary>
    /// Runs a full speed test to a device using system settings for duration and parallel streams.
    /// </summary>
    /// <param name="device">The device configuration to test.</param>
    /// <returns>The test result containing throughput measurements and analysis.</returns>
    Task<Iperf3Result> RunSpeedTestAsync(DeviceSshConfiguration device);

    /// <summary>
    /// Runs a full speed test to a device with specific parameters.
    /// </summary>
    /// <param name="device">The device configuration to test.</param>
    /// <param name="durationSeconds">The test duration in seconds.</param>
    /// <param name="parallelStreams">The number of parallel TCP streams to use.</param>
    /// <returns>The test result containing throughput measurements and analysis.</returns>
    Task<Iperf3Result> RunSpeedTestAsync(DeviceSshConfiguration device, int durationSeconds, int parallelStreams);

    /// <summary>
    /// Gets recent speed test results across all devices.
    /// </summary>
    /// <param name="count">The maximum number of results to return (0 = no limit, default 50).</param>
    /// <param name="days">Filter to results within the last N days (0 = all time).</param>
    /// <returns>A list of recent Iperf3Result entries.</returns>
    Task<List<Iperf3Result>> GetRecentResultsAsync(int count = 50, int days = 0);

    /// <summary>
    /// Gets speed test results for a specific device.
    /// </summary>
    /// <param name="deviceHost">The hostname or IP of the device.</param>
    /// <param name="count">The maximum number of results to return (default 20).</param>
    /// <returns>A list of Iperf3Result entries for the specified device.</returns>
    Task<List<Iperf3Result>> GetResultsForDeviceAsync(string deviceHost, int count = 20);

    /// <summary>
    /// Clears all speed test history from the database.
    /// </summary>
    /// <returns>The number of records deleted.</returns>
    Task<int> ClearHistoryAsync();
}
