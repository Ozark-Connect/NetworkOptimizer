using System.Text.Json;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Parsed result from iperf3 JSON output
/// </summary>
public record Iperf3ParsedResult(
    double BitsPerSecond,
    long Bytes,
    int Retransmits,
    string? LocalIp,
    string? ErrorMessage
);

/// <summary>
/// Shared utility for parsing iperf3 JSON output.
/// Used by both Iperf3SpeedTestService and GatewaySpeedTestService.
/// </summary>
public static class Iperf3JsonParser
{
    /// <summary>
    /// Parses iperf3 JSON output and extracts speed test metrics.
    /// </summary>
    /// <param name="json">Raw JSON output from iperf3</param>
    /// <param name="useSumReceived">If true, prefer sum_received (for download results). If false, use sum_sent.</param>
    /// <param name="logger">Optional logger for error reporting</param>
    /// <returns>Parsed result containing speed metrics, or error information</returns>
    public static Iperf3ParsedResult Parse(string json, bool useSumReceived = false, ILogger? logger = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Check for error in JSON
            if (root.TryGetProperty("error", out var errorProp))
            {
                var errorMsg = errorProp.GetString();
                if (!string.IsNullOrEmpty(errorMsg))
                {
                    return new Iperf3ParsedResult(0, 0, 0, null, errorMsg);
                }
            }

            // Extract local IP from connection info
            string? localIp = null;
            if (root.TryGetProperty("start", out var start) &&
                start.TryGetProperty("connected", out var connected) &&
                connected.GetArrayLength() > 0)
            {
                var firstConn = connected[0];
                if (firstConn.TryGetProperty("local_host", out var localHost))
                {
                    localIp = localHost.GetString();
                }
            }

            // Parse end results
            if (root.TryGetProperty("end", out var end))
            {
                // Try sum_received first if requested (for download direction)
                if (useSumReceived && end.TryGetProperty("sum_received", out var sumReceived))
                {
                    var bps = sumReceived.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumReceived.GetProperty("bytes").GetInt64();
                    // sum_received typically doesn't have retransmits
                    return new Iperf3ParsedResult(bps, bytes, 0, localIp, null);
                }

                // Use sum_sent
                if (end.TryGetProperty("sum_sent", out var sumSent))
                {
                    var bps = sumSent.GetProperty("bits_per_second").GetDouble();
                    var bytes = sumSent.GetProperty("bytes").GetInt64();
                    var retransmits = sumSent.TryGetProperty("retransmits", out var rt) ? rt.GetInt32() : 0;
                    return new Iperf3ParsedResult(bps, bytes, retransmits, localIp, null);
                }
            }

            return new Iperf3ParsedResult(0, 0, 0, localIp, "No end summary found in iperf3 output");
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex, "Failed to parse iperf3 JSON output");
            return new Iperf3ParsedResult(0, 0, 0, null, $"JSON parse error: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Unexpected error parsing iperf3 JSON");
            return new Iperf3ParsedResult(0, 0, 0, null, $"Parse error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extracts just the local IP from iperf3 JSON output.
    /// Useful when you only need the connection source IP.
    /// </summary>
    public static string? ExtractLocalIp(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("start", out var start) &&
                start.TryGetProperty("connected", out var connected) &&
                connected.GetArrayLength() > 0)
            {
                var firstConn = connected[0];
                if (firstConn.TryGetProperty("local_host", out var localHost))
                {
                    return localHost.GetString();
                }
            }
        }
        catch
        {
            // Ignore parse errors for this helper
        }

        return null;
    }

    /// <summary>
    /// Checks if iperf3 JSON output contains an error.
    /// </summary>
    public static string? ExtractError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                return errorProp.GetString();
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }
}
