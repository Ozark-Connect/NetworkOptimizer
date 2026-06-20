using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Storage.Services;
using NetworkOptimizer.Web.Services;

namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// Point-in-time per-port statistics for the Live View port playback table.
/// Reads <c>interface_counters</c> at the current map scrubber position (or the
/// latest sample when no instant is supplied) and enriches each port with the
/// UniFi-correlated friendly name, port number, media type and negotiated link
/// speed from <see cref="InterfaceNameMap"/>.
/// </summary>
public static class PortStatsEndpoints
{
    // Matches a VLAN sub-interface such as "eth0.100" or "switch0.99": a base
    // interface name followed by a dotted numeric VLAN id.
    private static readonly Regex SubInterface = new(@"^(?<base>.+)\.(?<vlan>\d+)$", RegexOptions.Compiled);

    // Switch SNMP ifTables expose logical interfaces - link aggregations, VLAN SVIs, and
    // stack / tunnel / VPN / user-defined ports - alongside the real physical ports. They are
    // noise in the port stats table. Real ports ("Port 1", "SFP+ 1") and renamed ports never
    // match these patterns, and VLAN sub-interfaces ("eth0.100") keep their dotted form. This
    // is a reader-side display filter only; nothing is removed from InfluxDB or the live cache.
    private static readonly Regex JunkSwitchInterface = new(
        @"^(Port-Channel\d+|Logical-int \d+|User Defined Port \d+|stack-port|tunnel\d+|OpenVPN|\d+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/monitoring/port-stats", async (
            MonitoringInfluxClient influx,
            MonitoringLiveStats liveStats,
            IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
            string? macs,
            DateTime? at,
            CancellationToken ct) =>
        {
            var requested = (macs ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            var filterMacs = requested.Count > 0 ? requested : null;

            // Live mode is served from the in-memory cache (sub-ms, no InfluxDB load);
            // only historic scrubbing (an explicit timestamp) hits InfluxDB.
            IReadOnlyList<MonitoringInfluxClient.PortStatsPoint> points = at.HasValue
                ? await influx.QueryPortStatsAsync(filterMacs, at.Value.ToUniversalTime(), ct)
                : liveStats.GetPortStatsSnapshot(filterMacs);

            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Device display name + type ("ap" / "switch" / "gateway") from the fabric
            // monitoring targets, matching how the device health chart resolves names.
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.TargetType == MonitoringTargetType.Fabric && t.DeviceMac != null)
                .Select(t => new { t.DeviceMac, t.Name, t.AutoLabel })
                .ToListAsync(ct);
            var targetByMac = targets
                .GroupBy(t => Norm(t.DeviceMac!))
                .ToDictionary(g => g.Key, g => g.First());

            // UniFi-correlated interface metadata keyed by (mac, ifName).
            var nameMaps = await db.InterfaceNameMaps.AsNoTracking().ToListAsync(ct);
            var mapByKey = new Dictionary<(string mac, string ifName), InterfaceNameMap>();
            var friendlyByMacIf = new Dictionary<(string mac, string ifName), string>();
            foreach (var m in nameMaps)
            {
                var k = (Norm(m.DeviceMac), m.IfName);
                mapByKey[k] = m;
                if (!string.IsNullOrWhiteSpace(m.FriendlyName))
                    friendlyByMacIf[k] = m.FriendlyName!;
            }

            string? ResolveFriendly(string mac, string ifName)
            {
                if (friendlyByMacIf.TryGetValue((mac, ifName), out var direct))
                    return direct;
                // VLAN sub-interface: inherit the parent port's friendly name and tag
                // it with the VLAN id, e.g. eth0.100 -> "Fiber ISP (100)".
                var sub = SubInterface.Match(ifName);
                if (sub.Success && friendlyByMacIf.TryGetValue((mac, sub.Groups["base"].Value), out var parent))
                    return $"{parent} ({sub.Groups["vlan"].Value})";
                return null;
            }

            var devices = points
                .GroupBy(p => p.DeviceMac, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var mac = Norm(g.Key);
                    targetByMac.TryGetValue(mac, out var target);
                    var type = NormalizeType(target?.AutoLabel);
                    return new
                    {
                        mac = g.Key,
                        name = !string.IsNullOrWhiteSpace(target?.Name) ? target!.Name : g.Key,
                        type,
                        // Switches only: drop logical-interface noise before the friendly-name
                        // resolution below. Physical and renamed ports pass through untouched.
                        ports = g
                            .Where(p => type != "switch" || !IsJunkSwitchInterface(p.IfName))
                            .Select(p =>
                            {
                                mapByKey.TryGetValue((mac, p.IfName), out var nm);
                                // VLAN sub-interfaces sit on a physical port, so they inherit
                                // the parent port's number and media when their own row has
                                // none (eth0.100 takes eth0's port number and SFP flag).
                                InterfaceNameMap? parent = null;
                                var sub = SubInterface.Match(p.IfName);
                                if (sub.Success)
                                    mapByKey.TryGetValue((mac, sub.Groups["base"].Value), out parent);
                                var portNumber = nm?.PortNumber ?? parent?.PortNumber;
                                // Single wired client on this physical port (links to the
                                // Client Dashboard). Keyed on the port's OWN number so a
                                // VLAN sub-interface doesn't borrow the parent's client.
                                var client = nm?.PortNumber is int pnum ? liveStats.GetPortClient(mac, pnum) : null;
                                return new
                                {
                                    ifName = p.IfName,
                                    portId = p.PortId,
                                    portNumber,
                                    connectedMac = client?.Mac,
                                    connectedIp = client?.Ip,
                                    connectedName = client?.Name,
                                    // Agent-resolved WAN/carrier label wins (e.g. gre1 ->
                                    // "WAN3 - AT&T Wireless"); otherwise the port_table /
                                    // sub-interface friendly name.
                                    friendlyName = liveStats.GetInterfaceLabel(mac, p.IfName)
                                        ?? ResolveFriendly(mac, p.IfName),
                                    isSfp = nm?.IsSfp ?? parent?.IsSfp,
                                    linkSpeedMbps = nm?.SpeedMbps,
                                    operStatus = p.OperStatus,
                                    rateInBps = p.RateInBps,
                                    rateOutBps = p.RateOutBps,
                                    bytesIn = p.BytesIn,
                                    bytesOut = p.BytesOut,
                                    ucastPktsIn = p.UcastPktsIn,
                                    ucastPktsOut = p.UcastPktsOut,
                                    mcastPktsIn = p.McastPktsIn,
                                    mcastPktsOut = p.McastPktsOut,
                                    bcastPktsIn = p.BcastPktsIn,
                                    bcastPktsOut = p.BcastPktsOut,
                                    errorsIn = p.ErrorsIn,
                                    errorsOut = p.ErrorsOut,
                                    discardsIn = p.DiscardsIn,
                                    discardsOut = p.DiscardsOut,
                                };
                            })
                            // Physical ports first, ordered by port number; virtual /
                            // sub-interfaces (no port number) fall to the bottom by name.
                            .OrderBy(p => p.portNumber ?? int.MaxValue)
                            .ThenBy(p => p.ifName, StringComparer.OrdinalIgnoreCase)
                            .ToList()
                    };
                })
                .OrderBy(d => d.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return Results.Ok(new { at = at?.ToUniversalTime().ToString("o"), devices });
        });
    }

    private static string Norm(string mac) =>
        string.IsNullOrEmpty(mac) ? string.Empty : mac.ToLowerInvariant().Replace('-', ':');

    // True when a switch interface is logical noise that should not appear in the port stats
    // table (see JunkSwitchInterface). Callers gate this on device type == "switch".
    private static bool IsJunkSwitchInterface(string? ifName) =>
        !string.IsNullOrEmpty(ifName) && JunkSwitchInterface.IsMatch(ifName);

    private static string NormalizeType(string? autoLabel) => (autoLabel ?? "").ToLowerInvariant() switch
    {
        "ap" => "ap",
        "switch" => "switch",
        "gateway" => "gateway",
        _ => "unknown"
    };
}
