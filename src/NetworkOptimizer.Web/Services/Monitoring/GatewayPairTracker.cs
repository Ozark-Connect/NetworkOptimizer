using System.Collections.Concurrent;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Firmware;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Remembers which MACs have served a site as one HA gateway, so its WAN and health history reads
/// as one series across a failover (<see cref="NetworkOptimizer.Storage.Services.MonitoringInfluxClient.GatewayPairMacs"/>).
/// </summary>
/// <remarks>
/// A Managed Shadow Gateway pair lists both units, labelled. A Console Shadow Mode pair lists one
/// gateway row at a time, marked only by vrrp, and a takeover makes that row the other unit's with
/// its own MAC; so the set is built up from each MAC seen as the active gateway with a vrrp block,
/// and persisted, because the earlier MAC is gone from the device list once the takeover happens.
/// </remarks>
public sealed class GatewayPairTracker
{
    internal const string SettingKey = "gateway.ha_pair_macs";

    /// <summary>Most MACs kept: a pair, plus replacements of either unit over time.</summary>
    internal const int MaxMacs = 4;

    private readonly ISystemSettingsService _settings;
    private readonly MonitoringInfluxRegistry _influx;
    private readonly ILogger<GatewayPairTracker> _logger;
    private readonly ConcurrentDictionary<string, string[]> _known = new(StringComparer.OrdinalIgnoreCase);

    public GatewayPairTracker(ISystemSettingsService settings, MonitoringInfluxRegistry influx, ILogger<GatewayPairTracker> logger)
    {
        _settings = settings;
        _influx = influx;
        _logger = logger;
    }

    /// <summary>
    /// Folds a fresh device list into the site's pair MACs, stores a change, and hands the set to
    /// the site's InfluxDB client. Never throws: without it, history stays split, nothing else changes.
    /// </summary>
    public async Task ObserveAsync(string siteSlug, IReadOnlyList<DiscoveredDevice> devices)
    {
        try
        {
            if (!_known.TryGetValue(siteSlug, out var current))
                current = Parse(await _settings.GetAsync(siteSlug, SettingKey));
            var next = Next(current, devices);
            if (!next.SequenceEqual(current, StringComparer.OrdinalIgnoreCase))
            {
                await _settings.SetAsync(siteSlug, SettingKey, string.Join(",", next));
                _logger.LogInformation("HA gateway MACs for site {Site}: {Macs}", siteSlug, string.Join(", ", next));
            }
            _known[siteSlug] = next;
            _influx.GetFor(siteSlug).SetGatewayPairMacs(next);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "HA gateway MAC tracking failed for site {Site}", siteSlug);
        }
    }

    /// <summary>
    /// The pair MACs after one device list: every labelled pair member, and the active gateway when
    /// it carries a vrrp block, newest first, then the ones already known. A site with no HA
    /// evidence keeps what it had.
    /// </summary>
    internal static string[] Next(string[] current, IReadOnlyList<DiscoveredDevice> devices)
    {
        var seen = new List<string>();
        foreach (var d in devices)
            if (d.HaRole != GatewayHaRole.None && !string.IsNullOrEmpty(d.Mac)) seen.Add(MacNormalizer.Normalize(d.Mac));
        var active = UniFiDiscovery.SiteGateway(devices);
        if (active?.VrrpState != null && !string.IsNullOrEmpty(active.Mac)) seen.Add(MacNormalizer.Normalize(active.Mac));
        if (seen.Count == 0) return current;
        return seen.Concat(current).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxMacs).ToArray();
    }

    internal static string[] Parse(string? stored) =>
        string.IsNullOrWhiteSpace(stored)
            ? []
            : stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(MacNormalizer.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
