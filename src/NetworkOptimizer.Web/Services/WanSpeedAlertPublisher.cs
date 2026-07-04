using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Publishes the WAN speed test alert events: <c>wan.speed_completed</c> for every
/// successful result and <c>wan.speed_degradation</c> when the download falls below
/// the recent average for the same WAN and direction. Shared by the server-side WAN
/// tests (<see cref="WanSpeedTestServiceBase"/>) and the gateway WAN test so both
/// emit the same event shapes. Callers pass their per-site alert bus, so events
/// carry the originating site.
/// </summary>
public static class WanSpeedAlertPublisher
{
    public static async Task PublishAsync(
        IAlertEventBus? alertEventBus,
        Iperf3Result result,
        Func<Task<NetworkOptimizerDbContext>> openSiteDb,
        ILogger logger)
    {
        if (alertEventBus == null) return;

        try
        {
            var downloadMbps = result.DownloadMbps;
            var uploadMbps = result.UploadMbps;
            var wanName = result.WanName ?? "Unknown";

            await alertEventBus.PublishAsync(new AlertEvent
            {
                EventType = "wan.speed_completed",
                Severity = AlertSeverity.Info,
                Source = "wan",
                Title = $"WAN Speed Test: {downloadMbps:F1} / {uploadMbps:F1} Mbps",
                Message = $"Download: {downloadMbps:F1} Mbps, Upload: {uploadMbps:F1} Mbps ({result.Direction})",
                SourceUrl = $"/wan-speedtest#result-{result.Id}",
                Context = new Dictionary<string, string>
                {
                    ["download_mbps"] = downloadMbps.ToString("F1"),
                    ["upload_mbps"] = uploadMbps.ToString("F1"),
                    ["direction"] = result.Direction.ToString(),
                    ["wan_name"] = wanName
                }
            });

            // Check for degradation vs recent average (same WAN, same direction)
            try
            {
                await using var db = await openSiteDb();
                var recent = await db.Iperf3Results
                    .AsNoTracking()
                    .Where(r => r.Direction == result.Direction && r.WanName == result.WanName && r.Id != result.Id && r.Success)
                    .OrderByDescending(r => r.TestTime)
                    .Take(5)
                    .ToListAsync();

                if (recent.Count >= 3)
                {
                    var avgDownload = recent.Average(r => r.DownloadMbps);
                    var dropPercent = avgDownload > 0 ? (avgDownload - downloadMbps) / avgDownload * 100 : 0;

                    if (dropPercent > 0)
                    {
                        await alertEventBus.PublishAsync(new AlertEvent
                        {
                            EventType = "wan.speed_degradation",
                            Severity = dropPercent >= 50 ? AlertSeverity.Error
                                : dropPercent >= 25 ? AlertSeverity.Warning : AlertSeverity.Info,
                            Source = "wan",
                            Title = $"WAN degradation: {downloadMbps:F0} Mbps ({dropPercent:F0}% below average)",
                            Message = $"{wanName} download is {dropPercent:F0}% below the recent average of {avgDownload:F0} Mbps",
                            MetricValue = downloadMbps,
                            ThresholdValue = avgDownload,
                            SourceUrl = $"/wan-speedtest#result-{result.Id}",
                            Context = new Dictionary<string, string>
                            {
                                ["wan_name"] = wanName,
                                ["current_mbps"] = downloadMbps.ToString("F1"),
                                ["average_mbps"] = avgDownload.ToString("F1"),
                                ["drop_percent"] = dropPercent.ToString("F0"),
                                ["sample_count"] = recent.Count.ToString()
                            }
                        });
                    }
                }
            }
            catch (Exception degradeEx)
            {
                logger.LogDebug(degradeEx, "Failed to check WAN speed degradation");
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to publish WAN speed test alert event");
        }
    }
}
