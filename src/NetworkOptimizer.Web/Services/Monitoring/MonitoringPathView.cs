using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;

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

        var l2NeighborMac = wanCtx?.L2NeighborMac ?? settings?.WanNeighborMac;
        var l2NeighborOui = wanCtx?.L2NeighborOui ?? settings?.WanNeighborOui;
        var accessTech = wanCtx?.AccessTechnology ?? settings?.AccessTechnology ?? AccessTechnology.Unknown;

        var isCgnat = l2NeighborMac != null
            && !string.IsNullOrEmpty(wan?.IpAddress)
            && NetworkUtilities.ClassifyPublicAddress(wan.IpAddress) == PublicAddressClass.Cgnat;

        var access = new AccessIspCloud
        {
            AccessTechnology = accessTech.ToString(),
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
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return Array.Empty<WanSummary>();

        List<UniFiDeviceResponse> devices;
        try
        {
            devices = (await _connectionService.Client.GetDevicesAsync(ct))?.ToList()
                       ?? new List<UniFiDeviceResponse>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetWansAsync: failed to fetch UniFi devices");
            return Array.Empty<WanSummary>();
        }

        var gateway = devices.FirstOrDefault(d => d.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway);
        if (gateway?.PortTable == null) return Array.Empty<WanSummary>();

        var results = new List<WanSummary>();
        var wanPorts = gateway.PortTable
            .Where(p => p.IsUplink && !string.IsNullOrEmpty(p.NetworkName))
            .OrderBy(p => p.PortIdx)
            .ToList();
        if (wanPorts.Count == 0)
        {
            // Fall back to any port whose network_name looks like a WAN.
            wanPorts = gateway.PortTable
                .Where(p => p.NetworkName != null && p.NetworkName.StartsWith("wan", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p.PortIdx)
                .ToList();
        }

        for (int i = 0; i < wanPorts.Count; i++)
        {
            var port = wanPorts[i];
            // Live throughput for this WAN port comes from the agent's per-port rate
            // cache (which we maintain anyway for AP backhaul lookups). The cache key
            // is (gateway MAC, port_idx).
            var deviceLive = _liveStats.GetForDevice(gateway.Mac);

            results.Add(new WanSummary
            {
                WanInterface = port.NetworkName ?? $"wan{i + 1}",
                FriendlyName = string.IsNullOrEmpty(port.Name) ? null : port.Name,
                IsPrimary = i == 0,
                GatewayMac = gateway.Mac.ToLowerInvariant().Replace('-', ':'),
                GatewayPortName = port.Name,
                LinkSpeedMbps = port.Speed > 0 ? port.Speed : (int?)null,
                LiveRateInBps = deviceLive?.RateInBps,
                LiveRateOutBps = deviceLive?.RateOutBps,
                IpAddress = port.Ip,
                IpClass = NetworkUtilities.ClassifyPublicAddress(port.Ip)
            });
        }

        return results;
    }

    /// <summary>
    /// The tracer encodes the inferred role (BNG / CMTS / OLT / etc.) into AutoLabel
    /// alongside the human-facing text. Until the tracer ships, AutoLabel is empty;
    /// fall back to AccessHop as the generic positional role.
    /// </summary>
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
    public int? LinkSpeedMbps { get; init; }
    public double? LiveRateInBps { get; init; }
    public double? LiveRateOutBps { get; init; }
    public string? IpAddress { get; init; }
    public PublicAddressClass IpClass { get; init; }
}
