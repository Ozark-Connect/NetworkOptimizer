using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Repositories;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.Web.Services.Firmware;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.DeviceRestart;

/// <summary>
/// The last PoE draw seen on the port that powers each device while that device was up. A hung
/// device's port can read 0 W, which is the case a power-cycle exists for, so the reading from
/// before it hung decides. In memory: after an app restart only live readings count.
/// </summary>
public sealed class PoeDrawMemory
{
    private readonly ConcurrentDictionary<string, double> _watts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The last draw recorded for the device on the site, or null.</summary>
    public double? Last(string siteSlug, string mac) =>
        _watts.TryGetValue(Key(siteSlug, mac), out var w) ? w : null;

    /// <summary>Records the draw on each online device's uplink port.</summary>
    public void Record(string siteSlug, RestartTopology topology)
    {
        foreach (var node in topology.All)
        {
            if (!node.IsOnline || node.UplinkPortIdx is not int port) continue;
            if (topology.Parent(node) is not { PoeWatts: { } watts } || !watts.TryGetValue(port, out var w)) continue;
            _watts[Key(siteSlug, node.Mac)] = w;
        }
    }

    /// <summary>Forgets a removed site.</summary>
    public void ClearSite(string siteSlug)
    {
        foreach (var key in _watts.Keys.Where(k => k.StartsWith(siteSlug + "|", StringComparison.OrdinalIgnoreCase)))
            _watts.TryRemove(key, out _);
    }

    private static string Key(string site, string mac) => $"{site}|{MacNormalizer.Normalize(mac)}";
}

/// <summary>
/// Reads what a restart decision needs for one site: the uplink tree with PoE draw, the lockout
/// inputs, whether SSH can reach a device, and the quiet hour. Shared by the sheet's service and
/// the executor so both decide from the same facts.
/// </summary>
public sealed class DeviceRestartSiteReader
{
    private readonly string _siteSlug;
    private readonly bool _isDefault;
    private readonly UniFiConnectionService _connection;
    private readonly DeviceSshRouter _ssh;
    private readonly SiteDbContextFactory _siteDbs;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _mainDb;
    private readonly MonitoringInfluxRegistry _influx;
    private readonly PoeDrawMemory _poe;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;

    public DeviceRestartSiteReader(
        SiteConnectionRegistry connections,
        DeviceSshRouterRegistry sshRouters,
        SiteDbContextFactory siteDbs,
        IDbContextFactory<NetworkOptimizerDbContext> mainDb,
        MonitoringInfluxRegistry influx,
        PoeDrawMemory poe,
        ILoggerFactory loggers,
        string siteSlug = SiteManagementService.DefaultSiteSlug)
    {
        _siteSlug = string.IsNullOrEmpty(siteSlug) ? SiteManagementService.DefaultSiteSlug : siteSlug;
        _isDefault = _siteSlug == SiteManagementService.DefaultSiteSlug;
        _connection = connections.GetFor(_siteSlug);
        _ssh = sshRouters.GetFor(_siteSlug);
        _siteDbs = siteDbs;
        _mainDb = mainDb;
        _influx = influx;
        _poe = poe;
        _loggers = loggers;
        _logger = loggers.CreateLogger<DeviceRestartSiteReader>();
    }

    /// <summary>The site this reader is for.</summary>
    public string SiteSlug => _siteSlug;

    /// <summary>The site's console connection.</summary>
    public UniFiConnectionService Connection => _connection;

    /// <summary>The site's SSH router.</summary>
    public DeviceSshRouter Ssh => _ssh;

    /// <summary>False for a site whose database has not been created yet.</summary>
    public bool DatabaseExists => _isDefault || _siteDbs.SiteDbExists(_siteSlug);

    /// <summary>Opens the site's own database.</summary>
    public async Task<NetworkOptimizerDbContext> OpenDbAsync(CancellationToken ct = default) =>
        _isDefault ? await _mainDb.CreateDbContextAsync(ct) : _siteDbs.CreateForSite(_siteSlug);

    /// <summary>
    /// The uplink tree with PoE draw, plus the discovered devices it came from. Null when the
    /// console does not answer: the caller waits rather than reading a dark console as every
    /// device offline.
    /// </summary>
    public async Task<(RestartTopology Topology, IReadOnlyList<DiscoveredDevice> Devices)?> ReadTopologyAsync(CancellationToken ct = default)
    {
        if (_connection.Client is not { } client) return null;

        List<DiscoveredDevice> devices;
        List<NetworkOptimizer.UniFi.Models.UniFiDeviceResponse> raw;
        try
        {
            devices = await _connection.GetDiscoveredDevicesAsync(ct);
            raw = await client.GetDevicesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart {Site}: the console did not answer the device list", _siteSlug);
            return null;
        }
        if (devices.Count == 0) return null;

        var rawByMac = raw
            .Where(d => !string.IsNullOrEmpty(d.Mac))
            .GroupBy(d => MacNormalizer.Normalize(d.Mac))
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var nodes = devices
            .Where(d => d.Adopted && !string.IsNullOrEmpty(d.Mac))
            .Select(d =>
            {
                rawByMac.TryGetValue(MacNormalizer.Normalize(d.Mac), out var r);
                var parent = !string.IsNullOrEmpty(d.UplinkMac) ? d.UplinkMac : d.LastUplinkMac;
                var wired = !string.Equals(d.UplinkType, "wireless", StringComparison.OrdinalIgnoreCase);
                int? port = wired
                    ? d.UplinkPort is > 0 ? d.UplinkPort : r?.LastUplink?.UplinkRemotePort is > 0 and var last ? last : null
                    : null;
                return new RestartNode(
                    MacNormalizer.Normalize(d.Mac), string.IsNullOrEmpty(d.Name) ? d.Mac : d.Name, d.Type,
                    string.IsNullOrEmpty(parent) ? null : MacNormalizer.Normalize(parent), port,
                    d.State, (long)d.Uptime.TotalSeconds, PoeDraw(r));
            })
            .ToList();

        var topology = new RestartTopology(nodes);
        _poe.Record(_siteSlug, topology);
        return (topology, devices);
    }

    /// <summary>Watts on each port that is delivering PoE, from the switch's port table.</summary>
    private static IReadOnlyDictionary<int, double>? PoeDraw(NetworkOptimizer.UniFi.Models.UniFiDeviceResponse? device)
    {
        if (device?.PortTable is not { Count: > 0 } ports) return null;
        var watts = new Dictionary<int, double>();
        foreach (var p in ports)
        {
            if (!p.PortPoe || !p.PoeEnable || p.PortIdx < 1) continue;
            if (double.TryParse(p.PoePower, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0)
                watts[p.PortIdx] = w;
        }
        return watts.Count == 0 ? null : watts;
    }

    /// <summary>The PoE port that can power-cycle the device, or null.</summary>
    public PoePort? PoweringPort(RestartTopology topology, string mac) =>
        RestartRules.PoweringPort(topology, mac, _poe.Last(_siteSlug, mac));

    /// <summary>The site-wide lockout inputs. A read that fails reads as no lockout from that source.</summary>
    public async Task<RestartSiteState> ReadSiteStateAsync(CancellationToken ct = default)
    {
        SiteRolloutStatus? rollout = null;
        var applying = false;
        try
        {
            await using var db = await OpenDbAsync(ct);
            var repository = new FirmwareRolloutRepository(db, _loggers.CreateLogger<FirmwareRolloutRepository>());
            rollout = await SiteRolloutStatus.ReadAsync(repository, DateTime.UtcNow, ct);
            applying = await db.ConfigPlans.AsNoTracking()
                .AnyAsync(p => p.Status == ConfigPlanStatus.Applying || p.Status == ConfigPlanStatus.Watching, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart {Site}: could not read the rollout or Change Control state", _siteSlug);
        }
        return new RestartSiteState(rollout, applying);
    }

    /// <summary>The SSH target for a discovered device.</summary>
    public static DeviceSshTarget Target(DiscoveredDevice device) => DeviceSshTarget.From(device);

    /// <summary>True when the site has SSH credentials on the route this device takes.</summary>
    public async Task<bool> HasSshAsync(DiscoveredDevice device, CancellationToken ct = default)
    {
        try
        {
            return await _ssh.GetConnectionAsync(Target(device), ct) != null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart {Site}: no SSH route for {Mac}", _siteSlug, device.Mac);
            return false;
        }
    }

    /// <summary>The console's timezone id, or null when it does not say; hours of the day are the site's, not this server's.</summary>
    public async Task<string?> ConsoleTimeZoneAsync(CancellationToken ct = default)
    {
        try
        {
            return _connection.Client is { } client ? (await client.GetConsoleSystemInfoAsync(ct))?.TimeZone : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Device restart {Site}: no console timezone, using this server's", _siteSlug);
            return null;
        }
    }

    /// <summary>
    /// The next quiet hour for the device and everything below it: a restart takes the whole
    /// subtree down, so the window has to be quiet for all of it.
    /// </summary>
    public async Task<QuietWindowProposal> ProposeQuietAsync(
        RestartTopology topology, IReadOnlyList<DiscoveredDevice> devices, string mac, CancellationToken ct = default)
    {
        var affected = topology.Subtree(mac).Select(n => n.Mac).Append(MacNormalizer.Normalize(mac))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planner = RolloutSnapshotBuilder.FromDevices(devices).Where(d => affected.Contains(d.Mac)).ToList();
        var type = topology.Find(mac)?.Type ?? DeviceType.Unknown;

        var zone = await ConsoleTimeZoneAsync(ct);
        var quiet = new QuietWindowService(_influx, _loggers.CreateLogger<QuietWindowService>(), _siteSlug, consoleTimeZoneId: zone);
        return await quiet.ProposeAsync(
            planner, (int)RestartRules.OfflineBudget(type).TotalSeconds, new FirmwareRolloutSettings(),
            clientCount: 0, minLead: TimeSpan.FromMinutes(30), ct, withinDay: true);
    }
}
