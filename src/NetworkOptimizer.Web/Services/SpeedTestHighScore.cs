using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>One earlier result considered for the high score: who ran it and the client's download.</summary>
/// <param name="DeviceHost">Address the result was recorded from.</param>
/// <param name="ClientMac">MAC enrichment tied it to, or null when enrichment found none.</param>
/// <param name="ClientDownloadBps">The client's download, from <see cref="SpeedTestHighScore.ClientDownloadBps"/>.</param>
public sealed record HighScoreHistoryEntry(string DeviceHost, string? ClientMac, double ClientDownloadBps);

/// <summary>
/// Decides whether a browser speed test is a new personal best for its device. History is every
/// earlier result from the same address plus every earlier result from the same MAC, so the device
/// keeps its best across an IP change, and the result never beats less than the address-only rule saw.
/// </summary>
public static class SpeedTestHighScore
{
    /// <summary>Earlier results required before a banner can show, so a device's first tests never claim one.</summary>
    public const int MinimumPriorResults = 2;

    /// <summary>
    /// The client's download for a browser result. LAN results are stored from the server's side,
    /// so the client's download is the server's upload; WAN results are stored from the client's side.
    /// </summary>
    public static double ClientDownloadBps(SpeedTestDirection direction, double downloadBps, double uploadBps)
        => direction == SpeedTestDirection.BrowserToServer ? uploadBps : downloadBps;

    /// <summary>
    /// Whether <paramref name="currentClientDownloadBps"/> beats every earlier result for this device.
    /// Entries that match neither the address nor the MAC are ignored, so a caller may pass a superset.
    /// </summary>
    /// <param name="currentClientDownloadBps">The new result's client download.</param>
    /// <param name="deviceHost">Address the new result came from.</param>
    /// <param name="clientMac">MAC resolved for that address now, or null to match by address only.</param>
    /// <param name="history">Earlier successful results in the same direction, excluding the new one.</param>
    public static bool IsNewHighScore(
        double currentClientDownloadBps,
        string deviceHost,
        string? clientMac,
        IEnumerable<HighScoreHistoryEntry> history)
    {
        var count = 0;
        var previousMax = 0.0;
        foreach (var entry in history)
        {
            if (!IsSameDevice(entry, deviceHost, clientMac))
                continue;
            count++;
            if (entry.ClientDownloadBps > previousMax)
                previousMax = entry.ClientDownloadBps;
        }

        return count >= MinimumPriorResults
            && previousMax > 0
            && currentClientDownloadBps > previousMax;
    }

    private static bool IsSameDevice(HighScoreHistoryEntry entry, string deviceHost, string? clientMac)
        => string.Equals(entry.DeviceHost, deviceHost, StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrEmpty(clientMac)
               && string.Equals(entry.ClientMac, clientMac, StringComparison.OrdinalIgnoreCase));
}
