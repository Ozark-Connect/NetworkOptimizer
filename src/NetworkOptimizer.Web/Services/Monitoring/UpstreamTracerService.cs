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
        new("Akamai", "23.0.0.1")                                    // AS20940 - global netarch anycast loopback
    };

    private record TraceEndpoint(string Label, string Address, bool IsTransitProbe = false);

    public UpstreamTracerService(
        UniFiConnectionService connectionService,
        IGatewaySshService gatewaySsh,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        LocalProbeExecutor localProbe,
        IServiceScopeFactory scopeFactory,
        NetworkOptimizer.Audit.Services.IeeeOuiDatabase ouiDb,
        ILogger<UpstreamTracerService> logger)
    {
        _connectionService = connectionService;
        _gatewaySsh = gatewaySsh;
        _dbFactory = dbFactory;
        _asnResolution = asnResolution;
        _localProbe = localProbe;
        _scopeFactory = scopeFactory;
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

            State = new UpstreamTracerState
            {
                Step = TracerStep.DetectingPublicIp,
                StartedAt = DateTime.UtcNow,
                CurrentActivity = "Reading WAN configuration from gateway..."
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
        string? wanDevice = null;

        foreach (var ifaceCandidate in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var cmd = $"ip neigh show dev {ifaceCandidate} 2>/dev/null | head -5";
            var (ok, output) = await _gatewaySsh.RunCommandAsync(cmd, TimeSpan.FromSeconds(5), ct);
            if (!ok || string.IsNullOrWhiteSpace(output)) continue;

            // Line shape: "x.x.x.x lladdr aa:bb:cc:dd:ee:ff REACHABLE"
            var match = Regex.Match(output, @"lladdr\s+([0-9a-f:]{17})", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                neighborMac = match.Groups[1].Value.ToLowerInvariant();
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

        // Access hops walk, capped at 3. Two cases:
        //   - accessAsn known: skip any null-Asn hops in the prefix (a private
        //     intermediate like an upstream modem at 192.168.x.x that survived
        //     the gateway-IP filter); only break when we hit a hop with a
        //     different non-null ASN. Without the skip, an AT&T-style setup
        //     where the UniFi sits behind a residential gateway aborts the
        //     access classification before reaching the first AT&T hop.
        //   - accessAsn null (no public hop in the trace at all - fully
        //     filtered carrier, all-CGNAT): take the first private hops as
        //     access until a public ASN unexpectedly appears.
        _accessHopsResolved = new List<AttributedHop>();
        foreach (var h in candidateHops)
        {
            if (_accessHopsResolved.Count >= 3) break;
            if (accessAsn.HasValue)
            {
                if (h.Asn == null) continue;
                if (h.Asn.Asn != accessAsn.Value) break;
            }
            else
            {
                if (h.Asn != null) break;
            }
            _accessHopsResolved.Add(h);
        }

        State.AccessHops = _accessHopsResolved.Select(h => new AccessHopCandidate
        {
            TargetId = $"access-{NormalizeMacForId(h.Address)}",
            Label = LabelAccessHop(h),
            Address = h.Address,
            PtrHostname = h.Hostname,
            AsnNumber = h.Asn?.Asn,
            AsnName = h.Asn?.Name,
            Role = InferAccessRole(h, State.AccessTechnology, State.WanNeighborOuiVendor),
            HopNumber = h.HopNumber,
            RespondedTo = h.RespondedTo,
            Enabled = true
        }).ToList();

        State.CurrentActivity = State.AccessHops.Count > 0
            ? $"Identified {State.AccessHops.Count} access ISP hop(s) on AS{accessAsn}."
            : "No access-ISP hops responded. Discovery will continue but the access cloud will have no probed targets.";
    }

    private async Task<(string Label, TracerouteResult Result)> TraceOneAsync(TraceEndpoint endpoint, ProbeMode mode, CancellationToken ct)
    {
        var target = new ProbeTarget(endpoint.Address, mode);
        try
        {
            var result = await _localProbe.TracerouteAsync(target, maxHops: 30,
                perHopTimeout: TimeSpan.FromSeconds(2),
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

        var transitGroups = _mergedHops
            .Where(h => h.Asn != null
                        && !accessAsnNumbers.Contains(h.Asn.Asn)
                        && !destinationAsns.Contains(h.Asn.Asn)
                        && !(h.Asn.Name != null && destinationOrgs.Contains(h.Asn.Name.Trim())))
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
            var chosen = hopsInOrder.First(); // already filtered to "responded" by attribution

            candidates.Add(new TransitAsnCandidate
            {
                AsnNumber = asn.Asn,
                AsnName = asn.Name,
                Method = DiscoveryMethod.DirectRouter,
                TargetId = $"transit-as{asn.Asn}",
                HopAddress = chosen.Address,
                HopHostname = chosen.Hostname,
                RespondedTo = chosen.RespondedTo,
                Enabled = true
            });
        }

        State.TransitAsns = candidates;

        // Path-proxy: for every CDN destination whose ASN appeared anywhere in the
        // trace - not just traces that reached the literal endpoint - add the
        // endpoint as a path-end monitoring target. The previous "only if Reached"
        // gate missed destinations like Akamai whose anycast endpoints often
        // don't respond to ICMP/UDP probes even though the trace clearly entered
        // their network. Seeing the destination ASN attributed to any hop is a
        // strong signal the path-end is monitorable.
        var pathProxyAsnsSeen = new HashSet<int>(candidates.Select(c => c.AsnNumber));
        var asnsInTrace = new HashSet<int>(_mergedHops
            .Where(h => h.Asn != null)
            .Select(h => h.Asn!.Asn));
        foreach (var endpoint in CdnRotation)
        {
            // TransitProbe endpoints aren't destinations to monitor - their job
            // was to surface their ASN as transit (handled above). Skip the
            // path-end registration for them.
            if (endpoint.IsTransitProbe) continue;
            var destAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (destAsn == null) continue;
            if (accessAsnNumbers.Contains(destAsn.Asn)) continue;
            var trace = State.Traces.FirstOrDefault(t =>
                string.Equals(t.CdnEndpoint, endpoint.Address, StringComparison.OrdinalIgnoreCase));
            bool reachedOrTraversed = (trace?.Reached ?? false) || asnsInTrace.Contains(destAsn.Asn);
            if (!reachedOrTraversed) continue;
            if (!pathProxyAsnsSeen.Add(destAsn.Asn)) continue;     // dedupe across CDNs

            candidates.Add(new TransitAsnCandidate
            {
                AsnNumber = destAsn.Asn,
                AsnName = destAsn.Name,
                Method = DiscoveryMethod.PathProxy,
                TargetId = $"path-{endpoint.Label.ToLowerInvariant()}-as{destAsn.Asn}",
                HopAddress = endpoint.Address,
                PathProxyTarget = endpoint.Address,
                RespondedTo = ProbeMode.Icmp,
                Enabled = true
            });
        }

        State.TransitAsns = candidates;

        var transitCount = candidates.Count(c => c.Method == DiscoveryMethod.DirectRouter);
        var proxyCount = candidates.Count(c => c.Method == DiscoveryMethod.PathProxy);
        State.CurrentActivity = candidates.Count > 0
            ? $"Discovered {transitCount} transit ASN(s) and {proxyCount} path-end target(s)."
            : "No transit ASNs or path-end targets identified.";
    }

    /// <summary>
    /// Hop label priority per spec 5.5: PTR hostname > role inference > bare IP +
    /// ISP name. PTRs that just encode the IP (e.g. "h1.2.3.4.static.ip.example.net")
    /// fall through to bare IP since the encoded form is useless as a label.
    /// Otherwise we strip just the trailing TLD label so the ISP-identifying SLD
    /// stays in the label ("router-name.example" rather than "router-name").
    /// </summary>
    private static string LabelAccessHop(AttributedHop hop)
    {
        var ispName = hop.Asn?.Name;
        if (!string.IsNullOrEmpty(hop.Hostname))
        {
            var parts = hop.Hostname.Split('.');
            if (!IsIpDerivedHostname(parts, hop.Address))
            {
                // Strip only the final TLD label (.net/.com/...) so the SLD that
                // names the ISP is preserved. If the hostname has only one label
                // (e.g. "_gateway") just return it whole.
                return parts.Length > 1
                    ? string.Join('.', parts.Take(parts.Length - 1))
                    : hop.Hostname;
            }
            // IP-encoded PTR; fall through to the bare-IP branch below.
        }
        return string.IsNullOrEmpty(ispName) ? hop.Address : $"{hop.Address} - {ispName}";
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
            return UpstreamRole.Bng; // L2-transparent OLT means hop 1 is the BNG behind it.
        if (tech == AccessTechnology.Docsis && (isCmtsVendor || hop.HopNumber == 1))
            return UpstreamRole.Cmts;
        if (tech == AccessTechnology.PppoE && hop.HopNumber == 1)
            return UpstreamRole.Bng;
        if (hop.HopNumber >= 2 && hop.HopNumber <= 3)
            return UpstreamRole.Aggregation;
        return UpstreamRole.AccessHop;
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
        foreach (var transit in State.TransitAsns.Where(t => t.Enabled))
        {
            await UpsertTransitTargetAsync(db, transit, wanInterface, ct);
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
        State.Step = TracerStep.Done;
        State.CurrentActivity = "Targets committed. The agent will start probing on the next latency-tier cycle.";
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
                AsnName = hop.AsnName,
                VantagePoint = "server",
                PollIntervalSeconds = 10,
                PingCount = 5,
                Enabled = true,
                AutoDiscovered = true,
                DiscoveryMethod = DiscoveryMethod.DirectRouter,
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
            existing.Address = hop.Address;
            existing.ProbeMode = hop.RespondedTo;
            existing.WanInterface = wanInterface;
            existing.Name = string.IsNullOrEmpty(existing.Name) ? hop.Label : existing.Name; // don't stomp user-renamed labels
            if (hop.AsnNumber.HasValue) existing.AsnNumber = hop.AsnNumber;
            if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = hop.AsnName;
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
                Name = transit.AsnName,
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
                AutoDiscovered = true,
                DiscoveryMethod = transit.Method,
                WanInterface = wanInterface,
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            existing.Address = transit.HopAddress ?? transit.PathProxyTarget ?? existing.Address;
            existing.ProbeMode = transit.RespondedTo ?? existing.ProbeMode;
            existing.DiscoveryMethod = transit.Method;
            existing.WanInterface = wanInterface;
            // Refresh ASN bookkeeping in case the resolver picked up a name now
            // (legacy rows from before the GeoLite2 path landed had nulls).
            if (transit.AsnNumber > 0) existing.AsnNumber = transit.AsnNumber;
            if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
            existing.LastVerified = DateTime.UtcNow;
        }
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
