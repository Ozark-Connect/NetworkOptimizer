using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Enums;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Monitoring.Probes;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
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
        SiteConnectionRegistry siteConnections,
        GatewaySshRegistry gatewaySshRegistry,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        LocalProbeExecutor localProbe,
        IServiceScopeFactory scopeFactory,
        IspHealth.IspHealthService ispHealth,
        NetworkOptimizer.Audit.Services.IeeeOuiDatabase ouiDb,
        ILogger<UpstreamTracerService> logger)
    {
        _connectionService = siteConnections.GetDefault();
        _gatewaySsh = gatewaySshRegistry.GetDefault();
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

            // Access technology is a user-set input that materially drives the run -
            // the reachability gate threshold (2/3 vs 3/3) and role/label inference -
            // not just display. The foreground panel hydrates it via RehydrateFromDbAsync
            // on open; the background re-discovery scheduler never opens the panel, so when
            // the in-memory state carries no value, fall back to the persisted one. This
            // keeps the scheduled run 1:1 with a user-initiated run.
            var preservedTech = State.AccessTechnology;
            if (preservedTech == AccessTechnology.Unknown)
                preservedTech = await LoadPersistedAccessTechnologyAsync(ct);
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

    /// <summary>
    /// Reads the most-recently-discovered WAN's saved access technology from the DB.
    /// Fallback for when a run starts (notably the background re-discovery scheduler)
    /// without the UI having hydrated the in-memory state first. Returns Unknown when
    /// nothing is persisted yet.
    /// </summary>
    private async Task<AccessTechnology> LoadPersistedAccessTechnologyAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var ctx = await db.WanDiscoveryContexts
                .OrderByDescending(c => c.LastDiscoveryAt ?? c.UpdatedAt)
                .FirstOrDefaultAsync(ct);
            return ctx?.AccessTechnology ?? AccessTechnology.Unknown;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load persisted access technology; defaulting to Unknown");
            return AccessTechnology.Unknown;
        }
    }

    /// <summary>Resets state back to Idle. Used by the re-discovery scheduler when a sweep matched committed targets.</summary>
    public void ResetToIdle()
    {
        // Preserve the access technology - it's a persisted, user-set input the next run
        // needs as its starting point. Zeroing it forced the following background run to
        // fall back to Unknown and diverge from the foreground path's reachability/role logic.
        State = new UpstreamTracerState
        {
            Step = TracerStep.Idle,
            AccessTechnology = State.AccessTechnology
        };
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

        // ISP/transit tracing follows the CONFIGURED primary WAN (not whichever WAN
        // happens to be first), matching the rest of the monitoring umbrella. Resolve
        // its networkgroup so the wan-object loop can pick the matching connection.
        string? primaryNg = null;
        try
        {
            var networks = await _connectionService.GetNetworksAsync(ct);
            primaryNg = UniFiConnectionService.ResolvePrimaryWanNetwork(networks, _logger)?.WanNetworkgroup;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UpstreamTracer: failed to resolve primary WAN networkgroup; falling back to first WAN");
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

                // ifname → networkgroup so each wan object can be matched against the
                // configured primary; falls back to the wan-key convention.
                var ifnameToNg = GatewayWanHelper.BuildNetworkGroupByIfname(
                    device.TryGetProperty("ethernet_overrides", out var eo) ? eo : default);

                (string Key, string Uplink, string? Ip)? firstWan = null;
                foreach (var wan in GatewayWanHelper.EnumerateWanInterfaces(device))
                {
                    var uplinkIfname = wan.UplinkIfName;
                    if (string.IsNullOrEmpty(uplinkIfname)) continue;

                    var ip = wan.Ip;

                    // Derive the WAN key from port_table network_name when available
                    // (e.g. "wan", "wan2") to match convention used by prior code.
                    string interfaceKey;
                    if (wan.PortIdx.HasValue &&
                        portIdxToNetworkName.TryGetValue(wan.PortIdx.Value, out var networkName))
                    {
                        interfaceKey = networkName;
                    }
                    else
                    {
                        interfaceKey = GatewayWanHelper.WanInterfaceKeyFromKey(wan.Key);
                    }

                    firstWan ??= (interfaceKey, uplinkIfname, ip);

                    // Resolve this wan's networkgroup and prefer the configured primary.
                    string? ng = null;
                    if (!string.IsNullOrEmpty(wan.IfName))
                        ifnameToNg.TryGetValue(wan.IfName, out ng);
                    ng ??= GatewayWanHelper.WanNetworkGroupFromKey(wan.Key);

                    if (primaryNg != null && string.Equals(ng, primaryNg, StringComparison.OrdinalIgnoreCase))
                    {
                        wanInterfaceName = interfaceKey;
                        wanUplinkIfName = uplinkIfname;
                        wanIp = ip;
                        break;
                    }
                }

                // Primary unresolved or not matched: fall back to the first WAN found.
                if (wanInterfaceName == null && firstWan != null)
                {
                    wanInterfaceName = firstWan.Value.Key;
                    wanUplinkIfName = firstWan.Value.Uplink;
                    wanIp = firstWan.Value.Ip;
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

        // Pull the WAN address line once: it yields the owning interface (a fallback
        // candidate) and the WAN IP's prefix, which lets us recognize the real ISP-side
        // gateway as the on-link neighbor rather than a public WAN SLA probe target.
        string? wanCidr = null;
        if (!string.IsNullOrEmpty(State.WanIpAddress))
        {
            var addrCmd = $"ip -o -4 addr show | grep -F ' {State.WanIpAddress}/' | head -1";
            var (addrOk, addrOut) = await _gatewaySsh.RunCommandAsync(addrCmd, TimeSpan.FromSeconds(5), ct);
            if (addrOk && !string.IsNullOrWhiteSpace(addrOut))
            {
                var m = Regex.Match(addrOut, @"^\s*\d+:\s+(?<iface>\S+)\s+inet\s+(?<cidr>\S+)", RegexOptions.Multiline);
                if (m.Success)
                {
                    if (candidates.Count == 0) candidates.Add(m.Groups["iface"].Value);
                    wanCidr = m.Groups["cidr"].Value;
                }
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

            var selected = SelectWanNeighbor(output, wanCidr);
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
    /// a private CPE IP as an ISP hop. Preference order: in the WAN subnet (the real
    /// ISP-side gateway is by definition on-link with our WAN IP) &gt; address class
    /// (public &gt; CGNAT &gt; private) &gt; freshness (REACHABLE/DELAY/PROBE over STALE).
    /// FAILED and INCOMPLETE entries carry no lladdr and never match. IPv6 link-local
    /// entries are skipped, as are UniFi's WAN SLA probe targets (1.1.1.1 / 8.8.8.8):
    /// the gateway keeps neighbor entries for those, but they are public DNS resolvers,
    /// not the first-mile device.
    /// </summary>
    /// <param name="ipNeighOutput">Raw `ip neigh show dev &lt;wan&gt;` output.</param>
    /// <param name="wanCidr">The gateway's WAN address in CIDR form (e.g. "203.0.113.5/24")
    /// used to recognize the on-link ISP gateway. Null/empty falls back to class+freshness.</param>
    public static (string Ip, string Mac)? SelectWanNeighbor(string? ipNeighOutput, string? wanCidr = null)
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

            // UniFi's default WAN SLA probe targets keep a neighbor entry on the WAN
            // interface but are public DNS resolvers, never the L2 next hop.
            if (NetworkUtilities.WanSlaProbeIps.Contains(ipText)) continue;

            var subnetScore = !string.IsNullOrEmpty(wanCidr) && NetworkUtilities.IsIpInSubnet(ip, wanCidr) ? 1 : 0;
            var classScore = NetworkUtilities.ClassifyPublicAddress(ip) switch
            {
                PublicAddressClass.PublicIPv4 => 3,
                PublicAddressClass.Cgnat => 2,
                PublicAddressClass.DoubleNat => 1,
                _ => 0
            };
            var freshScore = m.Groups[3].Value.Contains("STALE", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
            var score = subnetScore * 100 + classScore * 10 + freshScore;
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

    // The detected access ISP ASN from the last TraceAccessIspAsync. Kept as a field so
    // the reachability step can fall back to a curated endpoint even when none of the
    // access hops responded (the access pool can be empty/unreachable yet the ASN known).
    private int? _accessAsn;

    // The 1st/2nd-degree non-access ASNs off each trace (union): the access ISP's
    // direct upstream and that upstream's upstream. A transit-probe ASN (Lumen, AT&T,
    // INDATEL) only counts as *our* ISP's transit when it lands in this window -
    // probing toward a Lumen/AT&T anycast IP otherwise drags that tier-1 onto the path
    // as the destination's own network even when it isn't an upstream at all. Set in
    // TraceTransitAsnsAsync, read again when injecting transit witnesses.
    private readonly HashSet<int> _nearTransitAsns = new();

    // Tier-1 ASNs excluded as transit because they only ever appear directly above
    // another tier-1 on the path (core peering, not our access ISP's transit). Set in
    // TraceTransitAsnsAsync, read again when injecting transit witnesses.
    private readonly HashSet<int> _excludedTier1Asns = new();

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
        // Remember it so the reachability step can reach for a curated fallback endpoint
        // even when the access pool ends up empty or entirely unreachable.
        _accessAsn = accessAsn;

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
        // so it stays eligible as a target - the near-transit ASN gate below
        // decides whether it's actually our ISP's transit.
        var transitProbeAddresses = new HashSet<string>(
            CdnRotation.Where(e => e.IsTransitProbe && !e.EndpointIsTransitHop).Select(e => e.Address),
            StringComparer.OrdinalIgnoreCase);

        // Resolve the ASN of every transit probe (Lumen AS3356, AT&T AS7018,
        // INDATEL AS30517). Tracing toward one of these anycast IPs always enters
        // that ASN near the destination edge - so on its own, a probe ASN's
        // presence on the path proves nothing about whether it's *our* ISP's
        // upstream. We only keep it when it also lands in the near-transit window.
        var transitProbeAsns = new HashSet<int>();
        foreach (var endpoint in CdnRotation)
        {
            if (!endpoint.IsTransitProbe) continue;
            var probeAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (probeAsn != null) transitProbeAsns.Add(probeAsn.Asn);
        }

        // Per-trace ordered address sequences (responding hops only) feed both the
        // near-transit window and the tier-1 adjacency check. We work per trace rather
        // than over the merged pool: the merged pool orders hops by number across
        // heterogeneous traces, so a multi-homed access ISP's other upstreams (or a
        // near probe endpoint) can occupy the global "first two" slots at a lower hop
        // number and crowd out a genuine direct upstream.
        var asnByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _mergedHops)
            if (h.Asn != null) asnByIp.TryAdd(h.Address, h.Asn.Asn);
        var traceSequences = _lastTraces
            .Select(t => (IReadOnlyList<string>)t.Hops
                .Where(hp => hp.Responded && !string.IsNullOrEmpty(hp.Address))
                .OrderBy(hp => hp.HopNumber)
                .Select(hp => hp.Address!)
                .ToList())
            .ToList();

        _nearTransitAsns.Clear();
        _nearTransitAsns.UnionWith(
            ComputeNearTransitAsns(traceSequences, asnByIp, accessAsnNumbers, destinationAsns, WellKnownAsns.Tier1));

        _excludedTier1Asns.Clear();
        _excludedTier1Asns.UnionWith(
            ComputeExcludedTier1Asns(traceSequences, asnByIp, WellKnownAsns.Tier1, accessAsnNumbers));

        var transitGroups = _mergedHops
            .Where(h => h.Asn != null
                        && !accessAsnNumbers.Contains(h.Asn.Asn)
                        && !destinationAsns.Contains(h.Asn.Asn)
                        && !(h.Asn.Name != null && destinationOrgs.Contains(h.Asn.Name.Trim()))
                        && !transitProbeAddresses.Contains(h.Address)
                        && !_excludedTier1Asns.Contains(h.Asn.Asn)
                        && !WellKnownAsns.NonTransitInfrastructure.Contains(h.Asn.Asn)
                        && (!transitProbeAsns.Contains(h.Asn.Asn)
                            || _nearTransitAsns.Contains(h.Asn.Asn)))
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

    // Reachability gate: a candidate must answer enough pings in a short rapid burst (200 ms
    // spacing) to be auto-selected, so flaky, ICMP-deprioritized routers don't get monitored - and
    // so the Level 3 transit witness (4.2.2.2) is injected exactly when no real AS3356 router clears
    // the gate. We always send 3; the required successes depend on the connection's access medium
    // (Item B): air-interface mediums (WISP, cellular) and the unconfigured Unknown case allow one
    // dropped reply (2/3); stable wired/fiber/LEO mediums demand 3/3.
    private const int ReachabilityPingCount = 3;

    /// <summary>
    /// Required successful pings (out of <see cref="ReachabilityPingCount"/>) for a candidate to be
    /// auto-selected, by the connection's access technology. WISP / cellular have inherent
    /// air-interface transient loss, and Unknown is unconfigured, so we don't penalize them for a
    /// single drop; everything else (including LEO, which is stable) demands all three.
    /// </summary>
    private static int RequiredReachabilitySuccesses(AccessTechnology tech) => tech switch
    {
        AccessTechnology.FixedWireless or AccessTechnology.Cellular or AccessTechnology.Unknown => 2,
        _ => 3
    };

    /// <summary>Rapid ping burst used for reachability verification.</summary>
    private Task<PingProbeResult> ProbeReachabilityAsync(string address, ProbeMode mode, CancellationToken ct) =>
        _localProbe.PingAsync(new ProbeTarget(address, mode),
            count: ReachabilityPingCount, perPingTimeout: TimeSpan.FromSeconds(2), ct: ct);

    private async Task VerifyReachabilityAsync(CancellationToken ct)
    {
        State.Step = TracerStep.VerifyingReachability;

        var allTargets = new List<(string Address, ProbeMode Mode, Action<double?> ApplyRtt, Action MarkUnreachable)>();
        foreach (var hop in State.AccessHops)
            allTargets.Add((hop.Address, hop.RespondedTo, rtt => hop.VerifiedRttMs = rtt, () => { hop.Enabled = false; hop.Unreachable = true; }));
        foreach (var transit in State.TransitAsns.Where(t => t.HopAddress != null && t.Method == DiscoveryMethod.DirectRouter))
            allTargets.Add((transit.HopAddress!, transit.RespondedTo ?? ProbeMode.Icmp, rtt => transit.VerifiedRttMs = rtt, () => { transit.Enabled = false; transit.Unreachable = true; }));

        if (allTargets.Count == 0) return;

        // One gate for the whole run: every probe crosses the same access medium, so a WISP's
        // air-link loss hits the transit-router pings just as it hits the access-hop pings.
        var minSuccesses = RequiredReachabilitySuccesses(State.AccessTechnology);
        State.CurrentActivity = $"Pinging {allTargets.Count} candidate(s) to verify reachability ({minSuccesses}/{ReachabilityPingCount} required)...";

        var results = await Task.WhenAll(allTargets.Select(async t =>
            (t, Result: await ProbeReachabilityAsync(t.Address, t.Mode, ct))));
        var unreachable = 0;
        foreach (var (t, result) in results)
        {
            if (result.Received >= minSuccesses)
            {
                t.ApplyRtt(result.RttAvgMs);
            }
            else
            {
                t.MarkUnreachable();
                unreachable++;
                _logger.LogDebug("Ping check {Recv}/{Sent} for {Address} - below {Min} required, marked unreachable",
                    result.Received, result.Sent, t.Address, minSuccesses);
            }
        }

        // Item A: if Level 3 (AS3356) is on the path but no AS3356 router cleared the gate, inject
        // 4.2.2.2 as a transit witness so ISP Health still has Lumen transit data.
        await InjectTransitWitnessesAsync(minSuccesses, ct);

        // If the access ASN is one we have curated endpoints for and none of its first-mile
        // hops cleared the gate, adopt the lowest-RTT reachable curated endpoint as the access target.
        await InjectAccessIspFallbackAsync(minSuccesses, ct);

        State.CurrentActivity = unreachable > 0
            ? $"Reachability check complete: {unreachable} of {allTargets.Count} target(s) did not respond and were excluded."
            : $"All {allTargets.Count} target(s) responded to ping.";
    }

    // Item A: anycast DNS witnesses for transit ASNs whose routers commonly ICMP-deprioritize or
    // hide behind L2-transparent infra. Used only when the ASN is on the path but no router clears
    // the reachability gate. The endpoint is anycast (nearest edge), hence the "transit witness"
    // label. Extend this table to add witnesses for other transit ASNs (e.g. AS7018 AT&T).
    private static readonly (int Asn, string Address, string Name, string Label)[] TransitWitnesses =
    {
        (3356, "4.2.2.2", "Level 3", "Level 3 DNS (transit witness)")
    };

    // Curated access-ISP endpoints for carriers whose first-mile routers commonly ICMP-deprioritize,
    // leaving the access cloud with no probed target. When the detected access ASN is in this map and
    // none of the discovered access hops clear the reachability gate, we resolve + ping these published
    // hosts and adopt the lowest-RTT reachable one as the access target (InjectAccessIspFallbackAsync).
    // Hosts must answer ICMP (the same gate post-traceroute hops face); non-pingable PoPs are omitted.
    // The label follows the standard convention (stripped ASN name + stripped hostname via
    // FormatTransitHopLabel), e.g. "Deutsche Telekom ffm.wsqm".
    internal static readonly IReadOnlyDictionary<int, IReadOnlyList<string>> AccessIspFallbackHosts =
        new Dictionary<int, IReadOnlyList<string>>
        {
            // AS3320 Deutsche Telekom AG - WSQM endpoints (Düsseldorf omitted: not ICMP-pingable).
            [3320] = new[]
            {
                "ffm.wsqm.telekom-dienste.de",   // Frankfurt am Main
                "ham.wsqm.telekom-dienste.de",   // Hamburg
                "mue.wsqm.telekom-dienste.de",   // Munich
                "ber.wsqm.telekom-dienste.de",   // Berlin
            },
            // AS12912 T-Mobile Polska - public speedtest PoPs inside T-Mobile PL's own network.
            [12912] = new[]
            {
                "gda1.t-mobile.pl",   // Gdańsk
                "poz1.t-mobile.pl",   // Poznań
                "waw2.t-mobile.pl",   // Warsaw
                "kra1.t-mobile.pl",   // Kraków
            },
            // AS13036 T-Mobile Czech Republic - public speedtest PoPs inside its own network.
            [13036] = new[]
            {
                "speedtest5.t-mobile.cz",   // Prague
                "speedtest6.t-mobile.cz",   // Brno
            },
            // AS394056 Intrepid Fiber - the access-layer fiber network. T-Mobile Fiber is a retail
            // brand that rides on Intrepid in several US metros (and some markets sell Intrepid
            // Fiber direct). Either way the subscriber's access ASN is 394056 (not T-Mobile's
            // mobile AS21928), so that's the key matched here.
            [394056] = new[]
            {
                "speedtest.sandiego.intrepidfiber.com",     // San Diego
                "speedtest.denver.intrepidfiber.com",       // Denver
                "speedtest.minneapolis.intrepidfiber.com",  // Minneapolis
            },

            // Charter / Spectrum - Ookla speedtest hosts (*.st.charter.com), all verified
            // ICMP-pingable. Spectrum customers span 10 ASNs from the Charter / Time Warner Cable /
            // Bright House / Bresnan mergers, so there is one key per ASN, each listing only the
            // hosts that actually resolve into that ASN - a customer only ever probes the handful
            // in its own detected ASN (16 for AS20115, 1-5 elsewhere), well within the rapid burst.
            [20115] = new[]   // Charter Communications LLC
            {
                "aldlmi-speedtest-ookla-01.st.charter.com",   // Allendale, MI
                "euclwi-speedtest-ookla-01.st.charter.com",   // Eau Claire, WI
                "ftwotx-speedtest-ookla-01.st.charter.com",   // Fort Worth, TX
                "kgpttn-speedtest-ookla-01.st.charter.com",   // Kingsport, TN
                "krnyne-speedtest-ookla-01.st.charter.com",   // Kearney, NE
                "ledsal-speedtest-ookla-01.st.charter.com",   // Leeds, AL
                "mdfdor-speedtest-ookla-01.st.charter.com",   // Medford, OR
                "mtpkca-speedtest-ookla-01.st.charter.com",   // Monterey Park, CA
                "olvemo-speedtest-ookla-01.st.charter.com",   // Olivette, MO
                "oxfrma-speedtest-ookla-01.st.charter.com",   // Oxford, MA
                "ptldor-speedtest-ookla-01.st.charter.com",   // Portland, OR
                "renonv-speedtest-ookla-01.st.charter.com",   // Reno, NV
                "sghlga-speedtest-ookla-01.st.charter.com",   // Sugar Hill, GA
                "slidla-speedtest-ookla-01.st.charter.com",   // Slidell, LA
                "snloca-speedtest-ookla-01.st.charter.com",   // San Luis Obispo, CA
                "stcdmn-speedtest-ookla-01.st.charter.com",   // St Cloud, MN
            },
            [7843] = new[]    // Charter (legacy Time Warner Cable)
            {
                "dnvrco-speedtest-ookla-01.st.charter.com",   // Centennial, CO
            },
            [10796] = new[]   // Charter (legacy Time Warner Cable)
            {
                "clboh-speedtest-ookla-03.st.charter.com",    // Columbus, OH
                "lxtnky-speedtest-ookla-01.st.charter.com",   // Lexington, KY
            },
            [11351] = new[]   // Charter (legacy Time Warner Cable)
            {
                "ptldme-speedtest-ookla-01.st.charter.com",   // Portland, ME
                "syrny-speedtest-ookla-02.st.charter.com",    // Syracuse, NY
            },
            [11426] = new[]   // Charter (legacy Time Warner Cable)
            {
                "radnc-speedtest-ookla-01.st.charter.com",    // Durham, NC
            },
            [11427] = new[]   // Charter (legacy Time Warner Cable)
            {
                "houstx-speedtest-ookla-01.st.charter.com",   // Houston, TX
                "ksczks-speedtest-ookla-01.st.charter.com",   // Kansas City, KS
                "snantx-speedtest-ookla-01.st.charter.com",   // San Antonio, TX
            },
            [12271] = new[]   // Charter (legacy Time Warner Cable)
            {
                "nycny-speedtest-ookla-01.st.charter.com",    // New York, NY
            },
            [20001] = new[]   // Charter (legacy Time Warner Cable)
            {
                "kmlahi-speedtest-ookla-01.st.charter.com",   // Mauna Lani, HI
                "lsanca-speedtest-ookla-02.st.charter.com",   // Los Angeles, CA
                "milnhi-speedtest-ookla-01.st.charter.com",   // Mililani, HI
            },
            [33363] = new[]   // Charter (Bright House Networks)
            {
                "detmi-speedtest-ookla-01.st.charter.com",    // Livonia, MI
                "tampfl-speedtest-ookla-01.st.charter.com",   // Tampa, FL
            },
            [33588] = new[]   // Charter (Bresnan)
            {
                "blngmt-speedtest-ookla-01.st.charter.com",   // Billings, MT
                "chynwy-speedtest-ookla-01.st.charter.com",   // Cheyenne, WY
                "csprwy-speedtest-ookla-01.st.charter.com",   // Casper, WY
                "gdjtco-speedtest-ookla-01.st.charter.com",   // Grand Junction, CO
                "msslmt-speedtest-ookla-01.st.charter.com",   // Missoula, MT
            },

            // Orange S.A. (AS3215, France) - NOT YET ENABLED. These hosts BLOCK ICMP (0/3) but
            // answer TCP:8080, so enabling them needs per-endpoint TCP probe support added to this
            // map + InjectAccessIspFallbackAsync. The probe layer (TcpPingAsync) and the live agent
            // (MonitoringCollectionAgent) already honor ProbeMode.Tcp + Port; only the fallback
            // map/injection are ICMP-only today. When TCP support lands, key AS3215 -> TCP:8080:
            //   montsouris3.d2m.c2d.liveservices.fr   // Paris
            //   lyon3.d2m.c2d.liveservices.fr         // Lyon
            //   lille3.d2m.c2d.liveservices.fr        // Lille
            //   marseille3.d2m.c2d.liveservices.fr    // Marseille
            //   strasbourg3.d2m.c2d.liveservices.fr   // Strasbourg
            //   puteaux3.d2m.c2d.liveservices.fr      // Puteaux
            //   poitiers3.d2m.c2d.liveservices.fr     // Poitiers
        };

    private async Task InjectTransitWitnessesAsync(int minSuccesses, CancellationToken ct)
    {
        foreach (var (asn, address, name, label) in TransitWitnesses)
        {
            // Only when the ASN is genuinely near-transit (the access ISP's upstream
            // or its upstream's upstream). Mere presence on the path isn't enough -
            // tracing the Lumen probe drags AS3356 onto the path even when Lumen is
            // just the destination's own network, not our ISP's transit.
            if (!_nearTransitAsns.Contains(asn)) continue;
            // And not when this tier-1 only ever sits above another tier-1 (core peering).
            if (_excludedTier1Asns.Contains(asn)) continue;
            // Skip if any real router in this ASN already cleared the gate, or the witness exists.
            if (State.TransitAsns.Any(t => t.AsnNumber == asn && t.Enabled && !t.Unreachable)) continue;
            if (State.TransitAsns.Any(t => string.Equals(t.HopAddress, address, StringComparison.OrdinalIgnoreCase))) continue;

            // The witness must itself clear the gate before we enable it.
            var result = await ProbeReachabilityAsync(address, ProbeMode.Icmp, ct);
            var reachable = result.Received >= minSuccesses;
            if (!reachable)
            {
                _logger.LogDebug("Transit witness {Address} (AS{Asn}) only {Recv}/{Sent} - not injecting",
                    address, asn, result.Received, result.Sent);
                continue;
            }

            State.TransitAsns.Add(new TransitAsnCandidate
            {
                AsnNumber = asn,
                AsnName = name,
                Label = label,
                Method = DiscoveryMethod.DirectRouter,
                TargetId = $"transit-witness-as{asn}-{NormalizeMacForId(address)}",
                HopAddress = address,
                RespondedTo = ProbeMode.Icmp,
                VerifiedRttMs = result.RttAvgMs,
                Enabled = true
            });
            _logger.LogInformation("Injected transit witness {Address} (AS{Asn} {Name}) - no reachable {Name} router",
                address, asn, name, name);
        }
    }

    /// <summary>
    /// When the detected access ASN is one we have curated endpoints for and NO discovered hop in
    /// that ASN answered ICMP (the whole access-ISP hop set - the L2 neighbor plus any aggregation
    /// and border routers - came back unreachable), resolve + ICMP-ping the curated hosts and adopt
    /// the single lowest-RTT reachable one as the access target. The carrier's own routers commonly
    /// ICMP-deprioritize, so without this the access cloud would have nothing to probe. Same
    /// reachability gate as the post-traceroute hops; same label convention (stripped ASN name +
    /// stripped hostname) as every other discovered target.
    /// </summary>
    private async Task InjectAccessIspFallbackAsync(int minSuccesses, CancellationToken ct)
    {
        if (_accessAsn is not int asn) return;
        if (!AccessIspFallbackHosts.TryGetValue(asn, out var hosts)) return;

        // Only when discovery produced no reachable access target. A real first-mile router that
        // cleared the gate is always the better monitor than a city-PoP speedtest endpoint.
        if (State.AccessHops.Any(h => h.Enabled && !h.Unreachable)) return;

        var orgName = CleanAsnName(_accessHopsResolved.FirstOrDefault()?.Asn?.Name);
        if (string.IsNullOrEmpty(orgName)) orgName = $"AS{asn}";

        // Resolve + ping each curated host; keep the reachable ones with their measured RTT.
        var probed = new List<AccessFallbackProbe>();
        foreach (var host in hosts)
        {
            var ip = await ResolveIPv4Async(host, ct);
            if (ip == null)
            {
                _logger.LogDebug("Access fallback {Host} (AS{Asn}) did not resolve to an IPv4 address", host, asn);
                continue;
            }
            var result = await ProbeReachabilityAsync(ip, ProbeMode.Icmp, ct);
            if (result.Received >= minSuccesses && result.RttAvgMs is double rtt)
                probed.Add(new AccessFallbackProbe(host, ip, rtt));
            else
                _logger.LogDebug("Access fallback {Host} ({Ip}) only {Recv}/{Sent} - skipping",
                    host, ip, result.Received, result.Sent);
        }

        var winner = SelectLowestRtt(probed);
        if (winner == null)
        {
            _logger.LogDebug("Access fallback for AS{Asn} {Org}: no curated host cleared the gate", asn, orgName);
            return;
        }

        var ptrLabel = FormatTransitHopLabel(winner.Host, winner.Ip);
        State.AccessHops.Add(new AccessHopCandidate
        {
            TargetId = $"access-fallback-as{asn}-{NormalizeMacForId(winner.Host)}",
            Label = ptrLabel != null ? $"{orgName} {ptrLabel}" : $"{orgName} {winner.Host}",
            Address = winner.Ip,
            PtrHostname = winner.Host,
            AsnNumber = asn,
            AsnName = orgName,
            Role = UpstreamRole.Speedtest,
            HopNumber = 0,
            RespondedTo = ProbeMode.Icmp,
            Method = DiscoveryMethod.ConfiguredFallback,
            VerifiedRttMs = winner.Rtt,
            Enabled = true
        });
        _logger.LogInformation("Injected access fallback {Host} ({Ip}) for AS{Asn} {Org} - {Rtt:F1} ms, no reachable first-mile router",
            winner.Host, winner.Ip, asn, orgName, winner.Rtt);
    }

    /// <summary>A curated access-ISP host that resolved and cleared the reachability gate.</summary>
    internal sealed record AccessFallbackProbe(string Host, string Ip, double Rtt);

    /// <summary>
    /// Pick the lowest-RTT reachable curated endpoint, or null when none cleared the gate.
    /// Pure selection split out so it can be unit-tested without DNS or ICMP.
    /// </summary>
    internal static AccessFallbackProbe? SelectLowestRtt(IEnumerable<AccessFallbackProbe> probes) =>
        probes.OrderBy(p => p.Rtt).FirstOrDefault();

    /// <summary>
    /// Resolve a hostname to its first IPv4 (A-record) address, or null on failure. Uses the OS
    /// resolver via <see cref="System.Net.Dns"/>; the curated fallback hosts are plain unicast
    /// FQDNs so a single A lookup is sufficient.
    /// </summary>
    private async Task<string?> ResolveIPv4Async(string host, CancellationToken ct)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            return addresses
                .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Access fallback DNS resolution failed for {Host}", host);
            return null;
        }
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
            _logger.LogDebug("Commit access hop: id={TargetId} label='{Label}' addr={Address}", hop.TargetId, hop.Label, hop.Address);
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
            _logger.LogDebug("Commit transit: id={TargetId} label='{Label}' addr={Address} method={Method}",
                transit.TargetId, transit.Label, transit.HopAddress ?? transit.PathProxyTarget, transit.Method);
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
        // UniFi's WAN SLA probe targets (1.1.1.1 / 8.8.8.8) are public DNS resolvers, not
        // ISP first-mile infrastructure. They never belong as an Access ISP target; drop any
        // that slipped in before and never create one.
        if (NetworkUtilities.WanSlaProbeIps.Contains(hop.Address))
        {
            var stale = await db.MonitoringTargets
                .Where(t => t.TargetType == MonitoringTargetType.AccessIsp && t.Address == hop.Address)
                .ToListAsync(ct);
            if (stale.Count > 0) db.MonitoringTargets.RemoveRange(stale);
            return;
        }

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

    /// <summary>
    /// Storage-time ASN-name cleanup for discovered hops - delegates to the shared
    /// <see cref="NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName"/> (industry +
    /// legal suffixes). Manual target add (LatencyTargetsCard) calls the same helper, so a
    /// hand-added transit hop stores the same name discovery would ("Level 3", not "Level 3 Parent").
    /// </summary>
    internal static string CleanAsnName(string? name) =>
        NetworkOptimizer.Core.Helpers.NetworkFormatHelpers.CleanOrgName(name);

    /// <summary>
    /// Near-transit ASNs: every ASN that appears as the 1st or 2nd distinct non-access,
    /// non-destination ASN on at least one trace - the access ISP's direct upstream or
    /// its upstream's upstream, unioned across traces. A transit-probe ASN (Lumen, AT&amp;T,
    /// INDATEL) counts as our ISP's transit only when it lands in this window.
    ///
    /// The walk stops at the first tier-1: your transit horizon ends there. An ASN reached
    /// only by transiting a tier-1 (e.g. access → Arelion → INDATEL) is beyond your ISP's
    /// transit, not adjacent to it, so it is not near-transit. The tier-1 itself is included
    /// (it is the first upstream); the same INDATEL endpoint sitting one hop off the access
    /// ISP (access → INDATEL) stays near-transit because no tier-1 intervenes. Each trace is
    /// the responding hop addresses in hop order.
    /// </summary>
    internal static HashSet<int> ComputeNearTransitAsns(
        IEnumerable<IReadOnlyList<string>> traceAddressSequences,
        IReadOnlyDictionary<string, int> asnByIp,
        IReadOnlySet<int> accessAsns,
        IReadOnlySet<int> destinationAsns,
        IReadOnlySet<int> tier1Asns)
    {
        var near = new HashSet<int>();
        foreach (var trace in traceAddressSequences)
        {
            var degreesSeen = new HashSet<int>();
            foreach (var address in trace)
            {
                if (!asnByIp.TryGetValue(address, out var asn)) continue;
                if (accessAsns.Contains(asn) || destinationAsns.Contains(asn)) continue;
                if (degreesSeen.Add(asn))
                {
                    near.Add(asn);
                    // Transit horizon ends at the first tier-1: include it, then stop so
                    // nothing reached only by transiting it counts as near-transit.
                    if (tier1Asns.Contains(asn)) break;
                    if (degreesSeen.Count >= 2) break;
                }
            }
        }
        return near;
    }

    /// <summary>
    /// Tier-1 ASNs to exclude as transit because they only ever appear directly above
    /// another tier-1 on the path - core peering in the internet core, not our access
    /// ISP's transit. A tier-1 is kept when at least one trace shows it "grounded": the
    /// ASN immediately downstream (access side, lower TTL) is the access ISP itself, a
    /// non-tier-1 (a regional transit it feeds), or nothing (the tier-1 is the first
    /// resolved hop, so downstream is us). The access ISP is grounding even when it is
    /// itself a tier-1 (e.g. an AT&amp;T or Verizon fiber customer): the first tier-1 above
    /// the access edge is that ISP's upstream/peer and must stay, only tier-1s sitting
    /// above *another, non-access* tier-1 are core peering. Consecutive same-ASN hops
    /// are collapsed.
    /// </summary>
    internal static HashSet<int> ComputeExcludedTier1Asns(
        IEnumerable<IReadOnlyList<string>> traceAddressSequences,
        IReadOnlyDictionary<string, int> asnByIp,
        IReadOnlySet<int> tier1Asns,
        IReadOnlySet<int> accessAsns)
    {
        var seen = new HashSet<int>();
        var grounded = new HashSet<int>();
        foreach (var trace in traceAddressSequences)
        {
            int? prevAsn = null;
            foreach (var address in trace)
            {
                if (!asnByIp.TryGetValue(address, out var asn)) continue;
                if (asn == prevAsn) continue;
                if (tier1Asns.Contains(asn))
                {
                    seen.Add(asn);
                    if (prevAsn == null || accessAsns.Contains(prevAsn.Value) || !tier1Asns.Contains(prevAsn.Value))
                        grounded.Add(asn);
                }
                prevAsn = asn;
            }
        }
        seen.ExceptWith(grounded);
        return seen;
    }

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
