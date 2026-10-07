using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>Best-effort export after a speed test result has been saved to its site's SQLite database.</summary>
public static class SpeedTestInfluxExporter
{
    public static Task ExportAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger) => ExportCoreAsync(registry, siteSlug, result, logger, metadataOnly: false);

    public static Task ExportMetadataAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger) => ExportCoreAsync(registry, siteSlug, result, logger, metadataOnly: true);

    private static async Task ExportCoreAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger,
        bool metadataOnly)
    {
        if (registry == null) return;

        try
        {
            var client = registry.GetFor(siteSlug);
            if (!client.IsConfigured && !await client.ReconfigureAsync())
                return;

            if (metadataOnly)
                await client.WriteSpeedTestMetadataAsync(result);
            else
                await client.WriteSpeedTestResultAsync(result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to export speed test result {ResultId} for site {Site} to InfluxDB; SQLite result is unchanged",
                result.Id, siteSlug);
        }
    }
}
