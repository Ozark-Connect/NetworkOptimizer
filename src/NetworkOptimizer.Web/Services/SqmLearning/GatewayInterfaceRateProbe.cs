using System.Globalization;
using NetworkOptimizer.Sqm;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// One SSH round trip that reads what a learning sample needs from the gateway itself: the WAN
/// data-path interface's traffic over a short window (the idle fallback when SNMP monitoring is
/// not delivering), the gateway's local day and hour (the slot the deployed schedule reads), and
/// whether the Adaptive SQM speed test script is mid-run.
/// </summary>
public static class GatewayInterfaceRateProbe
{
    /// <summary>Seconds the counters are sampled over.</summary>
    public const int WindowSeconds = 3;

    /// <summary>The reading the probe produced.</summary>
    /// <param name="DownloadMbps">Traffic received on the WAN interface over the window.</param>
    /// <param name="UploadMbps">Traffic sent on the WAN interface over the window.</param>
    /// <param name="DayOfWeek">Gateway-local day, 0 = Monday.</param>
    /// <param name="Hour">Gateway-local hour.</param>
    /// <param name="SqmSpeedtestRunning">True while a deployed *-speedtest.sh is executing.</param>
    /// <param name="InterfacePresent">False when the interface has no counters on the gateway.</param>
    public sealed record Reading(
        double DownloadMbps,
        double UploadMbps,
        int DayOfWeek,
        int Hour,
        bool SqmSpeedtestRunning,
        bool InterfacePresent);

    /// <summary>The shell command for one probe of the given interface.</summary>
    public static string BuildCommand(string interfaceName)
    {
        var validation = InputSanitizer.ValidateInterface(interfaceName);
        if (!validation.isValid)
            throw new ArgumentException(validation.error, nameof(interfaceName));

        var stats = $"/sys/class/net/{interfaceName}/statistics";
        // The pgrep pattern is bracketed so the probe's own command line never matches itself.
        return $"present=$(test -d /sys/class/net/{interfaceName} && echo 1 || echo 0); " +
               $"r1=$(cat {stats}/rx_bytes 2>/dev/null || echo 0); t1=$(cat {stats}/tx_bytes 2>/dev/null || echo 0); " +
               $"sleep {WindowSeconds}; " +
               $"r2=$(cat {stats}/rx_bytes 2>/dev/null || echo 0); t2=$(cat {stats}/tx_bytes 2>/dev/null || echo 0); " +
               "echo \"rx_bytes=$((r2 - r1))\"; echo \"tx_bytes=$((t2 - t1))\"; " +
               "echo \"day=$(( $(date +%u) - 1 ))\"; echo \"hour=$(date +%-H)\"; " +
               "echo \"present=$present\"; " +
               "echo \"sqm_speedtest=$(pgrep -f -- '[-]speedtest\\.sh' >/dev/null 2>&1 && echo 1 || echo 0)\"";
    }

    /// <summary>Parses the probe's key=value output; null when it is not the probe's output.</summary>
    public static Reading? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            values[line[..eq]] = line[(eq + 1)..].Trim();
        }

        if (!TryLong(values, "rx_bytes", out var rx) || !TryLong(values, "tx_bytes", out var tx))
            return null;
        if (!TryInt(values, "day", out var day) || !TryInt(values, "hour", out var hour))
            return null;
        if (day is < 0 or > 6 || hour is < 0 or > 23)
            return null;

        var present = !values.TryGetValue("present", out var p) || p != "0";
        var running = values.TryGetValue("sqm_speedtest", out var s) && s == "1";

        // A counter that wrapped or reset reads negative; treat that window as unknown-busy
        // rather than idle, since the honest answer is "could not tell".
        var downMbps = rx < 0 ? double.MaxValue : rx * 8.0 / WindowSeconds / 1_000_000.0;
        var upMbps = tx < 0 ? double.MaxValue : tx * 8.0 / WindowSeconds / 1_000_000.0;

        return new Reading(downMbps, upMbps, day, hour, running, present);
    }

    private static bool TryLong(Dictionary<string, string> values, string key, out long value)
    {
        value = 0;
        return values.TryGetValue(key, out var s) && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryInt(Dictionary<string, string> values, string key, out int value)
    {
        value = 0;
        return values.TryGetValue(key, out var s) && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
