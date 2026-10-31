using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Alerts;
using NetworkOptimizer.Alerts.Events;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Publishes the WAN speed test alert events: <c>wan.speed_completed</c> for every
/// successful result and <c>wan.speed_degradation</c> when the download or upload falls
/// below the recent average for the same WAN and test kind. Shared by the server-side WAN
/// tests (<see cref="WanSpeedTestServiceBase"/>) and the gateway WAN test so both
/// emit the same event shapes. Callers pass their per-site alert bus, so events
/// carry the originating site. The site's first successful test also turns on the
/// degradation rule, which ships disabled, unless a user has edited it.
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
            // The page filters on the WAN key, which is what WanNetworkGroup already holds ("WAN",
            // "WAN2"). Without it a multi-WAN site opens on whichever WAN it was left on, which is
            // not the one the alert is about. Same filter the ISP Health hero link passes.
            //
            // A bonded test records "WAN+WAN2", which becomes the comma list the analysis charts
            // already read. WAN Speed Test understands a single key today and ignores a list, but
            // these URLs are frozen into history when the alert is raised: a row written without
            // the filter never gains one, while a row carrying it starts working the day that page
            // learns to read it.
            var wanFilter = string.IsNullOrWhiteSpace(result.WanNetworkGroup)
                ? "?"
                : $"?wan={Uri.EscapeDataString(string.Join(",", result.WanNetworkGroup.ToLowerInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries)))}&";

            await alertEventBus.PublishAsync(new AlertEvent
            {
                EventType = "wan.speed_completed",
                Severity = AlertSeverity.Info,
                Source = "wan",
                Title = $"WAN Speed Test: {downloadMbps:F1} / {uploadMbps:F1} Mbps",
                Message = $"Download: {downloadMbps:F1} Mbps, Upload: {uploadMbps:F1} Mbps ({result.Direction})",
                SourceUrl = $"/wan-speedtest{wanFilter}result={result.Id}",
                Context = new Dictionary<string, string>
                {
                    ["download_mbps"] = downloadMbps.ToString("F1"),
                    ["upload_mbps"] = uploadMbps.ToString("F1"),
                    ["direction"] = result.Direction.ToString(),
                    ["wan_name"] = wanName
                }
            });

            // The degradation rule ships disabled; a WAN test that works is the sign it is wanted.
            if (result.Success)
            {
                try
                {
                    await using var rulesDb = await openSiteDb();
                    await AlertRuleAutoEnable.EnableUntouchedDefaultAsync(rulesDb, "wan.speed_degradation",
                        DefaultAlertRules.WanSpeedDegradationRuleName, logger);
                }
                catch (Exception enableEx)
                {
                    logger.LogDebug(enableEx, "Failed to enable the WAN speed degradation rule");
                }
            }

            // Check for degradation vs recent results (same WAN, same test kind), in both directions
            try
            {
                await using var db = await openSiteDb();
                var recent = await db.Iperf3Results
                    .AsNoTracking()
                    .Where(r => r.Direction == result.Direction && r.WanName == result.WanName && r.Id != result.Id && r.Success)
                    .OrderByDescending(r => r.TestTime)
                    .Take(SpeedTestRegression.RecentToRead)
                    .ToListAsync();

                if (SpeedTestRegression.Worst(result, recent) is { } drop)
                {
                    await alertEventBus.PublishAsync(new AlertEvent
                    {
                        EventType = "wan.speed_degradation",
                        Severity = SpeedTestRegression.Severity(drop),
                        Source = "wan",
                        Title = $"WAN degradation: {drop.Direction} at {drop.CurrentMbps:F0} Mbps ({drop.DropPercent:F0}% below average)",
                        Message = $"{wanName}: {drop.Direction} is {drop.DropPercent:F0}% below the recent average of {drop.AverageMbps:F0} Mbps",
                        MetricValue = drop.CurrentMbps,
                        ThresholdValue = drop.AverageMbps,
                        SourceUrl = $"/wan-speedtest{wanFilter}result={result.Id}",
                        Context = new Dictionary<string, string>
                        {
                            ["wan_name"] = wanName,
                            ["drop_direction"] = drop.Direction,
                            ["current_mbps"] = drop.CurrentMbps.ToString("F1"),
                            ["average_mbps"] = drop.AverageMbps.ToString("F1"),
                            ["drop_percent"] = drop.DropPercent.ToString("F0"),
                            ["sample_count"] = drop.Samples.ToString()
                        }
                    });
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
