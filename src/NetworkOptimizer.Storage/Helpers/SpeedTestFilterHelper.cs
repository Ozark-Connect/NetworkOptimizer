using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Storage.Helpers;

/// <summary>
/// Shared filter logic for speed test results.
/// Used by both repository (future SQL migration) and UI components (in-memory filtering).
/// </summary>
public static class SpeedTestFilterHelper
{
    /// <summary>
    /// Checks if a result matches the search filter (case-insensitive partial match).
    /// Searches device host, name, client MAC, and hops in the network path.
    /// </summary>
    /// <remarks>
    /// FUTURE: This logic can be expressed as SQL for server-side filtering:
    /// - DeviceHost, DeviceName, ClientMac: Simple LIKE queries
    /// - PathAnalysis hops: SQLite json_each() + json_extract()
    /// Keep this method in sync with any future SQL implementation.
    /// </remarks>
    /// <param name="result">The speed test result to check</param>
    /// <param name="normalizedFilter">The filter string, already lowercased and trimmed</param>
    /// <param name="clientAndApOnly">If true, only search client device and connected AP; if false, search all hops</param>
    /// <returns>True if the result matches the filter</returns>
    public static bool MatchesFilter(Iperf3Result result, string normalizedFilter, bool clientAndApOnly = false)
    {
        // Check device host/IP (top-level column - easy to move to SQL)
        if (result.DeviceHost?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;

        // Check device name (top-level column - easy to move to SQL)
        if (result.DeviceName?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;

        // Check client MAC (top-level column - easy to move to SQL)
        if (result.ClientMac?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;

        // Check path analysis hops (JSON column - requires json_each() in SQL)
        var pathAnalysis = result.PathAnalysis;
        if (pathAnalysis?.Path?.Hops != null)
        {
            if (clientAndApOnly)
            {
                // Map filter: only check the connected AP (first AccessPoint hop)
                var apHop = pathAnalysis.Path.Hops.FirstOrDefault(h => h.Type == HopType.AccessPoint);
                if (apHop != null && MatchesHop(apHop, normalizedFilter))
                    return true;
            }
            else
            {
                // Full filter: check all hops in the path
                foreach (var hop in pathAnalysis.Path.Hops)
                {
                    if (MatchesHop(hop, normalizedFilter))
                        return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a hop matches the filter by name, MAC, or IP.
    /// </summary>
    private static bool MatchesHop(NetworkHop hop, string normalizedFilter)
    {
        if (hop.DeviceName?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;
        if (hop.DeviceMac?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;
        if (hop.DeviceIp?.ToLowerInvariant().Contains(normalizedFilter) == true)
            return true;
        return false;
    }
}
