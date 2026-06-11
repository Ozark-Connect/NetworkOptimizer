using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Read interface for the 3D LAN flow map's "outside the WAN" cloud rendering and the
/// per-WAN summaries. Translates the relational MonitoringTargets / UpstreamDiscoveries
/// rows into the cloud-grammar shapes the map renders (spec 5.7), so no rendering code
/// needs to know about row shapes.
///
/// The upstream-path portion returns minimal data until the tracer (Build #10) is
/// wired up. The WAN-summary portion is fully functional from day one since the
/// topology data is already available via UniFiConnectionService.
///
/// Multi-WAN aware from day one even though MVP only traces the primary; secondary WANs
/// get a populated WanSummary with their access-cloud handle but no transit chain
/// (matches spec 5.7's "every WAN gets its access ISP cloud; only the primary shows
/// transit clouds beyond it").
/// </summary>
public class MonitoringPathView
{
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly MonitoringLiveStats _liveStats;
    private readonly ILogger<MonitoringPathView> _logger;
    private IReadOnlyList<WanSummary>? _wanCache;
    private DateTime _wanCacheExpiry;

    public MonitoringPathView(
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        MonitoringLiveStats liveStats,
        ILogger<MonitoringPathView> logger)
    {
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _liveStats = liveStats;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cloud graph for one WAN's upstream rendering. Pass null for the
    /// system-default WAN (gateway's primary uplink). For secondary WANs only the
    /// access cloud is populated; transit clouds are MVP-primary-only per spec 5.7.
    /// </summary>
    public async Task<UpstreamPathSnapshot> GetUpstreamPathAsync(string? wanInterface = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        // Resolve the WAN to inspect; default to the first uplink port on the gateway.
        var wans = await GetWansAsync(ct);
        var wan = wanInterface == null
            ? wans.FirstOrDefault(w => w.IsPrimary) ?? wans.FirstOrDefault()
            : wans.FirstOrDefault(w => w.WanInterface == wanInterface);

        var resolvedWanInterface = wan?.WanInterface ?? wanInterface ?? "wan";
        var isPrimary = wan?.IsPrimary ?? true;
        _logger.LogDebug("GetUpstreamPathAsync: wans={WanCount}, wan={WanIf}, friendly={Friendly}, resolved={Resolved}, isPrimary={Primary}",
            wans.Count, wan?.WanInterface, wan?.FriendlyName, resolvedWanInterface, isPrimary);

        // Per-WAN context, with fallback to legacy MonitoringSettings for installs that
        // pre-date the WanDiscoveryContexts table. New installs and any post-migration
        // commit will populate the context row.
        var wanCtx = await db.WanDiscoveryContexts.AsNoTracking()
            .FirstOrDefaultAsync(c => c.WanInterface == resolvedWanInterface, ct);

        // Access cloud: WAN-scoped MonitoringTargets where TargetType = AccessIsp.
        // Fallback for legacy rows that pre-date WanInterface stamping: include null
        // WanInterface only when the requested WAN is the primary (single-WAN heritage).
        var accessHops = await db.MonitoringTargets.AsNoTracking()
            .Where(t => t.TargetType == MonitoringTargetType.AccessIsp
                        && (t.WanInterface == resolvedWanInterface
                            || (isPrimary && t.WanInterface == null)))
            .OrderBy(t => t.Id)
            .ToListAsync(ct);

        // TODO: once multi-WAN upstream tracing is implemented, each WAN will
        // have its own WanDiscoveryContext with L2 neighbor and access tech.
        // Until then, only fall back to global MonitoringSettings for the
        // primary WAN so secondaries don't inherit the primary's values.
        var l2NeighborMac = wanCtx?.L2NeighborMac ?? (isPrimary ? settings?.WanNeighborMac : null);
        var l2NeighborOui = wanCtx?.L2NeighborOui ?? (isPrimary ? settings?.WanNeighborOui : null);
        var accessTech = wanCtx?.AccessTechnology ?? (isPrimary ? settings?.AccessTechnology : null) ?? AccessTechnology.Unknown;

        var isCgnat = l2NeighborMac != null
            && !string.IsNullOrEmpty(wan?.IpAddress)
            && NetworkUtilities.ClassifyPublicAddress(wan.IpAddress) == PublicAddressClass.Cgnat;

        var access = new AccessIspCloud
        {
            AccessTechnology = FormatAccessTechnology(accessTech),
            L2NeighborOui = l2NeighborOui,
            AsnNumber = accessHops.FirstOrDefault()?.AsnNumber,
            AsnName = accessHops.FirstOrDefault()?.AsnName,
            IsCgnat = isCgnat || (wan?.IpClass == PublicAddressClass.Cgnat),
            Hops = accessHops.Select(t => new AccessHop
            {
                TargetId = t.TargetId,
                Label = t.Name,
                PtrHostname = t.PtrHostname,
                Address = t.Address,
                Role = MapRoleFromAutoLabel(t.AutoLabel),
                Live = _liveStats.GetTargetStats(t.TargetId)
            }).ToList()
        };

        // Transit clouds: only populate for the primary WAN per spec 5.7. WAN-scoped
        // same as access hops, with the same legacy-null fallback.
        IReadOnlyList<TransitCloud> transits = Array.Empty<TransitCloud>();
        if (isPrimary)
        {
            var transitRows = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Transit
                            && (t.WanInterface == resolvedWanInterface || t.WanInterface == null))
                .OrderBy(t => t.AsnNumber)
                .ToListAsync(ct);

            transits = transitRows.Select(t => new TransitCloud
            {
                AsnNumber = t.AsnNumber ?? 0,
                AsnName = t.AsnName ?? $"AS{t.AsnNumber}",
                Method = t.DiscoveryMethod ?? DiscoveryMethod.DirectRouter,
                TargetId = t.TargetId,
                RepresentativeHopAddress = t.Address,
                Live = _liveStats.GetTargetStats(t.TargetId)
            }).ToList();
        }

        return new UpstreamPathSnapshot
        {
            WanInterface = resolvedWanInterface,
            IsPrimary = isPrimary,
            Access = access,
            Transits = transits
        };
    }

    /// <summary>
    /// Per-WAN summaries from current topology. Fully functional from day one because
    /// the gateway's PortTable + the agent's per-port byte rate cache already provide
    /// everything the 3D map needs for WAN link rendering.
    /// </summary>
    public async Task<IReadOnlyList<WanSummary>> GetWansAsync(CancellationToken ct = default)
    {
        if (_wanCache != null && DateTime.UtcNow < _wanCacheExpiry)
        {
            RefreshWanLiveRates(_wanCache);
            return _wanCache;
        }

        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return Array.Empty<WanSummary>();

        // Read wan1...wan6 from raw device JSON instead of relying on
        // port_table.is_uplink, which may not be set for PPPoE, VLAN-tagged,
        // or GRE tunnel connections.
        string? deviceJson;
        try
        {
            deviceJson = await _connectionService.Client.GetDevicesRawJsonAsync(ct);
            if (string.IsNullOrEmpty(deviceJson))
                return Array.Empty<WanSummary>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetWansAsync: failed to fetch UniFi devices");
            return Array.Empty<WanSummary>();
        }

        // Fetch WAN network configs for friendly names (covers GRE tunnels and
        // other virtual WANs that don't have port_table entries).
        var networkGroupToName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var wanConfigs = await _connectionService.Client.GetWanConfigsAsync(ct);
            foreach (var wc in wanConfigs.Where(w => !string.IsNullOrEmpty(w.WanNetworkgroup)))
                networkGroupToName[wc.WanNetworkgroup!] = wc.Name;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetWansAsync: failed to fetch WAN configs for names");
        }

        var results = new List<WanSummary>();

        using (var doc = System.Text.Json.JsonDocument.Parse(deviceJson))
        {
            var root = doc.RootElement;
            var devices = root.ValueKind == System.Text.Json.JsonValueKind.Array
                ? root
                : root.TryGetProperty("data", out var data) ? data : root;

            foreach (var device in devices.EnumerateArray())
            {
                var deviceType = device.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;
                if (deviceType != "ugw" && deviceType != "udm" && deviceType != "uxg")
                    continue;

                var gwMac = device.TryGetProperty("mac", out var macProp) ? macProp.GetString() : null;
                gwMac = gwMac?.ToLowerInvariant().Replace('-', ':') ?? "";

                // Build port_idx lookups from port_table and ethernet_overrides
                var portInfo = new Dictionary<int, (string? networkName, string? name, int speed)>();
                if (device.TryGetProperty("port_table", out var portTable) &&
                    portTable.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var port in portTable.EnumerateArray())
                    {
                        if (!port.TryGetProperty("port_idx", out var idxProp) || !idxProp.TryGetInt32(out var idx))
                            continue;
                        var networkName = port.TryGetProperty("network_name", out var nnProp) ? nnProp.GetString() : null;
                        var name = port.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : null;
                        var speed = port.TryGetProperty("speed", out var speedProp) && speedProp.TryGetInt32(out var spd) ? spd : 0;
                        portInfo[idx] = (networkName, name, speed);
                    }
                }

                var ifnameToNetworkGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (device.TryGetProperty("ethernet_overrides", out var ethOverrides) &&
                    ethOverrides.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var ov in ethOverrides.EnumerateArray())
                    {
                        var ifn = ov.TryGetProperty("ifname", out var ifnProp) ? ifnProp.GetString() : null;
                        var ng = ov.TryGetProperty("networkgroup", out var ngProp) ? ngProp.GetString() : null;
                        if (!string.IsNullOrEmpty(ifn) && !string.IsNullOrEmpty(ng))
                            ifnameToNetworkGroup[ifn] = ng;
                    }
                }

                for (int i = 1; i <= 6; i++)
                {
                    var wanKey = $"wan{i}";
                    if (!device.TryGetProperty(wanKey, out var wanObj)) continue;

                    var uplinkIfname = wanObj.TryGetProperty("uplink_ifname", out var uplinkProp)
                        ? uplinkProp.GetString() : null;
                    if (string.IsNullOrEmpty(uplinkIfname)) continue;

                    var ip = wanObj.TryGetProperty("ip", out var ipProp) ? ipProp.GetString() : null;
                    var wanSpeed = wanObj.TryGetProperty("speed", out var wanSpeedProp) &&
                        wanSpeedProp.TryGetInt32(out var ws) && ws > 0 ? ws : 0;

                    string? interfaceKey = null;
                    string? friendlyName = null;
                    int linkSpeed = wanSpeed;

                    if (wanObj.TryGetProperty("port_idx", out var portIdxProp) &&
                        portIdxProp.TryGetInt32(out var portIdx) &&
                        portInfo.TryGetValue(portIdx, out var pi))
                    {
                        interfaceKey = pi.networkName;
                        friendlyName = pi.name;
                        if (linkSpeed == 0 && pi.speed > 0) linkSpeed = pi.speed;
                    }

                    // Physical port name (e.g. "eth6" for VLAN-tagged "eth6.100",
                    // same as uplinkIfname for non-VLAN connections).
                    var physicalIfname = wanObj.TryGetProperty("ifname", out var ifnProp) ? ifnProp.GetString() : null;

                    // For virtual WANs (GRE, etc.) without port_table entries,
                    // resolve the friendly name from WAN network configs via
                    // ethernet_overrides networkgroup or wan key convention.
                    if (string.IsNullOrEmpty(friendlyName))
                    {
                        var lookupIfname = physicalIfname ?? uplinkIfname;
                        string? networkGroup = null;
                        if (!string.IsNullOrEmpty(lookupIfname) && ifnameToNetworkGroup.TryGetValue(lookupIfname, out var ng))
                            networkGroup = ng;
                        networkGroup ??= i == 1 ? "WAN" : $"WAN{i}";
                        if (networkGroupToName.TryGetValue(networkGroup, out var configName))
                            friendlyName = configName;
                    }

                    interfaceKey ??= i == 1 ? "wan" : $"wan{i}";

                    results.Add(new WanSummary
                    {
                        WanInterface = interfaceKey,
                        FriendlyName = string.IsNullOrEmpty(friendlyName) ? null : friendlyName,
                        IsPrimary = results.Count == 0,
                        GatewayMac = gwMac,
                        GatewayPortName = friendlyName,
                        UplinkIfName = uplinkIfname,
                        PhysicalIfName = physicalIfname,
                        LinkSpeedMbps = linkSpeed > 0 ? linkSpeed : (int?)null,
                        IpAddress = ip,
                        IpClass = NetworkUtilities.ClassifyPublicAddress(ip)
                    });
                }

                if (results.Count > 0) break;
            }
        }

        _wanCache = results;
        _wanCacheExpiry = DateTime.UtcNow.AddSeconds(30);
        RefreshWanLiveRates(results);
        return results;
    }

    private void RefreshWanLiveRates(IReadOnlyList<WanSummary> wans)
    {
        if (wans.Count == 0) return;
        var gwMac = wans[0].GatewayMac;
        if (string.IsNullOrEmpty(gwMac)) return;
        foreach (var wan in wans)
        {
            // ppp* tunnel for PPPoE, physical port otherwise (issue #669): the
            // physical port stays active under PPPoE and over-counts (overhead +
            // sibling VLANs), while VLAN sub-interfaces double-count on some
            // kernels. The other name remains a fallback in case the preferred
            // interface has no SNMP rate yet.
            var preferred = NetworkUtilities.PreferredWanCounterInterface(wan.PhysicalIfName, wan.UplinkIfName);
            var fallback = preferred == wan.PhysicalIfName ? wan.UplinkIfName : wan.PhysicalIfName;
            PortLiveRate? portRate = null;
            if (!string.IsNullOrEmpty(preferred))
                portRate = _liveStats.GetPortRate(gwMac, preferred);
            if (portRate == null && !string.IsNullOrEmpty(fallback) && fallback != preferred)
                portRate = _liveStats.GetPortRate(gwMac, fallback);
            if (portRate != null)
            {
                // GetPortRate convention: DownBps = port TX, UpBps = port RX.
                // WAN port: TX = to internet = uploads (LiveRateInBps),
                //           RX = from internet = downloads (LiveRateOutBps).
                wan.LiveRateInBps = portRate.DownBps;
                wan.LiveRateOutBps = portRate.UpBps;
                continue;
            }
            var deviceLive = _liveStats.GetForDevice(gwMac);
            wan.LiveRateInBps = deviceLive?.RateInBps;
            wan.LiveRateOutBps = deviceLive?.RateOutBps;
        }
    }

    /// <summary>
    /// The tracer encodes the inferred role (BNG / CMTS / OLT / etc.) into AutoLabel
    /// alongside the human-facing text. Until the tracer ships, AutoLabel is empty;
    /// fall back to AccessHop as the generic positional role.
    /// </summary>
    private static string? FormatAccessTechnology(AccessTechnology tech) => tech switch
    {
        AccessTechnology.Unknown => null,
        AccessTechnology.Gpon => "GPON",
        AccessTechnology.XgsPon => "XGS-PON",
        AccessTechnology.Docsis => "DOCSIS",
        AccessTechnology.PppoE => "PPPoE",
        AccessTechnology.DirectEthernet => "Active Ethernet",
        AccessTechnology.FixedWireless => "Fixed Wireless",
        AccessTechnology.Satellite => "Satellite",
        AccessTechnology.Cellular => "Cellular",
        AccessTechnology.Other => "Other",
        _ => tech.ToString()
    };

    private static UpstreamRole MapRoleFromAutoLabel(string? autoLabel)
    {
        if (string.IsNullOrEmpty(autoLabel)) return UpstreamRole.AccessHop;
        var s = autoLabel.ToUpperInvariant();
        if (s.Contains("OLT")) return UpstreamRole.Olt;
        if (s.Contains("CMTS")) return UpstreamRole.Cmts;
        if (s.Contains("BNG") || s.Contains("BRAS")) return UpstreamRole.Bng;
        if (s.Contains("AGGREGATION")) return UpstreamRole.Aggregation;
        if (s.Contains("BORDER")) return UpstreamRole.Border;
        if (s.Contains("TRANSIT")) return UpstreamRole.Transit;
        if (s.Contains("PATH PROXY")) return UpstreamRole.PathProxy;
        return UpstreamRole.AccessHop;
    }
}

/// <summary>One WAN's full upstream cloud graph for the 3D map.</summary>
public record UpstreamPathSnapshot
{
    public required string WanInterface { get; init; }
    public required bool IsPrimary { get; init; }
    public required AccessIspCloud Access { get; init; }
    public IReadOnlyList<TransitCloud> Transits { get; init; } = Array.Empty<TransitCloud>();
}

/// <summary>
/// The access ISP cloud rendered immediately past the WAN link. Always populated
/// (every WAN has an access ISP), even on secondary WANs.
/// </summary>
public record AccessIspCloud
{
    public string? AccessTechnology { get; init; }       // "Gpon" / "Docsis" / ...
    public string? L2NeighborOui { get; init; }          // vendor name (Calix / Arris / ...)
    public int? AsnNumber { get; init; }
    public string? AsnName { get; init; }
    public IReadOnlyList<AccessHop> Hops { get; init; } = Array.Empty<AccessHop>();
    public bool IsCgnat { get; init; }
}

public record AccessHop
{
    public required string TargetId { get; init; }
    public required string Label { get; init; }
    public string? PtrHostname { get; init; }
    public required string Address { get; init; }
    public UpstreamRole Role { get; init; }
    public TargetLiveStats? Live { get; init; }
}

/// <summary>
/// A transit-ASN cloud beyond the access cloud, only populated for the primary WAN
/// in MVP. The Method field is the honesty signal: DirectRouter renders solid,
/// PathProxy renders dashed with the "via path" badge, UserProvided renders solid
/// with the "user-added" badge, Unresolved has no live stats and renders neutral.
/// </summary>
public record TransitCloud
{
    public int AsnNumber { get; init; }
    public required string AsnName { get; init; }
    public DiscoveryMethod Method { get; init; }
    public string? TargetId { get; init; }
    public string? RepresentativeHopAddress { get; init; }
    public TargetLiveStats? Live { get; init; }
}

public record WanSummary
{
    public required string WanInterface { get; init; }
    public string? FriendlyName { get; init; }
    public required bool IsPrimary { get; init; }
    public string? GatewayMac { get; init; }
    public string? GatewayPortName { get; init; }
    public string? UplinkIfName { get; init; }
    public string? PhysicalIfName { get; init; }
    public int? LinkSpeedMbps { get; init; }
    public double? LiveRateInBps { get; set; }
    public double? LiveRateOutBps { get; set; }
    public string? IpAddress { get; init; }
    public PublicAddressClass IpClass { get; init; }
}
