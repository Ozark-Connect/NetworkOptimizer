using NetworkOptimizer.Storage.Services;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Rolls each site's per-client byte counters up into one point per client per hour in the
/// longterm bucket, so usage over days is a few hundred rows to read rather than millions.
/// Runs a few minutes past every hour for the hour just finished, and on start fills whatever
/// the fast bucket still holds behind the newest rolled hour. Idempotent: an hour is written at
/// its own start, so a re-run overwrites it.
/// </summary>
public class ClientUsageRollupService : BackgroundService
{
    /// <summary>How far back a first run reaches; the fast bucket rarely keeps more.</summary>
    private static readonly TimeSpan BackfillHorizon = TimeSpan.FromDays(7);

    /// <summary>Past the hour, so the last write window's points have landed before it is read.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(3);

    private readonly MonitoringInfluxClient _influx;
    private readonly ILogger<ClientUsageRollupService> _logger;
    private readonly string _siteSlug;

    public ClientUsageRollupService(
        MonitoringInfluxRegistry influx,
        ILogger<ClientUsageRollupService> logger,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _influx = influx.GetFor(_siteSlug);
        _logger = logger;
    }

    /// <summary>No-op: the registry owns start and stop.</summary>
    public override void Dispose() { }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RollupPendingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Client usage rollup failed for site {Site}", _siteSlug);
            }

            var now = DateTime.UtcNow;
            var next = HourStart(now).AddHours(1) + SettleDelay;
            try { await Task.Delay(next - now, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RollupPendingAsync(CancellationToken ct)
    {
        if (!_influx.IsConfigured || string.IsNullOrEmpty(_influx.LongtermBucket)) return;

        var lastComplete = HourStart(DateTime.UtcNow).AddHours(-1);
        var last = await _influx.QueryLastUsageRollupHourAsync(ct);
        var start = last.HasValue ? last.Value.AddHours(1) : lastComplete - BackfillHorizon;
        if (start < lastComplete - BackfillHorizon) start = lastComplete - BackfillHorizon;

        var hours = 0;
        var wifi = 0;
        var ports = 0;
        for (var hour = start; hour <= lastComplete; hour = hour.AddHours(1))
        {
            ct.ThrowIfCancellationRequested();
            wifi += await _influx.RollupWifiClientUsageHourAsync(hour, ct);
            ports += await _influx.RollupPortUsageHourAsync(hour, ct);
            hours++;
        }
        if (hours > 0)
            _logger.LogInformation("Client usage rollup for site {Site}: {Hours} hour(s), {Wifi} wireless and {Ports} port points",
                _siteSlug, hours, wifi, ports);
    }

    private static DateTime HourStart(DateTime utc) =>
        new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
}
