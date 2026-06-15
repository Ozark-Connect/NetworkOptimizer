using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.Web.Services.Ssh;

namespace NetworkOptimizer.Web.Services.Monitoring;

/// <summary>
/// Orchestrates the upstream tracer wizard (spec 5.5). Singleton because the wizard's
/// state has to survive Blazor circuit reconnects and be observable by multiple UI
/// instances. The state machine runs in the background; the UI polls
/// <see cref="State"/> for progress and renders ReviewingResults when the run finishes.
///
/// Iteration 1 implements the discovery scaffolding:
/// - DetectingPublicIp: read WAN IP from UniFi PortTable, classify (CGNAT / DoubleNat /
///   IPv6 / etc.), surface unsupported cases honestly.
/// - DiscoveringL2Neighbor: SSH to gateway, run `ip neigh show dev &lt;wanIface&gt;`,
///   parse the L2 neighbor MAC, look up the OUI vendor for first-mile-device labeling.
/// - TracingAccessIsp / TracingTransitAsns / ReviewingResults: state machine in place;
///   actual traceroute orchestration + per-ASN fallback ladder land in iteration 2.
/// </summary>
public class UpstreamTracerService
{
    private readonly UniFiConnectionService _connectionService;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly AsnResolutionService _asnResolution;
    private readonly LocalProbeExecutor _localProbe;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IspHealth.IspHealthService _ispHealth;
    private readonly NetworkOptimizer.Audit.Services.IeeeOuiDatabase _ouiDb;
    private readonly ILogger<UpstreamTracerService> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Task? _runningTask;

    // IPs that belong to *our* gateway (LAN side, WAN side, management VLANs).
    // Used to keep our own gateway out of the access-ISP hop list when the
    // traceroute's first hop is a private/CGNAT address. Collected during
    // DetectPublicIpAsync from the gateway's port_table.
    private readonly HashSet<string> _gatewayIps = new(StringComparer.OrdinalIgnoreCase);

    // The OS-level interface name backing the WAN port (e.g. "ethN",
    // "ethN.M" for VLAN-tagged, "pppN" for PPPoE), read from the device's
    // wan1...wan6 uplink_ifname during DetectPublicIpAsync. The L2-neighbor
    // step uses this to target `ip neigh show dev <iface>` correctly.
    private string? _wanUplinkIfName;

    public UpstreamTracerState State { get; private set; } = new();

    // CDN / anycast rotation. Every entry must be a globally anycast IP that
    // routes to a local PoP from anywhere on the public internet - that's the
    // only way one hardcoded address gives every install a useful trace. The
    // old list mixed in regional unicast (Akamai 23.218.94.94 -> Tokyo, Meta
    // 163.70.128.35 -> Paris) which produced transpacific/transatlantic paths
    // and misleading transit attribution. The list is a typed collection so
    // IPv6 endpoints can be added later without restructuring (decision 5b).
    // Endpoints split into two intents:
    //   - DestinationProbe (default): we use this address to monitor the
    //     destination service end-to-end. Its ASN is excluded from the
    //     transit-router pool because intermediate hops in the dest's own
    //     ASN are just last-mile-to-dest, not real transit.
    //   - TransitProbe: chosen specifically because tracing to it forces
    //     the path through that ASN's network. We WANT that ASN to surface
    //     as a transit-router candidate, and we don't treat the endpoint
    //     itself as a path-end monitoring target.
    private static readonly TraceEndpoint[] CdnRotation =
    {
        new("Cloudflare", "1.1.1.1"),                                // AS13335
        new("Google", "8.8.8.8"),                                    // AS15169
        new("Quad9", "9.9.9.9"),                                     // AS19281 - PCH-anycast
        new("OpenDNS", "208.67.222.222"),                            // AS36692 - Cisco Umbrella
        new("Lumen", "4.2.2.1", IsTransitProbe: true),               // AS3356  - probe to surface Lumen as transit
        new("Apple", "17.253.144.10"),                               // AS714
        new("Microsoft", "13.107.42.14"),                            // AS8068  - M365 SharePoint anycast
        new("Fastly", "151.101.1.69"),                               // AS54113 - reaches local PoP via anycast
        new("Akamai", "23.0.0.1"),                                   // AS20940 - global netarch anycast loopback
        new("AT&T", "12.0.1.28", IsTransitProbe: true),               // AS7018  - probe to surface AT&T as transit
        new("INDATEL", "216.176.4.153", IsTransitProbe: true, EndpointIsTransitHop: true) // AS30517 - INDATEL on GLC (Everstream) infra
    };

    private record TraceEndpoint(string Label, string Address, bool IsTransitProbe = false, bool EndpointIsTransitHop = false);

    public UpstreamTracerService(
        UniFiConnectionService connectionService,
        IGatewaySshService gatewaySsh,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        LocalProbeExecutor localProbe,
        IServiceScopeFactory scopeFactory,
        IspHealth.IspHealthService ispHealth,
        NetworkOptimizer.Audit.Services.IeeeOuiDatabase ouiDb,
        ILogger<UpstreamTracerService> logger)
    {
        _connectionService = connectionService;
        _gatewaySsh = gatewaySsh;
        _dbFactory = dbFactory;
        _asnResolution = asnResolution;
        _localProbe = localProbe;
        _scopeFactory = scopeFactory;
        _ispHealth = ispHealth;
        _ouiDb = ouiDb;
        _logger = logger;
    }

    /// <summary>
    /// Rehydrate the in-memory <see cref="State"/> from persisted DB rows when
    /// the service starts cold (process restart). Safe to call multiple times -
    /// no-ops if a run is in flight or the state already reflects committed
    /// data. Without this the wizard panel showed "Ready" after every restart
    /// even when monitoring targets were already saved.
    /// </summary>
    public async Task RehydrateFromDbAsync(CancellationToken ct = default)
    {
        if (State.Step != TracerStep.Idle) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ctx = await db.WanDiscoveryContexts
                .OrderByDescending(c => c.LastDiscoveryAt ?? c.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            if (ctx == null) return;

            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.WanInterface == ctx.WanInterface
                            && (t.TargetType == MonitoringTargetType.AccessIsp
                                || t.TargetType == MonitoringTargetType.Transit))
                .ToListAsync(ct);
            if (targets.Count == 0) return;

            var accessRows = targets
                .Where(t => t.TargetType == MonitoringTargetType.AccessIsp)
                .OrderBy(t => t.Id)
                .ToList();
            var transitRows = targets
                .Where(t => t.TargetType == MonitoringTargetType.Transit)
                .OrderBy(t => t.AsnNumber)
                .ToList();

            var hydrated = new UpstreamTracerState
            {
                Step = TracerStep.Done,
                StartedAt = ctx.LastDiscoveryAt,
                CompletedAt = ctx.LastDiscoveryAt,
                WanInterface = ctx.WanInterface,
                WanNeighborMac = ctx.L2NeighborMac,
                WanNeighborIp = ctx.L2NeighborIp,
                WanNeighborOuiVendor = ctx.L2NeighborOui,
                AccessTechnology = ctx.AccessTechnology,
                CurrentActivity = "Targets saved. The monitor is probing them on its regular cycle.",
                AccessHops = accessRows.Select(t => new AccessHopCandidate
                {
                    TargetId = t.TargetId,
                    Label = t.Name,
                    Address = t.Address,
                    PtrHostname = t.PtrHostname,
                    AsnNumber = t.AsnNumber,
                    AsnName = t.AsnName,
                    Role = Enum.TryParse<UpstreamRole>(t.AutoLabel, out var role) ? role : UpstreamRole.AccessHop,
                    HopNumber = 0,
                    RespondedTo = t.DiscoveredProbeMode ?? t.ProbeMode,
                    Method = t.DiscoveryMethod ?? DiscoveryMethod.DirectRouter,
                    Enabled = t.Enabled,
                }).ToList(),
                TransitAsns = transitRows.Select(t => new TransitAsnCandidate
                {
                    AsnNumber = t.AsnNumber ?? 0,
                    AsnName = t.AsnName ?? $"AS{t.AsnNumber}",
                    Method = t.DiscoveryMethod ?? DiscoveryMethod.DirectRouter,
                    TargetId = t.TargetId,
                    HopAddress = t.Address,
                    HopHostname = null,
                    RespondedTo = t.DiscoveredProbeMode,
                    PathProxyTarget = (t.DiscoveryMethod == DiscoveryMethod.PathProxy) ? t.Address : null,
                    Enabled = t.Enabled,
                }).ToList(),
            };
            await _stateLock.WaitAsync(ct);
            try
            {
                if (State.Step == TracerStep.Idle) State = hydrated;
            }
            finally { _stateLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Upstream tracer rehydrate from DB failed; state stays Idle");
        }
    }

    /// <summary>
    /// Kick off discovery. Idempotent: if a run is already in progress, returns without
    /// starting another. The UI polls <see cref="State"/> for live progress.
    /// </summary>
    public async Task StartDiscoveryAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (_runningTask != null && !_runningTask.IsCompleted) return;

            var preservedTech = State.AccessTechnology;
            State = new UpstreamTracerState
            {
                Step = TracerStep.DetectingPublicIp,
                StartedAt = DateTime.UtcNow,
                CurrentActivity = "Reading WAN configuration from gateway...",
                AccessTechnology = preservedTech
            };
            _runningTask = Task.Run(() => RunAsync(ct), ct);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    /// <summary>Awaits the in-flight discovery task. Returns immediately if no run is active.</summary>
    public Task WaitForCompletionAsync() => _runningTask ?? Task.CompletedTask;

    /// <summary>Resets state back to Idle. Used by the re-discovery scheduler when a sweep matched committed targets.</summary>
    public void ResetToIdle()
    {
        State = new UpstreamTracerState { Step = TracerStep.Idle };
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            if (!await DetectPublicIpAsync(ct)) return;
            if (!await DiscoverL2NeighborAsync(ct)) return;
            await TraceAccessIspAsync(ct);
            await TraceTransitAsnsAsync(ct);
            await VerifyReachabilityAsync(ct);
            State.Step = TracerStep.ReviewingResults;
            State.CurrentActivity = "Review the discovered upstream path. Confirm to commit.";
            State.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            State.Step = TracerStep.Failed;
            State.FailureMessage = "Discovery cancelled.";
            State.CompletedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Upstream tracer failed");
            State.Step = TracerStep.Failed;
            State.FailureMessage = ex.Message;
            State.CompletedAt = DateTime.UtcNow;
        }
    }

    // ---- Step 1: Detect public IP ----

    private async Task<bool> DetectPublicIpAsync(CancellationToken ct)
    {
        State.Step = TracerStep.DetectingPublicIp;
        State.CurrentActivity = "Reading WAN configuration from gateway...";

        if (!_connectionService.IsConnected || _connectionService.Client == null)
        {
            return Fail("Not connected to UniFi Console.");
        }

        // Fetch raw device JSON to read wan1...wan6 objects directly.
        // These are the authoritative WAN descriptors and correctly report
        // the Linux interface name for all connection types (DHCP, PPPoE,
        // VLAN-tagged, GRE tunnels) - unlike port_table.is_uplink which
        // may not be set for non-standard connections.
        string? deviceJson;
        try
        {
            deviceJson = await _connectionService.Client.GetDevicesRawJsonAsync(ct);
            if (string.IsNullOrEmpty(deviceJson))
                return Fail("Empty device response from UniFi Console.");
        }
        catch (Exception ex)
        {
            return Fail($"Couldn't fetch UniFi devices: {ex.Message}");
        }

        string? wanInterfaceName = null;
        string? wanUplinkIfName = null;
        string? wanIp = null;

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

                // Collect every IP the gateway carries so we can filter our own
                // gateway out of the access-hop classification later.
                _gatewayIps.Clear();
                var portIdxToNetworkName = new Dictionary<int, string>();
                if (device.TryGetProperty("port_table", out var portTable) &&
                    portTable.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var port in portTable.EnumerateArray())
                    {
                        if (port.TryGetProperty("ip", out var ptIpProp))
                        {
                            var ptIp = ptIpProp.GetString();
                            if (!string.IsNullOrEmpty(ptIp)) _gatewayIps.Add(ptIp);
                        }
                        if (port.TryGetProperty("port_idx", out var idxProp) &&
                            idxProp.TryGetInt32(out var idx) &&
                            port.TryGetProperty("network_name", out var nnProp))
                        {
                            var nn = nnProp.GetString();
                            if (!string.IsNullOrEmpty(nn))
                                portIdxToNetworkName[idx] = nn;
                        }
                    }
                }

                for (int i = 1; i <= 6; i++)
                {
                    var wanKey = $"wan{i}";
                    if (!device.TryGetProperty(wanKey, out var wanObj)) continue;

                    var uplinkIfname = wanObj.TryGetProperty("uplink_ifname", out var uplinkProp)
                        ? uplinkProp.GetString() : null;
                    if (string.IsNullOrEmpty(uplinkIfname)) continue;

                    var ip = wanObj.TryGetProperty("ip", out var wanIpProp)
                        ? wanIpProp.GetString() : null;

                    // Derive the WAN key from port_table network_name when available
                    // (e.g. "wan", "wan2") to match convention used by prior code.
                    string interfaceKey;
                    if (wanObj.TryGetProperty("port_idx", out var portIdxProp) &&
                        portIdxProp.TryGetInt32(out var portIdx) &&
                        portIdxToNetworkName.TryGetValue(portIdx, out var networkName))
                    {
                        interfaceKey = networkName;
                    }
                    else
                    {
                        interfaceKey = i == 1 ? "wan" : $"wan{i}";
                    }

                    wanInterfaceName = interfaceKey;
                    wanUplinkIfName = uplinkIfname;
                    wanIp = ip;
                    break;
                }

                if (wanInterfaceName != null) break;
            }
        }

        if (wanInterfaceName == null)
            return Fail("Couldn't identify the WAN port on the gateway.");

        State.WanInterface = wanInterfaceName;
        _wanUplinkIfName = wanUplinkIfName;
        State.WanIpAddress = wanIp;
        State.WanIpClass = NetworkUtilities.ClassifyPublicAddress(wanIp);

        switch (State.WanIpClass)
        {
            case PublicAddressClass.PublicIPv4:
                // happy path
                break;

            case PublicAddressClass.Cgnat:
                State.IsCgnat = true;
                _logger.LogInformation("Tracer: WAN IP is CGNAT ({Ip}); proceeding with discovery", wanIp);
                break;

            case PublicAddressClass.DoubleNat:
                // Per locked Gate 2 decision 8: proceed anyway, traceroute will still
                // reveal the upstream ISP. Surface a small "double-NAT detected" badge.
                State.IsDoubleNat = true;
                _logger.LogInformation("Tracer: WAN IP is RFC1918 ({Ip}); proceeding (double-NAT)", wanIp);
                break;

            case PublicAddressClass.IPv6:
                return Fail("IPv6-only WAN. The upstream tracer is currently IPv4 only; IPv6 path tracing is on the roadmap.");

            case PublicAddressClass.NonGloballyRouted:
                return Fail("We couldn't confidently determine your public path.");

            case PublicAddressClass.Misconfigured:
                return Fail("Your gateway's WAN interface has a loopback / link-local address. Check the gateway's WAN configuration.");

            default:
                return Fail("Couldn't classify the WAN IP address.");
        }

        // Update MonitoringSettings with the classification + WAN context. UI surfaces
        // these for the access-cloud labeling regardless of whether the rest of the
        // tracer completes.
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings != null)
            {
                // The access technology is what the user picked during initial setup;
                // we leave that alone here. The L2 neighbor MAC + OUI vendor get set in
                // the next step.
                settings.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update MonitoringSettings during tracer detect");
        }

        return true;
    }

    // ---- Step 2: Discover L2 neighbor MAC + OUI ----

    private async Task<bool> DiscoverL2NeighborAsync(CancellationToken ct)
    {
        State.Step = TracerStep.DiscoveringL2Neighbor;
        State.CurrentActivity = "Identifying the first device upstream of your gateway...";

        // OS interface candidates, authoritative-first:
        //  1) port_table.uplink_ifname - UniFi's kernel device name for
        //     the uplink, correct for VLAN-tagged sub-interfaces too.
        //  2) `ip -o -4 addr show` line owning the known WAN IP.
        // No default-route lookup: UniFi gateways run policy routing with
        // per-WAN tables, so the default isn't in the main table.
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(_wanUplinkIfName))
        {
            candidates.Add(_wanUplinkIfName);
        }
        if (candidates.Count == 0 && !string.IsNullOrEmpty(State.WanIpAddress))
        {
            var addrCmd = $"ip -o -4 addr show | grep -F ' {State.WanIpAddress}/' | head -1";
            var (addrOk, addrOut) = await _gatewaySsh.RunCommandAsync(addrCmd, TimeSpan.FromSeconds(5), ct);
            if (addrOk && !string.IsNullOrWhiteSpace(addrOut))
            {
                var m = Regex.Match(addrOut, @"^\s*\d+:\s+(?<iface>\S+)\s+inet\s+", RegexOptions.Multiline);
                if (m.Success) candidates.Add(m.Groups["iface"].Value);
            }
        }

        string? neighborMac = null;
        string? neighborIp = null;
        string? wanDevice = null;

        foreach (var ifaceCandidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var cmd = $"ip neigh show dev {ifaceCandidate} 2>/dev/null | head -10";
            var (ok, output) = await _gatewaySsh.RunCommandAsync(cmd, TimeSpan.FromSeconds(5), ct);
            if (!ok || string.IsNullOrWhiteSpace(output)) continue;

            var selected = SelectWanNeighbor(output);
            if (selected != null)
            {
                neighborIp = selected.Value.Ip;
                neighborMac = selected.Value.Mac;
                wanDevice = ifaceCandidate;
                break;
            }
        }

        if (string.IsNullOrEmpty(neighborMac))
        {
            // Not fatal - we can still trace upstream without knowing the L2 neighbor.
            // We just lose the first-mile-device labeling enrichment.
            _logger.LogDebug("Tracer: no L2 neighbor MAC found via ip neigh on any common WAN candidate");
            State.CurrentActivity = "Couldn't identify the first upstream device. Continuing; ISP labels will fall back to hostname lookup.";
            return true;
        }

        State.WanNeighborMac = neighborMac;
        State.WanNeighborIp = neighborIp;

        // OUI lookup via the IEEE database service that's already loaded at app start
        // (~39k entries cached). The OuiVendors EF table is unused; this is the source
        // of truth.
        try
        {
            State.WanNeighborOuiVendor = _ouiDb.GetVendor(neighborMac);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Tracer: OUI lookup failed");
        }

        // Persist the WAN neighbor info to MonitoringSettings so the access cloud label
        // survives across discovery runs and is available to MonitoringPathView.
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
            if (settings != null)
            {
                settings.WanNeighborMac = State.WanNeighborMac;
                settings.WanNeighborOui = State.WanNeighborOuiVendor;
                settings.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to persist WAN neighbor info to MonitoringSettings");
        }

        State.CurrentActivity = State.WanNeighborOuiVendor != null
            ? $"L2 neighbor identified: {State.WanNeighborOuiVendor} ({neighborMac})"
            : $"L2 neighbor MAC: {neighborMac} (vendor unknown)";

        return true;
    }

    /// <summary>
    /// Picks the WAN-side L2 neighbor from `ip neigh show dev &lt;wan&gt;` output. A CPE
    /// bridged in front of the gateway (an ISP modem/router in passthrough) lists both
    /// its LAN-side RFC1918 address and the carrier-side address under the same MAC;
    /// the LAN-side entry often sorts first, and taking the first lladdr line mislabeled
    /// a private CPE IP as an ISP hop. Preference order: address class
    /// (public &gt; CGNAT &gt; private) then freshness (REACHABLE/DELAY/PROBE over STALE).
    /// FAILED and INCOMPLETE entries carry no lladdr and never match. IPv6 link-local
    /// entries are skipped.
    /// </summary>
    public static (string Ip, string Mac)? SelectWanNeighbor(string? ipNeighOutput)
    {
        if (string.IsNullOrWhiteSpace(ipNeighOutput)) return null;

        (string Ip, string Mac)? best = null;
        var bestScore = -1;
        foreach (Match m in Regex.Matches(ipNeighOutput,
            @"^(\S+)\s+.*lladdr\s+([0-9a-fA-F:]{17})(.*)$", RegexOptions.Multiline))
        {
            var ipText = m.Groups[1].Value;
            if (!System.Net.IPAddress.TryParse(ipText, out var ip)) continue;
            if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;

            var classScore = NetworkUtilities.ClassifyPublicAddress(ip) switch
            {
                PublicAddressClass.PublicIPv4 => 3,
                PublicAddressClass.Cgnat => 2,
                PublicAddressClass.DoubleNat => 1,
                _ => 0
            };
            var freshScore = m.Groups[3].Value.Contains("STALE", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            var score = classScore * 10 + freshScore;
            if (score > bestScore)
            {
                bestScore = score;
                best = (ipText, m.Groups[2].Value.ToLowerInvariant());
            }
        }
        return best;
    }

    /// <summary>
    /// Whether an L2 neighbor address may be proposed as a monitored access hop.
    /// Carrier-side addresses (public or CGNAT) qualify; RFC1918 addresses are the
    /// CPE's LAN side and must never be suggested as ISP infrastructure.
    /// </summary>
    public static bool IsInjectableAccessHopAddress(string? ip) =>
        NetworkUtilities.ClassifyPublicAddress(ip) is PublicAddressClass.PublicIPv4 or PublicAddressClass.Cgnat;

    // ---- Step 3: Trace the access ISP + Step 4 transit ASNs ----
    //
    // Both steps actually share one round of work: a single parallel traceroute sweep
    // produces all the hop data we need to classify access-ISP hops, transit ASN
    // hops, and the destination ASN. Split into two named state-machine steps for UI
    // clarity, but the underlying work runs once.

    /// <summary>
    /// Per-hop attribution computed once and shared between access + transit steps.
    /// </summary>
    private record AttributedHop(int HopNumber, string Address, string? Hostname, ProbeMode RespondedTo, AsnLookup? Asn);
    private List<AttributedHop> _mergedHops = new();
    private List<AttributedHop> _accessHopsResolved = new();

    // Raw per-trace hop sequences from the last discovery sweep. Kept so commit can
    // persist SAME-PATH hop ordering to UpstreamDiscoveries: the merged pool (_mergedHops)
    // dedupes hop IPs across ~22 anycast traces, so its hop numbers are not on a common
    // path and cannot prove "B routes through A". A single trace's sequence can.
    private List<TracerouteResult> _lastTraces = new();

    private async Task TraceAccessIspAsync(CancellationToken ct)
    {
        State.Step = TracerStep.TracingAccessIsp;
        State.CurrentActivity = "Running parallel traceroutes to major internet endpoints...";
        State.Traces = new List<TraceSummary>();

        // Spawn 10 traceroutes (5 endpoints × 2 modes) in parallel and merge once
        // they all settle. Each traceroute is capped at 10s, so wall-clock for the
        // whole sweep is ~10s + a bit of overhead.
        var tasks = new List<Task<(string Label, TracerouteResult Result)>>();
        foreach (var endpoint in CdnRotation)
        {
            tasks.Add(TraceOneAsync(endpoint, ProbeMode.Icmp, ct));
            tasks.Add(TraceOneAsync(endpoint, ProbeMode.Udp, ct));
        }
        var results = await Task.WhenAll(tasks);
        // Keep the raw per-trace sequences for same-path hop-order persistence at commit.
        _lastTraces = results.Select(r => r.Result).ToList();

        // Summarize per CDN for the live progress UI.
        foreach (var (label, result) in results)
        {
            State.Traces.Add(new TraceSummary
            {
                CdnLabel = label,
                CdnEndpoint = result.Target.Address,
                Mode = result.ModeUsed,
                HopsRecorded = result.Hops.Count,
                HopsResponding = result.Hops.Count(h => h.Responded),
                Reached = result.Reached,
                Error = result.ErrorMessage
            });
        }
        State.CurrentActivity = $"Traces complete: {State.Traces.Count(t => t.HopsResponding > 0)} of {State.Traces.Count} returned data. Attributing hops to ASNs...";

        // Merge hops across all traces by (hop IP -> first mode that saw it). We don't
        // care which CDN trace surfaced the hop, only that we saw it; ASN attribution
        // is per-IP and dedupes naturally on its way out.
        var byIp = new Dictionary<string, AttributedHop>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, result) in results)
        {
            foreach (var hop in result.Hops)
            {
                if (!hop.Responded || string.IsNullOrEmpty(hop.Address)) continue;
                if (byIp.ContainsKey(hop.Address)) continue;
                // Resolve ASN; ResolveAsync returns null for private/CGNAT/unparseable.
                var asn = await _asnResolution.ResolveAsync(hop.Address, ct);
                byIp[hop.Address] = new AttributedHop(
                    hop.HopNumber,
                    hop.Address,
                    hop.Hostname,
                    result.ModeUsed,
                    asn);
            }
        }
        _mergedHops = byIp.Values.OrderBy(h => h.HopNumber).ToList();

        // Drop hops that belong to *our* gateway from the candidate pool before
        // any access-hop classification. Without this our 192.168.x.1 gateway
        // shows up as the access ISP's first-mile device when the carrier
        // doesn't respond to early TTLs (or when the upstream is CGNAT and
        // the first responsive hops are all private). Carrier-side CGNAT
        // hops (also private, Asn == null) are still eligible - they ARE
        // first-mile access infra.
        var candidateHops = _mergedHops
            .Where(h => !_gatewayIps.Contains(h.Address))
            .ToList();

        // Identify the access ISP ASN: the first non-null ASN seen at the lowest hop
        // numbers across the traces. Hops in private/CGNAT space (Asn == null) are
        // skipped over; they don't have a public ASN.
        var firstPublicHop = candidateHops.FirstOrDefault(h => h.Asn != null);
        var accessAsn = firstPublicHop?.Asn?.Asn;

        // Collect ALL hops in the access ASN from the merged pool. Filter-based,
        // not sequential - the merged pool interleaves hops from different traces
        // so a sequential walk breaks at the first non-access hop and misses
        // access-ASN hops that only appear in certain traces (e.g. a second
        // border router used for specific transit peers).
        if (accessAsn.HasValue)
        {
            _accessHopsResolved = candidateHops
                .Where(h => h.Asn?.Asn == accessAsn.Value)
                .ToList();
        }
        else
        {
            _accessHopsResolved = candidateHops
                .TakeWhile(h => h.Asn == null)
                .ToList();
        }

        // Walk each individual trace to find border hops: an access-ASN hop
        // whose next responding hop is in a different ASN. Different traces
        // may exit through different border routers depending on the transit
        // peer, so we union across all traces.
        var borderIps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (accessAsn.HasValue)
        {
            foreach (var (_, result) in results)
            {
                var hops = result.Hops
                    .Where(h => h.Responded && !string.IsNullOrEmpty(h.Address)
                                && !_gatewayIps.Contains(h.Address))
                    .OrderBy(h => h.HopNumber)
                    .ToList();
                for (int i = 0; i < hops.Count - 1; i++)
                {
                    var ip = hops[i].Address!;
                    if (!byIp.TryGetValue(ip, out var attributed) || attributed.Asn?.Asn != accessAsn.Value)
                        continue;
                    var nextIp = hops[i + 1].Address!;
                    if (!byIp.TryGetValue(nextIp, out var nextAttr))
                        continue;
                    if (nextAttr.Asn != null && nextAttr.Asn.Asn != accessAsn.Value)
                        borderIps.Add(ip);
                }
            }
        }

        var orgName = CleanAsnName(firstPublicHop?.Asn?.Name);
        State.AccessHops = _accessHopsResolved.Select(h => new AccessHopCandidate
        {
            TargetId = $"access-{NormalizeMacForId(h.Address)}",
            Label = "",
            Address = h.Address,
            PtrHostname = h.Hostname,
            AsnNumber = h.Asn?.Asn,
            AsnName = h.Asn?.Name,
            Role = borderIps.Contains(h.Address)
                ? UpstreamRole.Border
                : InferAccessRole(h, State.AccessTechnology, State.WanNeighborOuiVendor),
            HopNumber = h.HopNumber,
            RespondedTo = h.RespondedTo,
            Enabled = true
        }).ToList();

        // Generate "<Org> <PTR-derived>" labels, same format as transit targets.
        var accessIdx = 0;
        foreach (var hop in State.AccessHops)
        {
            var ptrLabel = FormatTransitHopLabel(hop.PtrHostname, hop.Address);
            if (ptrLabel != null)
                hop.Label = $"{orgName} {ptrLabel}";
            else
                hop.Label = $"{orgName} {++accessIdx}";
        }

        // Inject the L2 neighbor (from ip neigh) as the first access hop if it
        // wasn't already found by traceroute. On GPON the OLT is typically
        // L2-transparent and doesn't appear as a traceroute hop, but it may
        // still respond to ICMP. The reachability check (step 5) will disable
        // it if it doesn't respond. Private LAN-side CPE addresses never qualify:
        // a bridged ISP modem's RFC1918 IP is not ISP infrastructure.
        if (!string.IsNullOrEmpty(State.WanNeighborIp)
            && IsInjectableAccessHopAddress(State.WanNeighborIp)
            && !State.AccessHops.Any(h => h.Address == State.WanNeighborIp))
        {
            var l2Asn = await _asnResolution.ResolveAsync(State.WanNeighborIp, ct);
            var l2Hop = new AccessHopCandidate
            {
                TargetId = $"access-l2-{NormalizeMacForId(State.WanNeighborIp)}",
                Label = $"{orgName} {LabelL2Role(State.WanNeighborOuiVendor, State.AccessTechnology)}",
                Address = State.WanNeighborIp,
                AsnNumber = l2Asn?.Asn,
                AsnName = l2Asn?.Name,
                Role = InferL2NeighborRole(State.AccessTechnology, State.WanNeighborOuiVendor),
                HopNumber = 0,
                RespondedTo = ProbeMode.Icmp,
                Method = DiscoveryMethod.L2Neighbor,
                Enabled = true
            };
            State.AccessHops.Insert(0, l2Hop);
        }

        State.CurrentActivity = State.AccessHops.Count > 0
            ? $"Identified {State.AccessHops.Count} access ISP hop(s){(accessAsn.HasValue ? $" on AS{accessAsn}" : "")}."
            : "No access-ISP hops responded. Discovery will continue but the access cloud will have no probed targets.";
    }

    private async Task<(string Label, TracerouteResult Result)> TraceOneAsync(TraceEndpoint endpoint, ProbeMode mode, CancellationToken ct)
    {
        var target = new ProbeTarget(endpoint.Address, mode);
        try
        {
            var result = await _localProbe.TracerouteAsync(target, maxHops: 30,
                perHopTimeout: TimeSpan.FromSeconds(1),
                totalDeadline: TimeSpan.FromSeconds(10),
                ct: ct);
            return (endpoint.Label, result);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Traceroute failed for {Label} ({Address}) mode {Mode}", endpoint.Label, endpoint.Address, mode);
            return (endpoint.Label, new TracerouteResult
            {
                Target = target,
                Vantage = _localProbe.Vantage,
                ModeUsed = mode,
                Hops = Array.Empty<TraceHop>(),
                Reached = false,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private async Task TraceTransitAsnsAsync(CancellationToken ct)
    {
        State.Step = TracerStep.TracingTransitAsns;
        State.CurrentActivity = "Attributing transit ASNs and selecting target hops...";

        // Access hops were already classified; remaining merged hops are the candidate
        // transit pool. Bucket by ASN, dropping the access ASN itself.
        var accessAsnNumbers = new HashSet<int>(_accessHopsResolved
            .Where(h => h.Asn != null)
            .Select(h => h.Asn!.Asn));

        // Also drop any ASN that's a CDN destination - the CDN's own edge routers
        // respond to traceroute from inside the CDN's ASN, so without this filter
        // major destination ASNs would each show up as a "transit" entry. They
        // belong on the path-proxy / path-end target list below, not as transit-
        // router candidates. TransitProbe endpoints (like Lumen 4.2.2.1) are
        // skipped here on purpose - the whole point of probing them is to
        // surface their ASN as transit.
        //
        // We also collect the destination org *names* so that sibling ASNs of
        // the same org get excluded (e.g. probing Microsoft 13.107.42.14 lives
        // in AS8068 but the trace traverses Microsoft's AS8075 backbone too -
        // both are Microsoft, neither belongs in the transit list).
        var destinationAsns = new HashSet<int>();
        var destinationOrgs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in CdnRotation)
        {
            if (endpoint.IsTransitProbe) continue;
            var destAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (destAsn == null) continue;
            destinationAsns.Add(destAsn.Asn);
            if (!string.IsNullOrWhiteSpace(destAsn.Name)) destinationOrgs.Add(destAsn.Name.Trim());
        }

        // Also exclude the transit-probe endpoints themselves from the hop pool.
        // Their job is to force the path through a specific ASN so real transit
        // routers surface - the endpoint IP itself is far away and not useful
        // as a monitoring target. Exception: EndpointIsTransitHop means the
        // endpoint itself is the transit router (small networks with one hop),
        // but only if its ASN is near the access network (within 2 ASN
        // transitions: access → transit, or access → upstream → transit).
        var transitProbeAddresses = new HashSet<string>(
            CdnRotation.Where(e => e.IsTransitProbe && !e.EndpointIsTransitHop).Select(e => e.Address),
            StringComparer.OrdinalIgnoreCase);

        // For EndpointIsTransitHop probes, build the set of ASNs that are
        // within 2 ASN transitions of the access network. If the endpoint's
        // ASN isn't in this set, it's not really our ISP's transit.
        var endpointTransitHopAddresses = new HashSet<string>(
            CdnRotation.Where(e => e.EndpointIsTransitHop).Select(e => e.Address),
            StringComparer.OrdinalIgnoreCase);
        var nearTransitAsns = new HashSet<int>();
        if (endpointTransitHopAddresses.Count > 0)
        {
            var seen = new HashSet<int>();
            foreach (var h in _mergedHops)
            {
                if (h.Asn == null || accessAsnNumbers.Contains(h.Asn.Asn)) continue;
                if (seen.Add(h.Asn.Asn))
                {
                    nearTransitAsns.Add(h.Asn.Asn);
                    if (seen.Count >= 2) break;
                }
            }
        }

        var transitGroups = _mergedHops
            .Where(h => h.Asn != null
                        && !accessAsnNumbers.Contains(h.Asn.Asn)
                        && !destinationAsns.Contains(h.Asn.Asn)
                        && !(h.Asn.Name != null && destinationOrgs.Contains(h.Asn.Name.Trim()))
                        && !transitProbeAddresses.Contains(h.Address)
                        && (!endpointTransitHopAddresses.Contains(h.Address)
                            || nearTransitAsns.Contains(h.Asn.Asn)))
            .GroupBy(h => h.Asn!.Asn)
            .ToList();

        var candidates = new List<TransitAsnCandidate>();
        foreach (var group in transitGroups)
        {
            // Per-ASN selection: the parallel ICMP+UDP sweep already captured every
            // hop that responded, so the chosen target is just the lowest-hop entry
            // in this ASN. TCP/443 probing was considered as a fallback for ASNs with
            // no responders but rejected: (1) we have 10k+ users and SYN-probing
            // transit routers looks like scanning to NOCs; (2) transit routers don't
            // serve 443 so a RST doesn't reflect anything real; (3) ACLs drop most
            // of them silently anyway. The path-proxy block below covers unenumerated
            // transit ASNs cleanly by monitoring the CDN destination instead.
            var asn = group.First().Asn!;
            var hopsInOrder = group.OrderBy(h => h.HopNumber).Take(3).ToList();

            foreach (var hop in hopsInOrder)
            {
                candidates.Add(new TransitAsnCandidate
                {
                    AsnNumber = asn.Asn,
                    AsnName = CleanAsnName(asn.Name),
                    Method = DiscoveryMethod.DirectRouter,
                    TargetId = $"transit-as{asn.Asn}-{NormalizeMacForId(hop.Address)}",
                    HopAddress = hop.Address,
                    HopHostname = hop.Hostname,
                    RespondedTo = hop.RespondedTo,
                    Enabled = hop == hopsInOrder.First()
                });
            }
        }

        // PTR-resolve candidate IPs that don't already have a hostname (e.g. from
        // Windows managed traceroute or traces where the native binary didn't resolve).
        // Only a handful of candidates, so the cost is negligible.
        await ResolveHostnamesAsync(candidates, ct);

        // Generate labels from PTR hostnames (strip TLD, filter IP-derived junk).
        // Generate "<Org> <PTR-derived>" labels, or "<Org> 1/2/3" when no PTR.
        var asnIndex = new Dictionary<int, int>();
        foreach (var c in candidates)
        {
            if (c.Method == DiscoveryMethod.PathProxy) continue;
            var ptrLabel = FormatTransitHopLabel(c.HopHostname, c.HopAddress);
            if (ptrLabel != null)
            {
                c.Label = $"{c.AsnName} {ptrLabel}";
            }
            else
            {
                asnIndex.TryGetValue(c.AsnNumber, out var idx);
                idx++;
                asnIndex[c.AsnNumber] = idx;
                c.Label = $"{c.AsnName} {idx}";
            }
        }

        // Path-proxy: for every CDN destination whose ASN appeared anywhere in the
        // trace - not just traces that reached the literal endpoint - add the
        // endpoint as a path-end monitoring target.
        var pathProxyAsnsSeen = new HashSet<int>(candidates.Select(c => c.AsnNumber));
        var asnsInTrace = new HashSet<int>(_mergedHops
            .Where(h => h.Asn != null)
            .Select(h => h.Asn!.Asn));
        foreach (var endpoint in CdnRotation)
        {
            if (endpoint.IsTransitProbe) continue;
            var destAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (destAsn == null) continue;
            if (accessAsnNumbers.Contains(destAsn.Asn)) continue;
            var trace = State.Traces.FirstOrDefault(t =>
                string.Equals(t.CdnEndpoint, endpoint.Address, StringComparison.OrdinalIgnoreCase));
            bool reachedOrTraversed = (trace?.Reached ?? false) || asnsInTrace.Contains(destAsn.Asn);
            if (!reachedOrTraversed) continue;
            if (!pathProxyAsnsSeen.Add(destAsn.Asn)) continue;

            candidates.Add(new TransitAsnCandidate
            {
                AsnNumber = destAsn.Asn,
                AsnName = CleanAsnName(destAsn.Name),
                Label = endpoint.Label,
                Method = DiscoveryMethod.PathProxy,
                TargetId = $"path-{endpoint.Label.ToLowerInvariant()}-as{destAsn.Asn}",
                HopAddress = endpoint.Address,
                PathProxyTarget = endpoint.Address,
                RespondedTo = ProbeMode.Icmp,
                Enabled = true
            });
        }

        // Reconcile ALL candidates (transit + path-end) and access hops against
        // existing DB targets. Enabled → pre-check; disabled → uncheck.
        // Absorb descriptive names over numbered fallbacks.
        await using var reconcileDb = await _dbFactory.CreateDbContextAsync(ct);
        var allExisting = await reconcileDb.MonitoringTargets
            .AsNoTracking()
            .ToListAsync(ct);
        var existingByAddress = new Dictionary<string, MonitoringTarget>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allExisting.Where(t => !string.IsNullOrEmpty(t.Address)))
            existingByAddress.TryAdd(t.Address, t);
        foreach (var c in candidates)
        {
            var addr = c.HopAddress ?? c.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            if (existingByAddress.TryGetValue(addr, out var existing))
            {
                c.Enabled = existing.Enabled;
                if (!string.IsNullOrEmpty(existing.Name))
                    c.Label = existing.Name;
            }
        }
        foreach (var hop in State.AccessHops)
        {
            if (existingByAddress.TryGetValue(hop.Address, out var existing))
            {
                hop.Enabled = existing.Enabled;
                if (!string.IsNullOrEmpty(existing.Name))
                    hop.Label = existing.Name;
            }
        }

        State.TransitAsns = candidates;

        var transitCount = candidates.Count(c => c.Method == DiscoveryMethod.DirectRouter);
        var proxyCount = candidates.Count(c => c.Method == DiscoveryMethod.PathProxy);
        State.CurrentActivity = candidates.Count > 0
            ? $"Discovered {transitCount} transit ASN(s) and {proxyCount} path-end target(s)."
            : "No transit ASNs or path-end targets identified.";
    }

    private async Task VerifyReachabilityAsync(CancellationToken ct)
    {
        State.Step = TracerStep.VerifyingReachability;

        var allTargets = new List<(string Address, ProbeMode Mode, Action<double?> ApplyRtt, Action MarkUnreachable)>();
        foreach (var hop in State.AccessHops)
            allTargets.Add((hop.Address, hop.RespondedTo, rtt => hop.VerifiedRttMs = rtt, () => { hop.Enabled = false; hop.Unreachable = true; }));
        foreach (var transit in State.TransitAsns.Where(t => t.HopAddress != null && t.Method == DiscoveryMethod.DirectRouter))
            allTargets.Add((transit.HopAddress!, transit.RespondedTo ?? ProbeMode.Icmp, rtt => transit.VerifiedRttMs = rtt, () => { transit.Enabled = false; transit.Unreachable = true; }));

        if (allTargets.Count == 0) return;

        State.CurrentActivity = $"Pinging {allTargets.Count} candidate(s) to verify reachability...";

        var tasks = allTargets.Select(async t =>
        {
            var result = await _localProbe.PingAsync(
                new ProbeTarget(t.Address, t.Mode),
                count: 2,
                perPingTimeout: TimeSpan.FromSeconds(2),
                ct: ct);
            return (t, result);
        });

        var results = await Task.WhenAll(tasks);
        var unreachable = 0;
        foreach (var (t, result) in results)
        {
            if (result.Success)
            {
                t.ApplyRtt(result.RttAvgMs);
            }
            else
            {
                t.MarkUnreachable();
                unreachable++;
                _logger.LogDebug("Ping check failed for {Address} - marked unreachable", t.Address);
            }
        }

        State.CurrentActivity = unreachable > 0
            ? $"Reachability check complete: {unreachable} of {allTargets.Count} target(s) did not respond and were excluded."
            : $"All {allTargets.Count} target(s) responded to ping.";
    }


    /// <summary>
    /// True when the hostname looks like an automated IP-encoded reverse DNS entry
    /// (the kind ISPs generate by default for unrouted IPs) rather than a
    /// human-labelled router name. Detected by counting how many of the IP's
    /// octets appear as standalone labels or embedded in the first few labels.
    /// </summary>
    private static bool IsIpDerivedHostname(string[] hostnameParts, string ipAddress)
    {
        if (string.IsNullOrEmpty(ipAddress)) return false;
        var ipOctets = ipAddress.Split('.');
        if (ipOctets.Length != 4) return false;

        int octetMatches = 0;
        foreach (var part in hostnameParts)
        {
            foreach (var octet in ipOctets)
            {
                if (string.Equals(part, octet, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "h" + octet, StringComparison.OrdinalIgnoreCase)
                    || part.Contains("-" + octet + "-")
                    || part.StartsWith(octet + "-") || part.EndsWith("-" + octet))
                {
                    octetMatches++;
                    break;
                }
            }
            if (octetMatches >= 2) return true;
        }
        return false;
    }

    /// <summary>
    /// Best-guess role label for an access hop using access tech + L2 neighbor OUI +
    /// hop position. Hop 1 behind a transparent L2 device (GPON OLT, etc.) is a BNG;
    /// hop 1 on DOCSIS is typically the CMTS; without any context it's a generic
    /// AccessHop. Spec 5.5 documents this priority.
    /// </summary>
    private static UpstreamRole InferAccessRole(AttributedHop hop, AccessTechnology tech, string? ouiVendor)
    {
        var vendor = ouiVendor?.ToLowerInvariant() ?? string.Empty;
        // Known OLT/PON vendors. Adtran for tier-2/3 US telcos, Ubiquiti for UISP-Fiber
        // (UF-OLT line), DZS/Dasan for Tier-3 fiber overbuilds, plus the global majors.
        var isOltVendor = vendor.Contains("calix") || vendor.Contains("nokia") || vendor.Contains("huawei")
                          || vendor.Contains("zte") || vendor.Contains("alcatel") || vendor.Contains("adtran")
                          || vendor.Contains("ubiquiti") || vendor.Contains("dzs") || vendor.Contains("dasan");
        var isCmtsVendor = vendor.Contains("arris") || vendor.Contains("commscope") || vendor.Contains("casa")
                           || vendor.Contains("cadant") || vendor.Contains("ubr");

        if ((tech == AccessTechnology.Gpon || tech == AccessTechnology.XgsPon) && isOltVendor && hop.HopNumber == 1)
            return UpstreamRole.Bng;
        if (tech == AccessTechnology.Docsis && (isCmtsVendor || hop.HopNumber == 1))
            return UpstreamRole.Cmts;
        if (tech == AccessTechnology.PppoE && hop.HopNumber == 1)
            return UpstreamRole.Bng;
        return UpstreamRole.Aggregation;
    }

    private static UpstreamRole InferL2NeighborRole(AccessTechnology tech, string? ouiVendor)
    {
        var vendor = ouiVendor?.ToLowerInvariant() ?? string.Empty;
        var isOltVendor = vendor.Contains("calix") || vendor.Contains("nokia") || vendor.Contains("huawei")
                          || vendor.Contains("zte") || vendor.Contains("alcatel") || vendor.Contains("adtran")
                          || vendor.Contains("ubiquiti") || vendor.Contains("dzs") || vendor.Contains("dasan");
        var isCmtsVendor = vendor.Contains("arris") || vendor.Contains("commscope") || vendor.Contains("casa")
                           || vendor.Contains("cadant") || vendor.Contains("ubr");

        if ((tech == AccessTechnology.Gpon || tech == AccessTechnology.XgsPon) && isOltVendor)
            return UpstreamRole.Olt;
        if (tech == AccessTechnology.Docsis && isCmtsVendor)
            return UpstreamRole.Cmts;
        if (tech == AccessTechnology.PppoE)
            return UpstreamRole.Bng;
        return UpstreamRole.AccessGateway;
    }

    private static string LabelL2Role(string? ouiVendor, AccessTechnology tech)
    {
        var vendor = FirstWord(ouiVendor);
        var role = tech switch
        {
            AccessTechnology.Gpon or AccessTechnology.XgsPon => "olt",
            AccessTechnology.Docsis => "cmts",
            AccessTechnology.PppoE => "bng",
            _ => "access"
        };
        var techSuffix = (role == "access" && tech != AccessTechnology.Unknown)
            ? $"-{tech.ToString().ToLowerInvariant()}"
            : "";
        return vendor != null ? $"{vendor}-{role}{techSuffix}" : $"{role}{techSuffix}";
    }

    private static string? FirstWord(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var word = value.Split(' ', ',', '/', '(')[0].Trim();
        return string.IsNullOrEmpty(word) ? null : word.ToLowerInvariant();
    }

    public void RecomputeL2NeighborLabel()
    {
        var hop = State.AccessHops.FirstOrDefault(h => h.Method == DiscoveryMethod.L2Neighbor);
        if (hop == null) return;
        hop.Role = InferL2NeighborRole(State.AccessTechnology, State.WanNeighborOuiVendor);
        var org = CleanAsnName(hop.AsnName ?? State.AccessHops.FirstOrDefault(h => h.AsnName != null)?.AsnName);
        hop.Label = $"{org} {LabelL2Role(State.WanNeighborOuiVendor, State.AccessTechnology)}";
    }

    private static string NormalizeMacForId(string s) => s.Replace(".", "-").Replace(":", "-");

    // ---- Commit ----

    /// <summary>
    /// After the user reviews and edits labels, commit the proposed targets into
    /// the MonitoringTargets table. Becomes the live source the latency tier probes.
    /// </summary>
    public async Task CommitResultsAsync(CancellationToken ct = default)
    {
        if (State.Step != TracerStep.ReviewingResults) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Scope all writes to the WAN this discovery ran against. Multi-WAN setups
        // get one row in MonitoringTargets per (target, wan) and one row in
        // WanDiscoveryContexts per WAN.
        var wanInterface = State.WanInterface ?? "wan";

        foreach (var hop in State.AccessHops.Where(h => h.Enabled))
        {
            await UpsertTargetAsync(db, hop, wanInterface, ct);
        }
        foreach (var hop in State.AccessHops.Where(h => !h.Enabled))
        {
            var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == hop.Address, ct);
            if (existing != null)
            {
                existing.Enabled = false;
                existing.Name = hop.Label;
                if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = CleanAsnName(hop.AsnName);
            }
        }
        foreach (var transit in State.TransitAsns.Where(t => t.Enabled))
        {
            await UpsertTransitTargetAsync(db, transit, wanInterface, ct);
        }
        foreach (var transit in State.TransitAsns.Where(t => !t.Enabled))
        {
            var addr = transit.HopAddress ?? transit.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == addr, ct);
            if (existing != null)
            {
                existing.Enabled = false;
                existing.Name = transit.Label ?? transit.AsnName;
                if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
            }
        }

        // Per-WAN tracer state. WanDiscoveryContexts is the new source of truth;
        // MonitoringSettings still gets the timestamp + review flag cleared because
        // legacy callers + UI still read it for single-WAN setups (transparent
        // upgrade path).
        var ctxRow = await db.WanDiscoveryContexts.FirstOrDefaultAsync(c => c.WanInterface == wanInterface, ct);
        if (ctxRow == null)
        {
            ctxRow = new WanDiscoveryContext { WanInterface = wanInterface };
            db.WanDiscoveryContexts.Add(ctxRow);
        }
        ctxRow.L2NeighborMac = State.WanNeighborMac;
        ctxRow.L2NeighborIp = State.WanNeighborIp;
        ctxRow.L2NeighborOui = State.WanNeighborOuiVendor;
        ctxRow.AccessTechnology = State.AccessTechnology;
        ctxRow.LastDiscoveryAt = DateTime.UtcNow;
        ctxRow.NeedsReview = false;
        ctxRow.UpdatedAt = DateTime.UtcNow;

        var settings = await db.MonitoringSettings.FirstOrDefaultAsync(ct);
        if (settings != null)
        {
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpstreamDiscoveryNeedsReview = false;
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(ct);

        // Persist same-path hop ordering so ISP Health can confirm a farther transit
        // cluster routes through a nearer one before assimilating its jitter.
        await PersistHopOrderAsync(db, wanInterface, ct);

        // Drop the ISP Health cache so the "re-run discovery" banner clears on the next tab
        // view without a manual refresh - the freshly committed ancestry is now in the DB.
        _ispHealth.Invalidate();

        State.Step = TracerStep.Done;
        State.CurrentActivity = "Targets committed. The agent will start probing on the next latency-tier cycle.";
    }

    /// <summary>
    /// Persists traceroute hop order to UpstreamDiscoveries so ISP Health can confirm one
    /// monitored target routes through another (same-path proof) before its jitter absolves
    /// the other. Hop numbers must be comparable across ASNs (ISP hop vs transit hop), so we
    /// record TTLs from a SINGLE global canonical trace per WAN - the one discovery trace that
    /// covered the most monitored hops, i.e. the main path out to the internet. Hops not on
    /// that trace (divergent side paths) get no row, so the gate conservatively declines to
    /// order them - exactly the behavior we want for divergent routers.
    /// </summary>
    private async Task PersistHopOrderAsync(NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        if (_lastTraces.Count == 0)
        {
            _logger.LogDebug("Tracer: no per-trace hop data to persist (rehydrated state); leaving UpstreamDiscoveries as-is");
            return;
        }

        // Access + transit hops are the graded path; destinations (anycast DNS, CDN probes)
        // are persisted too so ISP Health can use a destination's clean end-to-end jitter to
        // absolve an ICMP-deprioritized hop it provably routes through.
        var targets = await db.MonitoringTargets
            .Where(t => t.WanInterface == wanInterface && t.Enabled && t.AsnNumber != null
                && (t.TargetType == MonitoringTargetType.AccessIsp
                    || t.TargetType == MonitoringTargetType.Transit
                    || t.TargetType == MonitoringTargetType.InternetService))
            .ToListAsync(ct);

        // Refresh: drop prior rows for this WAN, then rebuild from this sweep.
        var prior = await db.UpstreamDiscoveries.Where(d => d.WanInterface == wanInterface).ToListAsync(ct);
        if (prior.Count > 0) db.UpstreamDiscoveries.RemoveRange(prior);
        if (targets.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var monitoredAddrs = targets.Select(t => t.Address).Where(a => !string.IsNullOrEmpty(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Ancestor sets from ALL traces: for each monitored hop, the monitored hops that
        // appear before it on any trace it was seen on (its proven upstream). Every trace
        // contributes (including those toward transit targets), so coverage is complete and
        // divergence-correct - a hop is only an ancestor of another when they truly share a
        // path with it upstream. Also track the lowest TTL seen, for a representative HopNumber.
        var ancestorsByIp = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var minTtlByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var tr in _lastTraces)
        {
            var seenMonitored = new List<string>();
            foreach (var h in tr.Hops.Where(h => h.Responded && !string.IsNullOrEmpty(h.Address)).OrderBy(h => h.HopNumber))
            {
                if (!monitoredAddrs.Contains(h.Address!)) continue;
                if (!ancestorsByIp.TryGetValue(h.Address!, out var anc))
                    ancestorsByIp[h.Address!] = anc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                anc.UnionWith(seenMonitored);
                seenMonitored.Add(h.Address!);
                if (!minTtlByIp.TryGetValue(h.Address!, out var ttl) || h.HopNumber < ttl)
                    minTtlByIp[h.Address!] = h.HopNumber;
            }
        }

        var now = DateTime.UtcNow;
        var written = 0;
        foreach (var t in targets)
        {
            var ancestors = ancestorsByIp.TryGetValue(t.Address, out var anc)
                ? anc.OrderBy(a => a).ToList()
                : new List<string>();
            db.UpstreamDiscoveries.Add(new UpstreamDiscovery
            {
                MonitoringTargetId = t.Id,
                AsnNumber = t.AsnNumber!.Value,
                AsnName = t.AsnName,
                HopIp = t.Address,
                HopNumber = minTtlByIp.TryGetValue(t.Address, out var ttl) ? ttl : 0,
                // Non-null (even if empty) marks that ancestor data exists, so ISP Health can
                // tell "no discovery yet" (open gate) from "on-path but no ancestors" (a first hop).
                AncestorHopIps = string.Join(" ", ancestors),
                Role = t.TargetType switch
                {
                    MonitoringTargetType.AccessIsp => UpstreamRole.AccessHop,
                    MonitoringTargetType.Transit => UpstreamRole.Transit,
                    _ => UpstreamRole.PathProxy
                },
                WanInterface = wanInterface,
                LastValidated = now,
                LastTracerouteAt = now,
                IsActive = true
            });
            written++;
            _logger.LogDebug("Tracer: AS{Asn} {Ip} ({Name}) ancestors=[{Ancestors}]",
                t.AsnNumber, t.Address, t.PtrHostname ?? t.Name, string.Join(", ", ancestors));
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Tracer: persisted {Count} upstream hop-ancestor rows for {Wan} from {Traces} traces",
            written, wanInterface, _lastTraces.Count);
    }

    private static async Task UpsertTargetAsync(NetworkOptimizerDbContext db, AccessHopCandidate hop, string wanInterface, CancellationToken ct)
    {
        var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == hop.TargetId, ct);
        existing ??= await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == hop.Address, ct);
        if (existing == null)
        {
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = hop.TargetId,
                Name = hop.Label,
                Address = hop.Address,
                ProbeMode = hop.RespondedTo,
                DiscoveredProbeMode = hop.RespondedTo,
                TargetType = MonitoringTargetType.AccessIsp,
                AsnNumber = hop.AsnNumber,
                AsnName = CleanAsnName(hop.AsnName),
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                Enabled = true,
                AutoDiscovered = true,
                DiscoveryMethod = hop.Method,
                WanInterface = wanInterface,
                PtrHostname = hop.PtrHostname,
                AutoLabel = hop.Role.ToString(),
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            // Re-validation: keep target_id stable, update mode if it changed (history
            // preservation per locked decision 6b). Backfill ASN fields whenever a
            // current run resolves them - rows committed before the GeoLite2 fix
            // landed have nulls and never refreshed without this.
            existing.Enabled = true;
            existing.Address = hop.Address;
            existing.ProbeMode = hop.RespondedTo;
            existing.WanInterface = wanInterface;
            existing.Name = hop.Label;
            if (hop.AsnNumber.HasValue) existing.AsnNumber = hop.AsnNumber;
            if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = CleanAsnName(hop.AsnName);
            if (!string.IsNullOrEmpty(hop.PtrHostname)) existing.PtrHostname = hop.PtrHostname;
            existing.LastVerified = DateTime.UtcNow;
        }
    }

    private static async Task UpsertTransitTargetAsync(NetworkOptimizerDbContext db, TransitAsnCandidate transit, string wanInterface, CancellationToken ct)
    {
        if (transit.Method == DiscoveryMethod.Unresolved || string.IsNullOrEmpty(transit.TargetId)) return;

        var targetType = transit.Method == DiscoveryMethod.PathProxy
            ? MonitoringTargetType.InternetService
            : MonitoringTargetType.Transit;

        var address = transit.HopAddress ?? transit.PathProxyTarget;
        var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == transit.TargetId, ct);
        if (existing == null && !string.IsNullOrEmpty(address))
            existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.Address == address, ct);
        if (existing == null)
        {
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = transit.TargetId,
                Name = transit.Label ?? transit.AsnName,
                Address = transit.HopAddress ?? transit.PathProxyTarget ?? "0.0.0.0",
                ProbeMode = transit.RespondedTo ?? NetworkOptimizer.Core.Enums.ProbeMode.Icmp,
                DiscoveredProbeMode = transit.RespondedTo,
                TargetType = targetType,
                AsnNumber = transit.AsnNumber,
                AsnName = transit.AsnName,
                VantagePoint = "server",
                PollIntervalSeconds = 15,
                PingCount = 5,
                Enabled = true,
                PtrHostname = transit.HopHostname,
                AutoDiscovered = true,
                DiscoveryMethod = transit.Method,
                WanInterface = wanInterface,
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            existing.Enabled = true;
            existing.Name = transit.Label ?? transit.AsnName;
            existing.Address = transit.HopAddress ?? transit.PathProxyTarget ?? existing.Address;
            existing.ProbeMode = transit.RespondedTo ?? existing.ProbeMode;
            if (!string.IsNullOrEmpty(transit.HopHostname)) existing.PtrHostname = transit.HopHostname;
            existing.DiscoveryMethod = transit.Method;
            existing.WanInterface = wanInterface;
            // Refresh ASN bookkeeping in case the resolver picked up a name now
            // (legacy rows from before the GeoLite2 path landed had nulls).
            if (transit.AsnNumber > 0) existing.AsnNumber = transit.AsnNumber;
            if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
            existing.LastVerified = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// PTR-resolve any transit candidates that don't already have a hostname from
    /// the traceroute output (e.g. Windows managed traceroute, or hops that only
    /// appeared in -n traces). Mutates HopHostname in place.
    /// </summary>
    private static async Task ResolveHostnamesAsync(List<TransitAsnCandidate> candidates, CancellationToken ct)
    {
        var tasks = candidates
            .Where(c => string.IsNullOrEmpty(c.HopHostname) && !string.IsNullOrEmpty(c.HopAddress))
            .Select(async c =>
            {
                try
                {
                    var entry = await System.Net.Dns.GetHostEntryAsync(c.HopAddress!, ct);
                    if (!string.IsNullOrEmpty(entry.HostName) && entry.HostName != c.HopAddress)
                        c.HopHostname = entry.HostName;
                }
                catch { /* no PTR record — leave null */ }
            });
        await Task.WhenAll(tasks);
    }

    internal static string CleanAsnName(string? name) =>
        NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName(name);

    /// <summary>
    /// Generate a display label from a PTR hostname for transit targets.
    /// Strips the last 2 labels (SLD + TLD, e.g. ".windstream.net") since
    /// the org name is already prepended separately. Returns null if the
    /// hostname is unusable (IP-derived auto-PTR or too short).
    /// </summary>
    internal static string? FormatTransitHopLabel(string? hostname, string? ipAddress)
    {
        if (string.IsNullOrEmpty(hostname)) return null;
        var parts = hostname.Split('.');
        if (IsIpDerivedHostname(parts, ipAddress ?? string.Empty)) return null;
        if (parts.Length <= 2) return null;
        return string.Join('.', parts.Take(parts.Length - 2));
    }

    private bool Fail(string message)
    {
        _logger.LogInformation("Upstream tracer stopped: {Message}", message);
        State.Step = TracerStep.Failed;
        State.FailureMessage = message;
        State.CompletedAt = DateTime.UtcNow;
        return false;
    }
}
