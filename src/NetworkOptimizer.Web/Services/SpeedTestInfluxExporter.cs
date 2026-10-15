using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>Best-effort export after a speed test result has been saved to its site's SQLite database.</summary>
public static class SpeedTestInfluxExporter
{
    /// <summary>
    /// Best-effort removal of the pre-edit Influx identity after a SQLite mutation,
    /// followed by export of the current row if it still exists.
    /// </summary>
    public static Task SynchronizeMutationAsync(MonitoringInfluxRegistry? registry, string siteSlug,
        Iperf3Result original, ILogger logger) => ExportCoreAsync(registry, siteSlug, original, logger,
            static (client, saved) => client.SynchronizeSpeedTestMutationAsync(saved));

    public static Task ClearHistoryAsync(MonitoringInfluxRegistry? registry, string siteSlug,
        IReadOnlyList<Iperf3Result> cleared, ILogger logger) => cleared.Count == 0 ? Task.CompletedTask
        : ExportCoreAsync(registry, siteSlug, cleared[0], logger,
            (client, _) => client.ClearSpeedTestHistoryAsync(cleared));

    public static Task ExportAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger) => ExportCoreAsync(registry, siteSlug, result, logger,
            static (client, saved) => client.WriteSpeedTestResultAsync(saved));

    public static Task ExportMetadataAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger) => ExportCoreAsync(registry, siteSlug, result, logger,
            static (client, saved) => client.WriteSpeedTestMetadataAsync(saved));

    public static Task ExportBackgroundLoadAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger) => ExportCoreAsync(registry, siteSlug, result, logger,
            static (client, saved) => client.WriteSpeedTestBackgroundLoadAsync(saved));

    private static async Task ExportCoreAsync(
        MonitoringInfluxRegistry? registry,
        string siteSlug,
        Iperf3Result result,
        ILogger logger,
        Func<MonitoringInfluxClient, Iperf3Result, Task> write)
    {
        if (registry == null) return;

        try
        {
            var client = registry.GetFor(siteSlug);
            if (!client.IsConfigured && !await client.ReconfigureAsync())
                return;

            await write(client, result);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to export speed test result {ResultId} for site {Site} to InfluxDB; SQLite result is unchanged",
                result.Id, siteSlug);
        }
    }
}
