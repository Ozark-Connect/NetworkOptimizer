using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>How far one direction of a speed test fell below its recent average.</summary>
public sealed record SpeedDrop(string Direction, double CurrentMbps, double AverageMbps, double DropPercent, int Samples);

/// <summary>
/// Compares a speed test with recent results of the same kind in each direction and picks the one
/// that fell furthest, so a regression in either direction raises the event. LAN results store From
/// Device as the download and To Device as the upload; WAN results store the internet download and upload.
/// </summary>
public static class SpeedTestRegression
{
    /// <summary>Recent results averaged per direction.</summary>
    public const int Window = 5;

    /// <summary>Fewest recent results in a direction that make an average to compare with.</summary>
    public const int MinSamples = 3;

    /// <summary>Recent results to read, enough to fill <see cref="Window"/> in each direction when some tests ran one way only.</summary>
    public const int RecentToRead = 20;

    public static bool IsWan(SpeedTestDirection direction) => direction is SpeedTestDirection.CloudflareWan
        or SpeedTestDirection.CloudflareWanGateway or SpeedTestDirection.UwnWan or SpeedTestDirection.UwnWanGateway
        or SpeedTestDirection.OpenSpeedTestWan;

    /// <summary>The directions as the speed test pages name them.</summary>
    public static (string Download, string Upload) Labels(SpeedTestDirection direction) =>
        IsWan(direction) ? ("Download", "Upload") : ("From Device", "To Device");

    /// <summary>The direction that fell furthest below its recent average, or null when neither fell.</summary>
    /// <param name="recent">Earlier successful results of the same kind, newest first.</param>
    public static SpeedDrop? Worst(Iperf3Result current, IReadOnlyList<Iperf3Result> recent)
    {
        var (download, upload) = Labels(current.Direction);
        return new[]
        {
            Drop(download, current.DownloadMbps, recent.Select(r => r.DownloadMbps)),
            Drop(upload, current.UploadMbps, recent.Select(r => r.UploadMbps)),
        }.OfType<SpeedDrop>().MaxBy(d => d.DropPercent);
    }

    public static AlertSeverity Severity(SpeedDrop drop) =>
        drop.DropPercent >= 50 ? AlertSeverity.Error : drop.DropPercent >= 25 ? AlertSeverity.Warning : AlertSeverity.Info;

    private static SpeedDrop? Drop(string direction, double currentMbps, IEnumerable<double> history)
    {
        // A direction this test did not measure is no drop, and earlier tests that skipped it are no baseline.
        if (currentMbps <= 0) return null;
        var samples = history.Where(v => v > 0).Take(Window).ToList();
        if (samples.Count < MinSamples) return null;
        var average = samples.Average();
        var drop = (average - currentMbps) / average * 100;
        return drop > 0 ? new SpeedDrop(direction, currentMbps, average, drop, samples.Count) : null;
    }
}
