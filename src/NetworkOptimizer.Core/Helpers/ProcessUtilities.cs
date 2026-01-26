namespace NetworkOptimizer.Core.Helpers;

/// <summary>
/// Utilities for locating and running external processes.
/// </summary>
public static class ProcessUtilities
{
    /// <summary>
    /// Gets the path to the iperf3 executable.
    /// On Windows, looks for bundled iperf3 in the install directory first.
    /// On Linux/macOS, uses iperf3 from PATH.
    /// </summary>
    public static string GetIperf3Path()
    {
        if (OperatingSystem.IsWindows())
        {
            // Look for bundled iperf3 relative to the application directory
            var bundledPath = Path.Combine(AppContext.BaseDirectory, "iperf3", "iperf3.exe");
            if (File.Exists(bundledPath))
            {
                return bundledPath;
            }
        }

        // Fall back to iperf3 in PATH (Linux/macOS/Docker)
        return "iperf3";
    }
}
