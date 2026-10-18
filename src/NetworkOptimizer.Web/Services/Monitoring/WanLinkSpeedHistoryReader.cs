using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Diagnostics.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Reads each gateway WAN port's recorded link speed history for the Config Optimizer, and
/// condenses it to how often the port dropped below its usual speed.
/// </summary>
public static class WanLinkSpeedHistoryReader
{
    /// <summary>How far back the Config Optimizer looks.</summary>
    public static readonly TimeSpan Lookback = TimeSpan.FromDays(30);

    /// <summary>Resolution the history is read at: the slowest speed in each window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Consecutive degraded windows a drop must span to count. Drops of a minute or two (a
    /// renegotiation caught mid-way by one snapshot) are left out; the pattern is drops that hold.
    /// </summary>
    internal const int MinWindowsPerEpisode = 2;

    /// <summary>The history of every gateway WAN port in <paramref name="devices"/>.</summary>
    public static async Task<List<WanLinkSpeedHistory>> ReadAsync(
        MonitoringInfluxClient influx, IEnumerable<UniFiDeviceResponse> devices, DateTime now, CancellationToken ct = default)
    {
        var results = new List<WanLinkSpeedHistory>();
        if (!influx.IsConfigured) return results;

        foreach (var gateway in devices.Where(d => d.DeviceType == DeviceType.Gateway && !string.IsNullOrEmpty(d.Mac)))
        {
            foreach (var port in gateway.PortTable ?? new List<SwitchPort>())
            {
                if (string.IsNullOrWhiteSpace(port.IfName) || !WanLinkSpeedExpectations.IsWanPort(port.NetworkName, null))
                    continue;
                var windows = await influx.QueryLinkSpeedWindowsAsync(gateway.Mac, port.IfName, now - Lookback, now, Window, ct);
                if (Summarize(gateway.Mac.ToLowerInvariant(), port.IfName, windows, Window, Lookback) is { } history)
                    results.Add(history);
            }
        }
        return results;
    }

    /// <summary>
    /// Condenses windowed minimum speeds into drops below the port's usual speed (the fastest
    /// window minimum). Null when there is no history.
    /// </summary>
    internal static WanLinkSpeedHistory? Summarize(
        string deviceMac, string ifName, IReadOnlyList<(DateTime Time, int MinMbps)> windows, TimeSpan window, TimeSpan lookback)
    {
        if (windows.Count == 0) return null;
        var usual = windows.Max(w => w.MinMbps);

        int episodes = 0, degradedWindows = 0, lowest = usual;
        int runLength = 0, runLowest = usual;

        // Missing windows (no snapshot: console unreachable, server restarting) are skipped, not
        // treated as recoveries. Splitting a run at every gap would count one long drop as many.
        void CloseRun()
        {
            if (runLength >= MinWindowsPerEpisode)
            {
                episodes++;
                degradedWindows += runLength;
                lowest = Math.Min(lowest, runLowest);
            }
            runLength = 0;
            runLowest = usual;
        }

        foreach (var (_, minMbps) in windows)
        {
            if (minMbps < usual)
            {
                runLength++;
                runLowest = Math.Min(runLowest, minMbps);
            }
            else
            {
                CloseRun();
            }
        }
        CloseRun();

        return new WanLinkSpeedHistory
        {
            DeviceMac = deviceMac,
            IfName = ifName,
            UsualMbps = usual,
            LowestMbps = lowest,
            DegradedEpisodes = episodes,
            DegradedTime = window * degradedWindows,
            // What the history actually covers, which is less than the lookback while it fills.
            Lookback = TimeSpan.FromTicks(Math.Min(lookback.Ticks, (windows[^1].Time - windows[0].Time + window).Ticks)),
        };
    }
}
