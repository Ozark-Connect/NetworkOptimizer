using System.Globalization;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.SqmLearning;

/// <summary>
/// Keeps a scheduled WAN speed test clear of the gateway's own Adaptive SQM speed tests, which
/// cron starts at each SQM WAN's morning and evening times (gateway-local, second 0). Two tests
/// at once each measure the other's load, and Adaptive SQM would shape to the wrong rate.
/// </summary>
public static class SqmSpeedTestClearance
{
    /// <summary>A WAN test does not start this close before an Adaptive SQM speed test: a 45 s test plus margin.</summary>
    public static readonly TimeSpan AheadWindow = TimeSpan.FromSeconds(60);

    /// <summary>Just after the cron minute starts, the script may not be visible to pgrep yet.</summary>
    public static readonly TimeSpan SpawnGrace = TimeSpan.FromSeconds(10);

    /// <summary>How often a held test checks the gateway again.</summary>
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The longest a test is held. Covers the ahead window plus one Adaptive SQM speed test; past
    /// it the test runs anyway, since a stuck script must not stop scheduled tests for good.
    /// </summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(3);

    /// <summary>The gateway's local time of day and whether an Adaptive SQM speed test is running.</summary>
    public sealed record Reading(TimeSpan TimeOfDay, bool SqmSpeedtestRunning);

    /// <summary>One SSH round trip: gateway-local time to the second, and the running check.</summary>
    public const string Command =
        "echo \"time=$(date +%H:%M:%S)\"; " +
        "echo \"sqm_speedtest=" + GatewayInterfaceRateProbe.SqmSpeedtestRunningShell + "\"";

    /// <summary>Parses <see cref="Command"/>'s output; null when it is not that output.</summary>
    public static Reading? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        string? time = null;
        string? running = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("time=", StringComparison.Ordinal)) time = line[5..];
            else if (line.StartsWith("sqm_speedtest=", StringComparison.Ordinal)) running = line[14..];
        }

        if (time == null || running == null
            || !TimeSpan.TryParseExact(time, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var timeOfDay))
            return null;

        return new Reading(timeOfDay, running == "1");
    }

    /// <summary>
    /// Why the test must wait right now, or null when it is clear to start. Only Adaptive SQM WANs
    /// that are enabled have a deployed schedule to avoid.
    /// </summary>
    public static string? HoldReason(Reading reading, IEnumerable<SqmWanConfiguration> sqmWans)
    {
        if (reading.SqmSpeedtestRunning)
            return "an Adaptive SQM speed test is running";

        var now = (int)reading.TimeOfDay.TotalSeconds;
        foreach (var wan in sqmWans.Where(w => w.Enabled))
        {
            foreach (var due in new[]
                     {
                         wan.SpeedtestMorningHour * 3600 + wan.SpeedtestMorningMinute * 60,
                         wan.SpeedtestEveningHour * 3600 + wan.SpeedtestEveningMinute * 60,
                     })
            {
                var ahead = ((due - now) % 86400 + 86400) % 86400;
                var behind = (86400 - ahead) % 86400;
                if (ahead < AheadWindow.TotalSeconds || behind < SpawnGrace.TotalSeconds)
                    return $"the Adaptive SQM speed test for {(string.IsNullOrEmpty(wan.Name) ? $"WAN{wan.WanNumber}" : wan.Name)} is due";
            }
        }
        return null;
    }
}
