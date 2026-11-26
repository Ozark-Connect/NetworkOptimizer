using System.Collections.Concurrent;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// <see cref="ILinkSpeedHistory"/> over the console's recorded link speeds. Each answer is held for an hour per
/// site, port and client: a path is redrawn every few seconds, and the evidence moves by the hour.
/// </summary>
public sealed class LinkSpeedHistory : ILinkSpeedHistory
{
    private static readonly TimeSpan Lookback = TimeSpan.FromDays(30);
    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, (DateTime At, bool Faster)> Cache = new(StringComparer.OrdinalIgnoreCase);

    private readonly NetworkOptimizer.Storage.Services.MonitoringInfluxClient _influx;
    private readonly SiteContextService _site;
    private readonly ILogger<LinkSpeedHistory> _logger;

    public LinkSpeedHistory(
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient influx,
        SiteContextService site,
        ILogger<LinkSpeedHistory> logger)
    {
        _influx = influx;
        _site = site;
        _logger = logger;
    }

    public async Task<bool> RanFasterWithClientAsync(string switchMac, int port, string clientMac, int mbps, CancellationToken ct = default)
    {
        var key = $"{_site.Slug}|{switchMac}|{port}|{clientMac}|{mbps}";
        if (Cache.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.Faster;
        try
        {
            var faster = await _influx.PortRanFasterWithClientAsync(switchMac, port, clientMac, mbps, Lookback, ct) ?? false;
            Cache[key] = (DateTime.UtcNow, faster);
            return faster;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not cached: a read that failed says nothing, and the next redraw asks again.
            _logger.LogDebug(ex, "Link speed history unavailable for {Switch} port {Port}", switchMac, port);
            return false;
        }
    }
}
