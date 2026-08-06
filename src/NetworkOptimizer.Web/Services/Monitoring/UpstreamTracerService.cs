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
    // Per-site: the site's console + gateway SSH + ISP Health, and the "server" probe
    // vantage - the local server on the default site, or the on-site agent (running the
    // same LocalProbeExecutor over the tunnel) on a secondary site, so the traceroute
    // originates on the site's own network with first-hop logic identical to home.
    private readonly string _siteSlug;
    private readonly bool _isDefault;
    private readonly UniFiConnectionService _connectionService;
    private readonly IGatewaySshService _gatewaySsh;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly AsnResolutionService _asnResolution;
    // Resolved per use, not captured: whether the site's agent covers it can change while the
    // instance lives, and the registry caches one tracer per site for the life of the process.
    private readonly Func<IProbeExecutor> _traceExecutorFactory;
    private IProbeExecutor _traceExecutor => _traceExecutorFactory();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IspHealth.IspHealthService _ispHealth;
    private readonly IspHealth.IspHealthRegistry _ispHealthRegistry;
    private readonly NetworkOptimizer.Audit.Services.IeeeOuiDatabase _ouiDb;
    private readonly ILogger<UpstreamTracerService> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Task? _runningTask;

    // The WAN context this instance discovers for, or null for the site's primary run - which is
    // every install with no contexts, and behaves exactly as it did before contexts existed.
    private readonly WanProbeBinding? _binding;

    /// <summary>
    /// Ties a discovery run to one WAN context: which UniFi WAN it measures, and what its probes
    /// bind to on the way out (the context's interface for an on-gateway agent, its policy-routed
    /// source IP otherwise). A run with a binding traces THAT WAN rather than the configured
    /// primary, and everything it persists is stamped with the WAN and the context.
    /// </summary>
    /// <param name="WanContextId">The <see cref="WanContext"/> row this run belongs to.</param>
    /// <param name="WanInterface">The UniFi WAN key the context measures ("wan", "wan2").</param>
    /// <param name="Source">Interface name or source IP the probes bind to; null to leave them on the prober's own route.</param>
    public sealed record WanProbeBinding(int WanContextId, string WanInterface, string? Source);

    /// <summary>The WAN context this tracer discovers for, or null when it is the site's primary tracer.</summary>
    public WanProbeBinding? Binding => _binding;

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

    // The anycast rotation bans unicast regionals (they'd force transpacific/transatlantic paths).
    // AWS DynamoDB regional endpoints are unicast BUT ride paid transit (unlike the CDN/DNS targets,
    // which peer at the local IX and cross no transit), so they surface more transit ASNs and seed
    // clean routes-through witnesses for jitter/stability absolution. Because they're unicast, they
    // are resolved + latency-ranked at discovery time and only the nearest sub-80ms region(s) are
    // kept - never hardcoded. Comprehensive/global list so a site anywhere keeps its local region(s).
    // Every commercial region whose endpoint answers ICMP (me-south-1 does not reply and is omitted).
    // GEO-ORDERED for the batched probe's early bail: batch 1 (of AwsProbeBatchSize) is the
    // Americas, batch 2 is Europe, batches 3+ are MEA/APAC - so once a site has found
    // AwsEnoughRegionals nearby regions, the far-side-of-the-globe rounds are skipped entirely.
    private static readonly string[] AwsRegions =
    {
        "us-east-1", "us-east-2", "us-west-1", "us-west-2",
        "ca-central-1", "ca-west-1", "mx-central-1", "sa-east-1",
        "eu-west-1", "eu-west-2", "eu-west-3", "eu-central-1", "eu-central-2",
        "eu-north-1", "eu-south-1", "eu-south-2",
        "il-central-1", "me-central-1", "af-south-1",
        "ap-east-1", "ap-east-2", "ap-south-1", "ap-south-2",
        "ap-northeast-1", "ap-northeast-2", "ap-northeast-3",
        "ap-southeast-1", "ap-southeast-2", "ap-southeast-3", "ap-southeast-4",
        "ap-southeast-5", "ap-southeast-6", "ap-southeast-7"
    };
    private const double AwsMaxRttMs = 80.0;

    /// <summary>Regions probed concurrently per round (DNS resolve + rapid ping burst each).</summary>
    internal const int AwsProbeBatchSize = 8;

    /// <summary>Sub-80ms regionals that are "enough": once a probe round has found this many, later rounds are skipped.</summary>
    internal const int AwsEnoughRegionals = 6;

    /// <summary>Max AWS regionals SURFACED as path-end candidates (transit-eliciting first, then lowest RTT).</summary>
    internal const int MaxAwsPathEndTargets = 7;

    /// <summary>
    /// How many of those arrive pre-enabled. Surfacing is cheap - a row to tick - but every enabled
    /// one becomes a probed target forever, and four path-ends through one provider already
    /// characterize the path. The rest are offered rather than chosen.
    /// </summary>
    internal const int MaxAwsAutoEnabledTargets = 4;

    // A resolved AWS regional: the hostname (what we persist as the target address, since AWS rotates
    // the regional IPs and the hostname re-resolves to a live in-region IP each poll) and the concrete
    // IP resolved this run (used for the discovery traceroute, RTT rank, and ASN lookup - deterministic).
    private record AwsRegional(string Region, string Hostname, string Ip, long RttMs);

    // Per-run trace set: the static CDN/DNS rotation plus resolved AWS regionals (traced by IP).
    // Defaults to the rotation so callers are safe before TraceAccessIspAsync populates it.
    private List<TraceEndpoint> _traceEndpoints = CdnRotation.ToList();
    private List<AwsRegional> _awsRegionals = new();

    /// <summary>
    /// Resolves + pings the AWS DynamoDB regional hostnames in parallel rounds of
    /// <see cref="AwsProbeBatchSize"/> (the region list is geo-ordered, Americas -> EU -> MEA/APAC)
    /// and returns those answering under <see cref="AwsMaxRttMs"/>, sorted nearest-first. Bails out
    /// of later rounds once <see cref="AwsEnoughRegionals"/> sub-80ms regions are in hand - a site
    /// that already found that many nearby never probes the far side of the globe. Not anycast, so
    /// this per-run resolve + latency-rank is required - a far region would trace a transcontinental
    /// path and misrepresent the transit. The persist block trims what's surfaced to
    /// <see cref="MaxAwsPathEndTargets"/> (transit-eliciting first, then lowest RTT); only regions
    /// whose trace crosses transit are pre-enabled. Best-effort: a region that won't resolve or
    /// answer ICMP is skipped.
    /// </summary>
    private async Task<List<AwsRegional>> ResolveAwsRegionalsAsync(CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var found = new List<AwsRegional>();
        var round = 0;
        foreach (var batch in AwsRegions.Chunk(AwsProbeBatchSize))
        {
            if (ct.IsCancellationRequested) break;
            round++;
            var results = await Task.WhenAll(batch.Select(r => ProbeAwsRegionAsync(r, ct)));
            var hits = results.Where(r => r != null).Select(r => r!).ToList();
            found.AddRange(hits);
            _logger.LogDebug("Tracer: AWS probe round {Round} ({First}..{Last}): {Hits}/{Probed} sub-{Max}ms [{HitDetail}] - {Total} total, {Elapsed} ms elapsed",
                round, batch[0], batch[^1], hits.Count, batch.Length, AwsMaxRttMs,
                string.Join(", ", hits.Select(h => $"{h.Region} {h.RttMs}ms")), found.Count, sw.ElapsedMilliseconds);
            if (found.Count >= AwsEnoughRegionals)
            {
                _logger.LogDebug("Tracer: AWS probe bail after round {Round} - {Count} regionals is enough, skipping {Skipped} remaining region(s) ({Elapsed} ms total)",
                    round, found.Count, AwsRegions.Length - round * AwsProbeBatchSize, sw.ElapsedMilliseconds);
                break;
            }
        }
        var chosen = found.OrderBy(r => r.RttMs).ToList();
        if (chosen.Count > 0)
            _logger.LogInformation("Tracer: {Count} sub-{Max}ms AWS DynamoDB regionals in {Elapsed} ms ({Rounds} round(s)): {Regions}",
                chosen.Count, AwsMaxRttMs, sw.ElapsedMilliseconds, round, string.Join(", ", chosen.Select(r => r.Region)));
        return chosen;
    }

    /// <summary>
    /// One region's DNS resolve + rapid 3-ping burst via the vantage's probe executor (-i 0.2 where
    /// the platform allows, agent vantage on secondary sites). Ranks/gates on the burst MINIMUM -
    /// the standard path-distance estimator (one queued reply shouldn't push a nearby region over
    /// the gate). Null when unresolvable, unreachable, or over the RTT gate.
    /// </summary>
    private async Task<AwsRegional?> ProbeAwsRegionAsync(string region, CancellationToken ct)
    {
        try
        {
            var host = $"dynamodb.{region}.amazonaws.com";
            var addrs = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            var ip = addrs.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            if (ip == null) return null;
            var result = await ProbeReachabilityAsync(ip.ToString(), ProbeMode.Icmp, ct);
            if (result.Received == 0 || (result.RttMinMs ?? result.RttAvgMs) is not double rtt || rtt > AwsMaxRttMs) return null;
            return new AwsRegional(region, host, ip.ToString(), (long)Math.Round(rtt));
        }
        catch { return null; /* region won't resolve or answer - skip */ }
    }

    /// <summary>
    /// The farthest (deepest) transit ASN on the trace to <paramref name="destIp"/> - what a path-end
    /// host connects "via" - with a cleaned name. Walks that destination's trace hops in order and
    /// keeps the last hop whose attributed ASN is neither an access ASN nor the destination's own org.
    /// The destination match is by cleaned NAME, not just ASN number, so a provider that appears under
    /// sibling ASNs (Amazon's AS16509 / AS14618, a transit's multiple ASNs) isn't mistaken for transit.
    /// Null when the path crosses no transit (peered / IX). <paramref name="asnByHopIp"/> maps hop IP
    /// to its attributed ASN from the merged pool, so no extra lookups are done here.
    /// </summary>
    private (int Asn, string Name)? FarthestTransitVia(string destIp, int destAsn, string destName,
        HashSet<int> accessAsns, Dictionary<string, AsnLookup> asnByHopIp, out bool pathComplete)
    {
        var responded = _lastTraces
            .Where(t => string.Equals(t.Target.Address, destIp, StringComparison.OrdinalIgnoreCase))
            .SelectMany(t => t.Hops)
            .Where(h => h.Responded && h.Address != null)
            .ToList();
        var respondedHopNums = responded.Select(h => h.HopNumber).ToHashSet();

        (int Asn, string Name)? via = null;
        int? accessStartHop = null;
        int? destOrgHop = null;
        foreach (var h in responded.OrderBy(h => h.HopNumber))
        {
            if (!asnByHopIp.TryGetValue(h.Address!, out var asn)) continue;
            if (accessAsns.Contains(asn.Asn)) { accessStartHop ??= h.HopNumber; continue; }
            var name = CleanAsnName(asn.Name);
            if (asn.Asn == destAsn || string.Equals(name, destName, StringComparison.OrdinalIgnoreCase))
            {
                destOrgHop ??= h.HopNumber;
                continue;
            }
            if (destOrgHop == null) via = (asn.Asn, name); // farthest transit before we reach the dest org
        }

        // "Peered" is only provable when we reached the destination's org AND every hop from the
        // access ASN to it responded - a star in between could be hiding transit. Otherwise the
        // caller shows a dash (unknown), not "peered".
        var from = accessStartHop ?? 1;
        pathComplete = destOrgHop is int dh && dh >= from
            && Enumerable.Range(from, dh - from + 1).All(n => respondedHopNums.Contains(n));
        return via;
    }

    // Built per site by UpstreamTracerRegistry, which resolves the site's console,
    // gateway SSH, ISP Health, and "server" probe vantage (local on the default site,
    // on-site agent on a secondary site).
    public UpstreamTracerService(
        string siteSlug,
        bool isDefault,
        UniFiConnectionService connectionService,
        IGatewaySshService gatewaySsh,
        IspHealth.IspHealthService ispHealth,
        IspHealth.IspHealthRegistry ispHealthRegistry,
        Func<IProbeExecutor> traceExecutor,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        IServiceScopeFactory scopeFactory,
        NetworkOptimizer.Audit.Services.IeeeOuiDatabase ouiDb,
        ILogger<UpstreamTracerService> logger,
        WanProbeBinding? binding = null)
    {
        _binding = binding;
        _siteSlug = siteSlug;
        _isDefault = isDefault;
        _connectionService = connectionService;
        _gatewaySsh = gatewaySsh;
        _ispHealth = ispHealth;
        _ispHealthRegistry = ispHealthRegistry;
        _traceExecutorFactory = traceExecutor;
        _siteDbFactory = siteDbFactory;
        _dbFactory = dbFactory;
        _asnResolution = asnResolution;
        _scopeFactory = scopeFactory;
        _ouiDb = ouiDb;
        _logger = logger;
    }

    /// <summary>The site's own database for persisting upstream discovery state.</summary>
    private async Task<NetworkOptimizerDbContext> CreateDbAsync(CancellationToken ct = default) =>
        _isDefault ? await _dbFactory.CreateDbContextAsync(ct) : _siteDbFactory.CreateForSite(_siteSlug, isDefault: false);

    private static readonly TimeSpan WitnessAncestryTtl = TimeSpan.FromHours(12);
    private const int MaxWitnessTracesPerTick = 6;

    /// <summary>
    /// Fills and refreshes hop ancestry for this site's enabled Custom + InternetService targets.
    /// They aren't part of the discovery sweep (only the built-in CDN/AWS probes and discovered
    /// ISP/transit hops are), so without this they'd never become - or would go stale as -
    /// routes-through witnesses. Traces those whose ancestry is missing or older than
    /// <see cref="WitnessAncestryTtl"/> over this site's vantage (server or on-site agent),
    /// rate-limited to <see cref="MaxWitnessTracesPerTick"/> per call. Called every re-discovery
    /// tick (hourly), independent of the full re-discovery cadence. Best-effort.
    /// </summary>
    public async Task BackfillWitnessAncestryAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await CreateDbAsync(ct);
            var targets = await db.MonitoringTargets.AsNoTracking()
                .Where(t => t.Enabled && (t.TargetType == MonitoringTargetType.Custom
                    || t.TargetType == MonitoringTargetType.InternetService))
                .ToListAsync(ct);
            if (targets.Count == 0) return;

            // A target is "fresh" only when it has a NON-EMPTY ancestry row traced within the TTL. An
            // empty row (e.g. a hostname-addressed AWS regional whose discovery same-path pass couldn't
            // attribute it, but which stamped LastTracerouteAt) must NOT count as fresh, or it would
            // never get real ancestry and would read as carrying no transit in ISP Health.
            var cutoff = DateTime.UtcNow - WitnessAncestryTtl;
            var freshIds = (await db.UpstreamDiscoveries.AsNoTracking()
                .Where(d => d.MonitoringTargetId != null
                    && d.AncestorHopIps != null && d.AncestorHopIps != ""
                    && d.LastTracerouteAt > cutoff)
                .Select(d => d.MonitoringTargetId!.Value)
                .Distinct()
                .ToListAsync(ct))
                .ToHashSet();

            var traced = 0;
            foreach (var t in targets)
            {
                if (ct.IsCancellationRequested || traced >= MaxWitnessTracesPerTick) break;
                if (freshIds.Contains(t.Id)) continue;
                await using var wdb = await CreateDbAsync(ct);
                if (await TargetAncestry.TraceAndPersistAsync(_traceExecutor, wdb, t.Id, t.Address, _logger, ct))
                    traced++;
            }
            if (traced > 0)
                _logger.LogInformation("Tracer: backfilled/refreshed ancestry for {Count} witness target(s) on site {Site}", traced, _siteSlug);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Witness ancestry backfill failed for site {Site}", _siteSlug);
        }
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
            await using var db = await CreateDbAsync(ct);
            // The tracer rehydrates ITS OWN WAN's committed state. A context-bound tracer reads
            // exactly its WAN's row; the primary tracer asks the console which WAN is the
            // configured primary (primary is a ROLE - it can be any wanN group) and only guesses
            // when the console cannot answer. The old newest-first pick alone would hand the
            // primary panel whichever WAN discovered LAST (a context's nightly run), showing a
            // secondary's hops as the primary's. Single-WAN sites have one row either way.
            string? configuredPrimaryKey = null;
            if (_binding == null)
            {
                try
                {
                    var primaryNet = await _connectionService.GetPrimaryWanNetworkAsync(ct);
                    if (!string.IsNullOrEmpty(primaryNet?.WanNetworkgroup))
                        configuredPrimaryKey = NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(primaryNet!.WanNetworkgroup!);
                }
                catch { /* console unreachable - fall through to the documented guess */ }
            }
            var contexts = await db.WanDiscoveryContexts.ToListAsync(ct);
            var ctx = PickRehydrateContext(contexts, _binding?.WanInterface, configuredPrimaryKey);
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
                preservedTech = await LoadPersistedAccessTechnologyAsync(State.WanInterface, ct);
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
    /// Reads the saved access technology from the DB. Fallback for when a run starts
    /// (notably the background re-discovery scheduler) without the UI having hydrated
    /// the in-memory state first. The technology is per-WAN, so the traced WAN's own
    /// context row wins when <paramref name="wanInterface"/> is known; otherwise (and
    /// when that row has nothing set) any row with a known technology is used,
    /// recency-ordered - the old "most recent row, whatever it holds" pick returned
    /// Unknown on multi-WAN installs whenever a different WAN's row was fresher, which
    /// mislabeled the first-mile device (e.g. "cisco-access" instead of "cisco-olt" on
    /// a saved-GPON install). Last resort is the legacy single-WAN
    /// MonitoringSettings.AccessTechnology from installs that predate per-WAN contexts.
    /// Returns Unknown when nothing is persisted anywhere.
    /// </summary>
    private async Task<AccessTechnology> LoadPersistedAccessTechnologyAsync(string? wanInterface, CancellationToken ct)
    {
        try
        {
            await using var db = await CreateDbAsync(ct);
            var contexts = await db.WanDiscoveryContexts.AsNoTracking().ToListAsync(ct);

            var own = contexts.FirstOrDefault(c =>
                wanInterface != null &&
                string.Equals(c.WanInterface, wanInterface, StringComparison.OrdinalIgnoreCase));
            if (own != null && own.AccessTechnology != AccessTechnology.Unknown)
                return own.AccessTechnology;

            // A context run measures exactly one WAN, so another WAN's technology is not evidence
            // about it: an LTE backup behind a fiber primary would inherit "GPON" and have its
            // first-mile device labeled as an OLT. Unknown is the honest answer, and it is what
            // the reachability gate and role inference already handle.
            if (_binding != null) return AccessTechnology.Unknown;

            var known = contexts
                .Where(c => c.AccessTechnology != AccessTechnology.Unknown)
                .OrderByDescending(c => c.LastDiscoveryAt ?? c.UpdatedAt)
                .FirstOrDefault();
            if (known != null) return known.AccessTechnology;

            var settings = await db.MonitoringSettings.AsNoTracking().FirstOrDefaultAsync(ct);
            return settings?.AccessTechnology ?? AccessTechnology.Unknown;
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
            // The access technology is stored per-WAN; the pre-run load couldn't key on
            // the WAN yet. Now that detection resolved which WAN we're tracing, retry
            // against its own context row before any technology-driven labeling runs.
            if (State.AccessTechnology == AccessTechnology.Unknown)
                State.AccessTechnology = await LoadPersistedAccessTechnologyAsync(State.WanInterface, ct);
            if (!await DiscoverL2NeighborAsync(ct)) return;
            await TraceAccessIspAsync(ct);
            await TraceTransitAsnsAsync(ct);
            await VerifyReachabilityAsync(ct);

            // Shared post-run change evaluation - manual and scheduled runs alike advance the
            // per-WAN absence counters exactly once per completed run, and both stage any
            // confirmed off-path transit ASNs for the review. A failure here only skips the
            // counters for this run; the review of the discovered candidates still proceeds.
            try
            {
                await using var evalDb = await CreateDbAsync(ct);
                await UpstreamRediscoveryService.EvaluateCompletedRunAsync(evalDb, State, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Post-run off-path evaluation failed; absence counters not advanced this run");
            }

            // Metered WANs arrive with fewer candidates ticked. Done here rather than at commit so
            // the review shows what will actually be probed, with the count the operator can change.
            try
            {
                await using var planDb = await CreateDbAsync(ct);
                var reviewPlan = await ResolveProbePlanAsync(
                    planDb, _binding?.WanInterface ?? State.WanInterface ?? "wan", ct);
                ApplyAutoEnableBudget(State, reviewPlan.MaxAutoEnabled);
                if (reviewPlan.Rung > 0)
                    _logger.LogInformation(
                        "Metered WAN {Wan} (rung {Rung}): {Max} target(s) pre-selected at {Interval}s",
                        State.WanInterface, reviewPlan.Rung, reviewPlan.MaxAutoEnabled, reviewPlan.PollIntervalSeconds);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not apply the metered probe budget; leaving candidates as discovered");
            }

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
        // A context run skips that entirely: the context already names the WAN it
        // measures, and picking the primary would trace the wrong one.
        string? primaryNg = null;
        if (_binding == null)
        {
            try
            {
                var networks = await _connectionService.GetNetworksAsync(ct);
                primaryNg = UniFiConnectionService.ResolvePrimaryWanNetwork(networks, _logger)?.WanNetworkgroup;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UpstreamTracer: failed to resolve primary WAN networkgroup; falling back to first WAN");
            }
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

                    // A context run takes its own WAN and nothing else - no first-WAN fallback,
                    // since tracing a different WAN than the one being recorded would file this
                    // WAN's upstream under that one.
                    if (_binding != null)
                    {
                        if (!string.Equals(interfaceKey, _binding.WanInterface, StringComparison.OrdinalIgnoreCase))
                            continue;
                        wanInterfaceName = interfaceKey;
                        wanUplinkIfName = uplinkIfname;
                        wanIp = ip;
                        break;
                    }

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
                if (wanInterfaceName == null && _binding == null && firstWan != null)
                {
                    wanInterfaceName = firstWan.Value.Key;
                    wanUplinkIfName = firstWan.Value.Uplink;
                    wanIp = firstWan.Value.Ip;
                }

                if (wanInterfaceName != null) break;
            }
        }

        if (wanInterfaceName == null)
            return Fail(_binding == null
                ? "Couldn't identify the WAN port on the gateway."
                : $"The gateway no longer reports {_binding.WanInterface}, so this context's WAN can't be traced.");

        State.WanInterface = wanInterfaceName;
        _wanUplinkIfName = wanUplinkIfName;
        State.WanIpAddress = wanIp;
        State.WanIpClass = NetworkUtilities.ClassifyPublicAddress(wanIp);

        // A gre* uplink is a UniFi Cellular Modem attached to the gateway, and nothing else on the
        // gateway presents a WAN that way, so this WAN's medium is known from the interface rather
        // than guessed from whoever answered. It settles only this case: a third-party modem or a
        // bridged carrier router is equally cellular and looks like an ordinary WAN, so those still
        // rely on the inference below or on the user. Decided HERE rather than beside the vendor
        // inference because a GRE tunnel has no ARP neighbor, so that step returns early on exactly
        // these WANs and never reaches it. Still only fills an empty slot.
        if (State.AccessTechnology is AccessTechnology.Unknown or AccessTechnology.PppoE
            && NetworkUtilities.IsUniFiCellularModemTunnel(_wanUplinkIfName))
        {
            State.AccessTechnology = AccessTechnology.Cellular;
            State.AccessTechnologyInferred = true;
            _logger.LogDebug("Tracer: access technology set to Cellular from the {Uplink} uplink", _wanUplinkIfName);
        }

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
            await using var db = await CreateDbAsync(ct);
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

        // The WAN's default gateway from the routing tables. UniFi gateways run policy
        // routing with per-WAN tables, so the default isn't in the main table - but
        // `ip route show table all` surfaces every table's default route, and the line
        // whose dev is our WAN interface names the true next hop. This is authoritative:
        // on a shared WAN subnet (metro Ethernet, two sites on one carrier segment) the
        // neighbor table also carries OTHER same-subnet hosts, and heuristic scoring can
        // pick one of those peers over the actual gateway. We still read the gateway's
        // MAC from `ip neigh` below - the routing table only supplies WHICH IP to pick.
        string? routeShowAll = null;
        {
            var (routeOk, routeOut) = await _gatewaySsh.RunCommandAsync(
                "ip route show table all 2>/dev/null | grep '^default' | head -20",
                TimeSpan.FromSeconds(5), ct);
            if (routeOk && !string.IsNullOrWhiteSpace(routeOut)) routeShowAll = routeOut;
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

            var defaultGatewayIp = SelectWanDefaultGateway(routeShowAll, ifaceCandidate);
            var selected = SelectWanNeighbor(output, wanCidr, defaultGatewayIp);
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

        // With no technology set for this WAN, an unambiguous vendor is better evidence than
        // nothing: the reachability gate and every role label key off the technology, and
        // Unknown makes all of them fall back to generic. Only proposes into an empty slot -
        // a user's (or an earlier run's) choice is never overwritten.
        //
        // A stored PppoE counts as empty here. PPPoE names an encapsulation, not a medium, so a
        // vendor-derived medium is strictly better evidence for this field, and the redirect
        // costs nothing even once PPPoE earns scoring behavior of its own: the encapsulation is
        // re-derivable any time from uplink_ifname "ppp0" / wan_type "pppoe". Nothing infers PPPoE -
        // TechnologyFromVendor cannot return it, and it is no longer in the Upstream Discovery
        // dropdown - so this only redirects values picked before it was removed.
        if (State.AccessTechnology is AccessTechnology.Unknown or AccessTechnology.PppoE)
        {
            var inferred = TechnologyFromVendor(State.WanNeighborOuiVendor);
            if (inferred != null)
            {
                State.AccessTechnology = inferred.Value;
                State.AccessTechnologyInferred = true;
                _logger.LogDebug("Tracer: access technology inferred as {Tech} from L2 neighbor vendor {Vendor}",
                    inferred.Value, State.WanNeighborOuiVendor);
            }
        }

        // Persist the WAN neighbor info to MonitoringSettings so the access cloud label
        // survives across discovery runs and is available to MonitoringPathView.
        try
        {
            await using var db = await CreateDbAsync(ct);
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
    /// Parses `ip route show table all` output for the default route egressing the given
    /// WAN interface and returns its gateway ("via") address. UniFi gateways run policy
    /// routing, so each WAN's default lives in its own table (`default via 203.0.113.1
    /// dev eth8 table 201 ...`); matching on the dev finds ours regardless of table
    /// number. Returns the first match (multiple tables for one WAN name the same next
    /// hop). On-link defaults without a `via` (PPPoE's `default dev ppp0 scope link`)
    /// yield null - there's no gateway address, and no MAC to look up either.
    /// </summary>
    internal static string? SelectWanDefaultGateway(string? routeShowAllOutput, string wanIface)
    {
        if (string.IsNullOrWhiteSpace(routeShowAllOutput) || string.IsNullOrEmpty(wanIface)) return null;
        var m = Regex.Match(routeShowAllOutput,
            @"^default\s+via\s+(?<gw>\d{1,3}(?:\.\d{1,3}){3})\s+dev\s+" + Regex.Escape(wanIface) + @"(\s|$)",
            RegexOptions.Multiline);
        return m.Success ? m.Groups["gw"].Value : null;
    }

    /// <summary>
    /// Picks the WAN-side L2 neighbor from `ip neigh show dev &lt;wan&gt;` output. A CPE
    /// bridged in front of the gateway (an ISP modem/router in passthrough) lists both
    /// its LAN-side RFC1918 address and the carrier-side address under the same MAC;
    /// the LAN-side entry often sorts first, and taking the first lladdr line mislabeled
    /// a private CPE IP as an ISP hop. When the WAN's routed default gateway is known
    /// (from <see cref="SelectWanDefaultGateway"/>) its entry wins outright - on a shared
    /// WAN subnet the neighbor table also lists unrelated same-subnet hosts (another
    /// site's gateway on the same carrier segment), and no heuristic can rank those below
    /// the true next hop reliably. Otherwise preference order: in the WAN subnet (the real
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
    /// <param name="defaultGatewayIp">The WAN's routed default gateway, when known. Its
    /// neighbor entry is returned outright, bypassing the heuristic scoring.</param>
    public static (string Ip, string Mac)? SelectWanNeighbor(string? ipNeighOutput, string? wanCidr = null, string? defaultGatewayIp = null)
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

            // The routed next hop IS the first-mile device; no scoring needed.
            if (defaultGatewayIp != null && string.Equals(ipText, defaultGatewayIp, StringComparison.OrdinalIgnoreCase))
                return (ipText, m.Groups[2].Value.ToLowerInvariant());

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
    /// <summary>Best (lowest) RTT seen for a hop address across every trace, in ms.</summary>
    private readonly Dictionary<string, double> _minRttByIp = new(StringComparer.OrdinalIgnoreCase);
    private List<AttributedHop> _accessHopsResolved = new();

    // The detected access ISP ASN from the last TraceAccessIspAsync. Kept as a field so
    // the reachability step can fall back to a curated endpoint even when none of the
    // access hops responded (the access pool can be empty/unreachable yet the ASN known).
    private int? _accessAsn;

    // Cleaned display name for _accessAsn - from its hops' ASN attribution, or from the
    // WAN IP lookup when the ASN was derived from the WAN address and no hop carries the
    // name. Used by the curated-fallback injection for labeling.
    private string? _accessAsnName;

    // Destination (CDN endpoint) ASNs and org names from the rotation, resolved once per
    // run during the access step (they gate the access-ISP pick) and reused by the transit
    // step. Transit-probe endpoints are excluded on purpose - their ASN is allowed to
    // surface as transit.
    private readonly HashSet<int> _destinationAsns = new();
    private readonly HashSet<string> _destinationOrgs = new(StringComparer.OrdinalIgnoreCase);

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

        // Build this run's endpoint set: the anycast rotation plus every sub-80ms AWS regional
        // (resolved live, traced by IP), which surface paid-transit ASNs and seed clean witnesses.
        _awsRegionals = await ResolveAwsRegionalsAsync(ct);
        _traceEndpoints = CdnRotation
            .Concat(_awsRegionals.Select(r => new TraceEndpoint($"AWS-{r.Region}", r.Ip)))
            .ToList();

        // Spawn traceroutes (each endpoint × 2 modes) in parallel and merge once they all settle.
        // Each traceroute is capped at 10s, so wall-clock for the whole sweep is ~10s + overhead.
        var tasks = new List<Task<(string Label, TracerouteResult Result)>>();
        foreach (var endpoint in _traceEndpoints)
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
        _minRttByIp.Clear();
        var byIp = new Dictionary<string, AttributedHop>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, result) in results)
        {
            foreach (var hop in result.Hops)
            {
                if (!hop.Responded || string.IsNullOrEmpty(hop.Address)) continue;
                var rtt = hop.RttMinMs ?? hop.RttAvgMs;
                if (rtt is double seen
                    && (!_minRttByIp.TryGetValue(hop.Address, out var best) || seen < best))
                    _minRttByIp[hop.Address] = seen;
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

        // Resolve the destination (CDN) ASNs up front: the access-ISP pick below must
        // never crown a probe destination's own ASN, and the transit step reuses the sets.
        await ResolveDestinationAsnsAsync(ct);

        // The WAN IP's own ASN is the strongest access-ISP signal when the address is
        // public: it's the ISP's customer allocation, independent of which routers
        // answer traceroute. ResolveAsync returns null for CGNAT/private WAN addresses.
        AsnLookup? wanIpAsn = null;
        if (!string.IsNullOrEmpty(State.WanIpAddress))
            wanIpAsn = await _asnResolution.ResolveAsync(State.WanIpAddress, ct);

        // Identify the access ISP ASN. The old heuristic ("first hop with a resolvable
        // ASN across the merged pool") broke on ISPs like Bell Canada (#984) whose entire
        // first mile is RFC1918 plus public space deliberately NOT announced in BGP: the
        // first attributable hop was the probed CDN's own edge, and the destination
        // (Cloudflare) got crowned as the access ISP. DetermineAccessAsn combines the
        // WAN IP's ASN with per-trace voting instead.
        var asnByIp = BuildAsnByIpMap();
        var traceSequences = BuildTraceSequences();
        var accessAsn = DetermineAccessAsn(traceSequences, asnByIp, wanIpAsn?.Asn, _destinationAsns);
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

        // Unannounced first-mile hops: an ISP like Bell (#984) numbers its aggregation
        // layer from public or CGNAT space that carries no BGP attribution (Asn == null),
        // upstream of its announced routers. Any such hop appearing BEFORE the first
        // access-ASN hop on its own trace sits on the customer side of the ISP's
        // announced border - it is access infrastructure, positionally attributed.
        // RFC1918 prefix hops stay excluded (a bridged CPE's LAN address or a double-NAT
        // middlebox is not ISP infra); the reachability gate later disables any of these
        // that don't answer ping.
        if (accessAsn.HasValue)
        {
            var alreadyIncluded = new HashSet<string>(
                _accessHopsResolved.Select(h => h.Address), StringComparer.OrdinalIgnoreCase);
            var unannounced = CollectUnannouncedAccessAddresses(
                    traceSequences, asnByIp, accessAsn.Value, _gatewayIps, _minRttByIp)
                .Where(a => !alreadyIncluded.Contains(a) && !_gatewayIps.Contains(a) && byIp.ContainsKey(a))
                .Select(a => byIp[a])
                .OrderBy(h => h.HopNumber)
                .ToList();
            _accessHopsResolved = unannounced.Concat(_accessHopsResolved).ToList();
        }

        // On a UniFi Cellular Modem WAN the gateway reaches the modem over a GRE tunnel, so hop 1 is
        // the modem's own tunnel endpoint: a CGNAT address on our side of the radio, answering in a
        // fraction of a millisecond, that says nothing about the carrier's first mile. Dropped here
        // rather than in any single collector because three paths feed this pool - the access-ASN
        // filter, the positional pass for unannounced CGNAT first-mile hops, and the border walk -
        // and it arrives by the second, which by design admits exactly this shape of address.
        // Confined to that one topology: a cellular WAN behind any other modem has no tunnel hop to
        // drop, and on every other medium hop 1 IS the first-mile device. Carrier hops past it, CGNAT
        // included, stay eligible.
        if (NetworkUtilities.IsUniFiCellularModemTunnel(_wanUplinkIfName))
        {
            var tunnelHops = _accessHopsResolved.Where(h => h.HopNumber <= 1).ToList();
            if (tunnelHops.Count > 0)
            {
                _accessHopsResolved = _accessHopsResolved.Where(h => h.HopNumber > 1).ToList();
                _logger.LogDebug(
                    "Tracer: dropped {Count} first hop(s) at the {Uplink} tunnel endpoint from the access pool: {Addresses}",
                    tunnelHops.Count, _wanUplinkIfName, string.Join(", ", tunnelHops.Select(h => h.Address)));
            }
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

        // Access org display name: from an attributed access hop when one exists, else
        // from the WAN IP lookup (a fully silent/unannounced first mile still labels).
        var accessAsnRawName = _accessHopsResolved.FirstOrDefault(h => h.Asn != null)?.Asn?.Name
                               ?? (accessAsn.HasValue && accessAsn.Value == wanIpAsn?.Asn ? wanIpAsn.Name : null);
        var orgName = CleanAsnName(accessAsnRawName);
        _accessAsnName = string.IsNullOrEmpty(orgName) ? null : orgName;
        // Which hop the WAN-side vendor evidence can speak for: the box on the other end of the
        // WAN, and nothing behind it.
        //
        // The L2 neighbor IS that box whenever we have one - it is read from the WAN's own neighbor
        // table, not inferred from distance - so when it is going to be injected below, no traced
        // hop is first mile. Only when the trace already surfaced it does a traced hop hold the
        // slot; failing both, the nearest traced hop is the best we can say.
        var l2FirstMile = !string.IsNullOrEmpty(State.WanNeighborIp)
                          && IsInjectableAccessHopAddress(State.WanNeighborIp);
        var l2Traced = l2FirstMile && _accessHopsResolved.Any(h =>
            string.Equals(h.Address, State.WanNeighborIp, StringComparison.OrdinalIgnoreCase));
        var firstMileHopNumber =
            l2Traced ? _accessHopsResolved.First(h =>
                    string.Equals(h.Address, State.WanNeighborIp, StringComparison.OrdinalIgnoreCase)).HopNumber
            : l2FirstMile ? -1
            : _accessHopsResolved.Count > 0 ? _accessHopsResolved.Min(h => h.HopNumber)
            : -1;
        State.AccessHops = _accessHopsResolved.Select(h => new AccessHopCandidate
        {
            TargetId = $"access-{NormalizeMacForId(h.Address)}",
            Label = "",
            Address = h.Address,
            PtrHostname = h.Hostname,
            // A hop with no BGP attribution of its own is here BECAUSE it sits below the ISP's
            // announced border - private first-mile gear, or CGNAT. It is the ISP's, so it is
            // stored as the ISP's: left unattributed it reads as an unknown network on the path,
            // and nothing downstream would grade it against the access ISP.
            AsnNumber = h.Asn?.Asn ?? accessAsn,
            AsnName = h.Asn?.Name ?? (h.Asn == null ? accessAsnRawName : null),
            Role = borderIps.Contains(h.Address)
                ? UpstreamRole.Border
                : InferAccessRole(h, State.AccessTechnology, State.WanNeighborOuiVendor,
                    h.HopNumber == firstMileHopNumber),
            HopNumber = h.HopNumber,
            RespondedTo = h.RespondedTo,
            Enabled = true
        }).ToList();

        // A Starlink hop whose PTR is the "undefined" placeholder is a SATELLITE, not ground
        // infrastructure - overhead for a few minutes and then gone, so the row would flap between
        // reachable and dark for as long as it existed. Dropped from the candidate set outright
        // rather than proposed and left to fail: nothing on the ground answers that way, and the
        // ground hops on the same ASN keep a real PTR to be found by.
        State.AccessHops.RemoveAll(h =>
            IsPlaceholderPtrHostname(h.PtrHostname)
            && IsStarlinkAsn(h.AsnNumber ?? accessAsn, h.AsnName ?? orgName));

        // Generate "<Org> <PTR-derived>" labels, same format as transit targets. With no usable
        // PTR the hop's own number names it, bare. Where several responders answer at the SAME hop
        // - ECMP, which is how satellite first miles usually look - the number alone names them
        // all identically, so those carry a -1, -2 suffix and nothing else does.
        foreach (var hopGroup in State.AccessHops.GroupBy(h => h.HopNumber))
        {
            var labeled = hopGroup
                .Select(h => (Hop: h, Ptr: FormatTransitHopLabel(h.PtrHostname, h.Address)))
                .ToList();
            var unnamedCount = labeled.Count(x => x.Ptr == null);
            var suffix = 0;
            foreach (var (hop, ptr) in labeled)
            {
                hop.Label = ptr != null
                    ? $"{orgName} {ptr}"
                    : unnamedCount > 1
                        ? $"{orgName} {hop.HopNumber}-{++suffix}"
                        : $"{orgName} {hop.HopNumber}";
            }
        }

        // The ISP itself can settle the technology where the L2 neighbor could not: a Starlink
        // dish presents its own router to the gateway, so the OUI names the CPE rather than the
        // medium. Same rule as the vendor inference - only into an empty slot, never over a
        // choice someone made.
        if (State.AccessTechnology is AccessTechnology.Unknown or AccessTechnology.PppoE
            && TechnologyFromAccessAsn(accessAsn, orgName) is { } asnTech)
        {
            State.AccessTechnology = asnTech;
            State.AccessTechnologyInferred = true;
            _logger.LogDebug("Tracer: access technology inferred as {Tech} from access AS{Asn} ({Org})",
                asnTech, accessAsn, orgName);
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
        var target = new ProbeTarget(endpoint.Address, mode, Port: null, SourceInterface: _binding?.Source);
        try
        {
            var result = await _traceExecutor.TracerouteAsync(target, maxHops: 30,
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
                Vantage = _traceExecutor.Vantage,
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
        if (_accessAsn.HasValue) accessAsnNumbers.Add(_accessAsn.Value);

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
        var destinationAsns = _destinationAsns;
        var destinationOrgs = _destinationOrgs;

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
        var asnByIp = BuildAsnByIpMap();
        var traceSequences = BuildTraceSequences();

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
            // hop that responded; candidates come from those responders, clumped
            // below. TCP/443 probing was considered as a fallback for ASNs with
            // no responders but rejected: (1) SYN-probing transit routers from
            // every install looks like scanning to NOCs; (2) transit routers don't
            // serve 443 so a RST doesn't reflect anything real; (3) ACLs drop most
            // of them silently anyway. The path-proxy block below covers unenumerated
            // transit ASNs cleanly by monitoring the CDN destination instead.
            var asn = group.First().Asn!;

            // An ASN typically spans its ingress POP near the access network and,
            // several ms later, a distant egress POP - and across the merged pool
            // of heterogeneous traces only RTT separates those POPs, never hop
            // numbers. Carry the nearest responders as candidates; selection is
            // provisional here (nearest hop) and is refined after the reachability
            // pass, which clusters the ASN's hops by verified RTT and enables the
            // lowest-RTT gate-clearing hop in each cluster.
            var hopsInOrder = group.OrderBy(h => h.HopNumber).Take(MaxTransitCandidatesPerAsn).ToList();
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
                    HopNumber = hop.HopNumber,
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
        // Hop IP -> attributed ASN, from the merged pool, for the "Via" (farthest transit) column.
        var asnByHopIp = _mergedHops
            .Where(h => h.Asn != null)
            .GroupBy(h => h.Address, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Asn!, StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in _traceEndpoints)
        {
            if (endpoint.IsTransitProbe) continue;
            // AWS regionals are persisted per-region by the dedicated block below, not ASN-deduped
            // here (they all share Amazon's ASN, which would collapse them to one target).
            if (endpoint.Label.StartsWith("AWS-", StringComparison.Ordinal)) continue;
            var destAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (destAsn == null) continue;
            if (accessAsnNumbers.Contains(destAsn.Asn)) continue;
            var trace = State.Traces.FirstOrDefault(t =>
                string.Equals(t.CdnEndpoint, endpoint.Address, StringComparison.OrdinalIgnoreCase));
            bool reachedOrTraversed = (trace?.Reached ?? false) || asnsInTrace.Contains(destAsn.Asn);
            if (!reachedOrTraversed) continue;
            if (!pathProxyAsnsSeen.Add(destAsn.Asn)) continue;

            var cdnName = CleanAsnName(destAsn.Name);
            var via = FarthestTransitVia(endpoint.Address, destAsn.Asn, cdnName, accessAsnNumbers, asnByHopIp, out var cdnComplete);
            candidates.Add(new TransitAsnCandidate
            {
                AsnNumber = destAsn.Asn,
                AsnName = cdnName,
                Label = endpoint.Label,
                Method = DiscoveryMethod.PathProxy,
                TargetId = $"path-{endpoint.Label.ToLowerInvariant()}-as{destAsn.Asn}",
                HopAddress = endpoint.Address,
                PathProxyTarget = endpoint.Address,
                RespondedTo = ProbeMode.Icmp,
                Enabled = true,
                ViaAsnNumber = via?.Asn,
                ViaAsnName = via?.Name,
                ViaPathComplete = cdnComplete
            });
        }

        // AWS DynamoDB regionals: each surfaced region is its own path-end target (not ASN-deduped -
        // they all share Amazon's ASN, which would collapse them to one target). The surfaced set is
        // trimmed to the MaxAwsPathEndTargets best: transit-eliciting regions first (surfacing
        // transit is the point), then lowest RTT. Persist the HOSTNAME (AWS rotates the regional
        // IPs; it re-resolves to a live in-region IP each poll) even though the trace/rank used the
        // concrete IP. Pre-enable a region only if its trace crossed a transit ASN; the rest come in
        // disabled for the user to tick on. Toggle survives re-discovery via the address-keyed
        // reconcile below.
        if (_awsRegionals.Count > 0)
        {
            var amazon = await _asnResolution.ResolveAsync(_awsRegionals[0].Ip, ct);
            var amazonAsn = amazon?.Asn ?? 0;
            var amazonName = string.IsNullOrEmpty(amazon?.Name) ? "Amazon" : CleanAsnName(amazon.Name);
            // The via (farthest transit) labels the row and ranks the trim. It also gates pre-enable:
            // a region is pre-checked only when its trace actually crosses transit, and only while
            // the pre-enabled count is under its own cap.
            var rankedAws = _awsRegionals
                .Select(r =>
                {
                    var via = FarthestTransitVia(r.Ip, amazonAsn, amazonName, accessAsnNumbers, asnByHopIp, out var complete);
                    return (Regional: r, Via: via, PathComplete: complete);
                })
                .OrderByDescending(x => x.Via != null)
                .ThenBy(x => x.Regional.RttMs)
                .Take(MaxAwsPathEndTargets)
                .ToList();
            // Pre-enable only the best few. The list is already ordered transit-eliciting first,
            // then by RTT, so taking from the front enables the most useful of them.
            var autoEnabled = 0;
            foreach (var (r, via, awsComplete) in rankedAws)
            {
                var enable = via != null && autoEnabled < MaxAwsAutoEnabledTargets;
                if (enable) autoEnabled++;
                candidates.Add(new TransitAsnCandidate
                {
                    AsnNumber = amazonAsn,
                    AsnName = amazonName,
                    Label = $"AWS {r.Region}",
                    Method = DiscoveryMethod.PathProxy,
                    TargetId = $"aws-{r.Region}",
                    HopAddress = r.Hostname,
                    PathProxyTarget = r.Hostname,
                    RespondedTo = ProbeMode.Icmp,
                    Enabled = enable,
                    ViaAsnNumber = via?.Asn,
                    ViaAsnName = via?.Name,
                    ViaPathComplete = awsComplete
                });
            }
        }

        // Reconcile ALL candidates (transit + path-end) and access hops against
        // existing DB targets. Enabled → pre-check; disabled → uncheck.
        // Absorb descriptive names over numbered fallbacks.
        await using var reconcileDb = await CreateDbAsync(ct);
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
                c.PreservedFromExisting = true;
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

    /// <summary>RTT clumps monitored per transit ASN (near ingress + far egress).</summary>
    internal const int MaxClumpsPerAsn = 2;

    /// <summary>Responding hops carried as candidates per transit ASN.</summary>
    internal const int MaxTransitCandidatesPerAsn = 6;

    /// <summary>Minimum RTT step (ms) that starts a new clump within an ASN's run.</summary>
    internal const double RttClumpStepFloorMs = 2.0;

    /// <summary>
    /// Fractional RTT step that starts a new clump, for high-RTT paths where a few ms
    /// is noise. Kept below the floor's reach until ~20 ms so the 2 ms floor governs
    /// the low-RTT regime - at metro RTTs, anything over 2 ms IS the next POP.
    /// </summary>
    internal const double RttClumpStepFraction = 0.10;

    /// <summary>
    /// Post-verification auto-selection: sorts each transit ASN's gate-clearing hops
    /// by verified RTT and clusters them where the RTT steps up by more than
    /// max(<see cref="RttClumpStepFloorMs"/>, <see cref="RttClumpStepFraction"/> of the
    /// previous hop) - the signature of the long-haul link between two of the ASN's
    /// POPs. Hop numbers are deliberately NOT used for ordering: the candidates come
    /// from the merged pool of heterogeneous traces, whose hop numbers interleave
    /// meaninglessly across paths (the same warning the near-transit window handles
    /// per trace above); RTT is the physical quantity that actually separates POPs.
    /// The lowest-RTT NET-NEW hop in each of the first <see cref="MaxClumpsPerAsn"/>
    /// clusters is enabled. Candidates reconciled from existing targets are never
    /// flipped in either direction: an existing enabled row keeps covering its
    /// cluster (no second pick is added beside it), and an existing disabled row is
    /// never re-enabled - a disabled row can be a flaky-target verdict, not just an
    /// old default. An ASN with no gate-clearing hop ends up with nothing enabled here;
    /// that is fine, because a curated transit ASN (e.g. Level 3) still gets its anycast
    /// witness attached downstream regardless (InjectTransitWitnessesAsync).
    /// </summary>
    internal static void ApplyTransitClumpSelection(IEnumerable<TransitAsnCandidate> candidates)
    {
        var byAsn = candidates
            .Where(c => c.Method == DiscoveryMethod.DirectRouter)
            .GroupBy(c => c.AsnNumber);
        foreach (var asnGroup in byAsn)
        {
            var byRtt = asnGroup
                .Where(c => !c.Unreachable && c.VerifiedRttMs.HasValue)
                .OrderBy(c => c.VerifiedRttMs!.Value)
                .ThenBy(c => c.HopNumber)
                .ToList();

            var clumps = new List<List<TransitAsnCandidate>>();
            foreach (var c in byRtt)
            {
                if (clumps.Count > 0)
                {
                    var prevRtt = clumps[^1][^1].VerifiedRttMs!.Value;
                    if (c.VerifiedRttMs!.Value - prevRtt > Math.Max(RttClumpStepFloorMs, prevRtt * RttClumpStepFraction))
                        clumps.Add(new List<TransitAsnCandidate>());
                }
                else
                {
                    clumps.Add(new List<TransitAsnCandidate>());
                }
                clumps[^1].Add(c);
            }

            var winners = new HashSet<TransitAsnCandidate>();
            foreach (var clump in clumps.Take(MaxClumpsPerAsn))
            {
                if (clump.Any(c => c.PreservedFromExisting && c.Enabled)) continue;
                var winner = clump.FirstOrDefault(c => !c.PreservedFromExisting);
                if (winner != null) winners.Add(winner);
            }

            foreach (var c in asnGroup)
            {
                if (c.PreservedFromExisting) continue;
                c.Enabled = winners.Contains(c);
            }
        }
    }

    // Reachability gate: a candidate must answer enough pings in a short rapid burst (200 ms
    // spacing) to be auto-selected, so flaky, ICMP-deprioritized routers don't get monitored - and
    // so a curated transit witness (e.g. Level 3 4.2.2.2) must itself clear the gate before it is
    // attached. We always send 3; the required successes depend on the connection's access medium
    // (Item B): air-interface mediums (WISP, cellular) allow one dropped reply (2/3), everything
    // else demands 3/3.
    private const int ReachabilityPingCount = 3;

    /// <summary>
    /// Required successful pings (out of <see cref="ReachabilityPingCount"/>) for a candidate to be
    /// auto-selected, by the connection's access technology. WISP / cellular have inherent
    /// air-interface transient loss, so a single drop does not disqualify a candidate there;
    /// everything else (including LEO, which is stable) demands all three.
    ///
    /// Unknown demands all three too. It used to be lenient on the grounds that an unconfigured
    /// link might turn out to be air-interface - but Unknown is the state of every FIRST run on a
    /// WAN, which is precisely the run that decides which candidates get seeded as monitoring
    /// targets. Relaxing the gate exactly when the least is known adopted flaky routers on the
    /// strength of the run with the weakest evidence. Leniency now follows a deliberate statement
    /// about the medium rather than the absence of one: someone on a WISP sets the technology and
    /// re-runs.
    /// </summary>
    private static int RequiredReachabilitySuccesses(AccessTechnology tech) => tech switch
    {
        AccessTechnology.FixedWireless or AccessTechnology.Cellular => 2,
        _ => 3
    };

    /// <summary>
    /// Rapid ping burst used for reachability verification. Bound the same way the traces are on
    /// a context run: a hop reachable on the primary WAN but not out this one must read as
    /// unreachable here, which is the whole point of verifying per WAN.
    /// </summary>
    private Task<PingProbeResult> ProbeReachabilityAsync(string address, ProbeMode mode, CancellationToken ct) =>
        _traceExecutor.PingAsync(new ProbeTarget(address, mode, Port: null, SourceInterface: _binding?.Source),
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
                // Burst MINIMUM, not average: this RTT feeds the POP clustering, and
                // a single queued reply in the average drags a near hop into the far
                // cluster (observed: a 11.3 ms hop measuring 14.7 avg bridged two
                // POPs). The minimum is the standard path-distance estimator.
                t.ApplyRtt(result.RttMinMs ?? result.RttAvgMs);
            }
            else
            {
                t.MarkUnreachable();
                unreachable++;
                _logger.LogDebug("Ping check {Recv}/{Sent} for {Address} - below {Min} required, marked unreachable",
                    result.Received, result.Sent, t.Address, minSuccesses);
            }
        }

        // With verified RTTs in hand, refine the provisional per-clump picks: enable
        // the lowest-RTT hop that cleared the gate in each of an ASN's clumps (near
        // ingress + far egress), instead of blindly keeping the lowest hop number.
        ApplyTransitClumpSelection(State.TransitAsns);

        // Item A: whenever a curated transit ASN (e.g. Level 3 / AS3356) is near-transit, attach its
        // anycast witness (4.2.2.2) as a transit target - alongside any real routers, not only as a
        // fallback - so ISP Health always has a stable Lumen transit anchor even when the real hops
        // vary POP-to-POP across runs or start deprioritizing ICMP.
        await InjectTransitWitnessesAsync(minSuccesses, ct);

        // If the access ASN is one we have curated endpoints for and none of its first-mile
        // hops cleared the gate, adopt the lowest-RTT reachable curated endpoint as the access target.
        await InjectAccessIspFallbackAsync(minSuccesses, ct);

        State.CurrentActivity = unreachable > 0
            ? $"Reachability check complete: {unreachable} of {allTargets.Count} target(s) did not respond and were excluded."
            : $"All {allTargets.Count} target(s) responded to ping.";
    }

    // Item A: anycast DNS witnesses for transit ASNs whose routers commonly ICMP-deprioritize or
    // hide behind L2-transparent infra. Attached whenever the ASN is genuinely near-transit - not
    // only as a fallback - so the tier always has a stable anchor even when real routers respond
    // (they vary POP-to-POP across runs and can start dropping ICMP). The endpoint is anycast
    // (nearest edge), hence the "transit witness" label; it hits a more distant POP than the closest
    // real hop, which is why the real hops are kept, never replaced. Extend this table to add
    // witnesses for other transit ASNs (e.g. AS7018 AT&T).
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
            // Skip only if this witness address is already a candidate this run. A real router in the
            // same ASN no longer suppresses the witness - the anycast anchor is attached alongside it
            // (the real hops are usually a closer POP, so both are kept). Same-address dedup on write
            // (UpsertTransitTargetAsync) folds this into any hand-added row at the same address.
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
            _logger.LogInformation("Attached transit witness {Address} (AS{Asn} {Name}) - near-transit anchor",
                address, asn, name);

            // Trace the witness so PersistHopOrderAsync records the real transit hops that precede
            // it as its ancestry. Without this the anycast endpoint is ping-only, lands in
            // UpstreamDiscoveries with no ancestors, and fails FarClusterRoutesThroughNear - so ISP
            // Health can never use the witness's clean end-to-end jitter to absolve a jittery near
            // hop it routes through. The proof is honest: ancestry comes from 4.2.2.2's actual path,
            // so an absolve only happens when that path genuinely traverses the monitored hop; if
            // the anycast lands on a different Level 3 POP there is no overlap and no absolve.
            var (_, witnessTrace) = await TraceOneAsync(new TraceEndpoint(name, address), ProbeMode.Icmp, ct);
            _lastTraces.Add(witnessTrace);
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

        var orgName = _accessAsnName ?? $"AS{asn}";

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
    ///
    /// The PppoE branch here (and in <see cref="InferL2NeighborRole"/> and
    /// <see cref="LabelL2Role"/>) is where PPPoE splits behavior today; ISP Health scoring is
    /// expected to split on it too once there is field data to calibrate against. Neither split
    /// needs the stored value, which is why PPPoE left the Upstream Discovery selector: a PPPoE
    /// WAN announces itself in the gateway interface name (uplink_ifname "ppp0", already read
    /// into <see cref="_wanUplinkIfName"/>) and in the network config's wan_type "pppoe". Drive
    /// the BNG label off that, and the user is left picking only the medium PPPoE rides - the
    /// two are independent facts and both are then available to score on.
    /// </summary>
    /// <param name="isFirstMile">
    /// Whether this is the nearest access hop we found. The vendor evidence describes the box on
    /// the other end of the WAN and nothing beyond it, so it may only name that one. By nearest
    /// rather than by TTL: the first-mile device is hop 1 from a gateway vantage and hop 2 or 3
    /// from a LAN one, and the same box should not change role with the vantage.
    /// </param>
    private static UpstreamRole InferAccessRole(
        AttributedHop hop, AccessTechnology tech, string? ouiVendor, bool isFirstMile)
    {
        var vendor = ouiVendor?.ToLowerInvariant() ?? string.Empty;
        // Known OLT/PON vendors. Adtran for tier-2/3 US telcos, Ubiquiti for UISP-Fiber
        // (UF-OLT line), DZS/Dasan for Tier-3 fiber overbuilds, plus the global majors.
        var isOltVendor = vendor.Contains("calix") || vendor.Contains("nokia") || vendor.Contains("huawei")
                          || vendor.Contains("zte") || vendor.Contains("alcatel") || vendor.Contains("adtran")
                          || vendor.Contains("ubiquiti") || vendor.Contains("dzs") || vendor.Contains("dasan");
        var isCmtsVendor = vendor.Contains("arris") || vendor.Contains("commscope") || vendor.Contains("casa")
                           || vendor.Contains("cadant") || vendor.Contains("ubr");

        if (!isFirstMile) return UpstreamRole.Aggregation;
        if ((tech == AccessTechnology.Gpon || tech == AccessTechnology.XgsPon) && isOltVendor)
            return UpstreamRole.Bng;
        if (tech == AccessTechnology.Docsis && (isCmtsVendor || hop.HopNumber == 1))
            return UpstreamRole.Cmts;
        if (tech == AccessTechnology.PppoE)
            return UpstreamRole.Bng;
        return UpstreamRole.Aggregation;
    }

    /// <summary>
    /// The access technology an L2 neighbor's OUI vendor points at, or null when it points
    /// at nothing useful. Only consulted for a WAN with no technology set, and the user can
    /// change it in one click, so a well-founded lean beats leaving it Unknown - Unknown
    /// makes the reachability gate and every role label fall back to generic.
    ///
    /// Still excluded are the vendors whose OUI spans far more than access gear: Ubiquiti
    /// (UISP-Fiber OLTs, airMAX fixed wireless, and ordinary ISP routers) and Cisco (the
    /// uBR/cBR CMTS line shares an OUI with every switch and router they ship). Those are
    /// splits with no majority, unlike the CMTS and PON vendors below.
    /// </summary>
    internal static AccessTechnology? TechnologyFromVendor(string? ouiVendor)
    {
        if (string.IsNullOrWhiteSpace(ouiVendor)) return null;
        var vendor = ouiVendor.ToLowerInvariant();

        // CMTS/HFC vendors. Cadant (the C4, still shipping under Arris/CommScope but on the
        // original OUI), Vecima, Harmonic and Teleste build access gear for cable and
        // nothing else. Arris/CommScope and Casa do also sell PON OLTs, but their CMTS
        // install base dwarfs it, so cable is the right lean on their OUI alone.
        if (vendor.Contains("cadant") || vendor.Contains("vecima") || vendor.Contains("harmonic")
            || vendor.Contains("teleste") || vendor.Contains("arris") || vendor.Contains("commscope")
            || vendor.Contains("casa"))
            return AccessTechnology.Docsis;

        // These vendors ship both PON and DSL, but one of their boxes terminating a WAN is
        // an OLT far more often than a DSLAM - DSL is a shrinking minority, and a DSL line
        // usually presents PPPoE rather than the DSLAM as the L2 neighbor. GPON rather than
        // XGS-PON: it is the larger install base, and the two differ only in thresholds.
        if (vendor.Contains("calix") || vendor.Contains("nokia") || vendor.Contains("huawei")
            || vendor.Contains("zte") || vendor.Contains("alcatel") || vendor.Contains("adtran")
            || vendor.Contains("dzs") || vendor.Contains("dasan"))
            return AccessTechnology.Gpon;

        return null;
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
    /// <summary>
    /// What this WAN's traffic costs, from its access technology and whether Data Usage has a cap
    /// configured for it. Any cap above zero counts: setting one is the operator saying the link is
    /// metered, whatever the toggle beside it is doing.
    /// </summary>
    private async Task<MeteredProbePolicy.Plan> ResolveProbePlanAsync(
        NetworkOptimizerDbContext db, string wanInterface, CancellationToken ct)
    {
        var metered = false;
        try
        {
            var key = GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface);
            var configs = await db.WanDataUsageConfigs.AsNoTracking()
                .Where(c => c.DataCapGb > 0)
                .Select(c => c.WanKey)
                .ToListAsync(ct);
            metered = configs.Any(k => string.Equals(
                GatewayWanHelper.WanInterfaceKeyFromKey(k), key, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            // Unreadable config is not evidence of a cap: probe as normal rather than quietly
            // throttling a link that may have none.
            _logger.LogDebug(ex, "Could not read Data Usage config for {Wan}; probing unmetered", wanInterface);
        }
        return MeteredProbePolicy.For(State.AccessTechnology, metered);
    }

    /// <summary>
    /// Leaves only a metered WAN's budget of candidates ticked, nearest first. Access hops before
    /// transit: they are the ISP's own first mile, the fewest, and the ones whose loss is the ISP's
    /// to answer for. Nothing is removed and nothing is disabled that the operator ticked - this
    /// only decides what arrives ticked, and the review is still theirs to change.
    /// </summary>
    internal static void ApplyAutoEnableBudget(UpstreamTracerState state, int? budget)
    {
        if (budget is not int max) return;

        // Three buckets, taken one at a time in rotation. Access hops used to be taken first and
        // in full, which on a first mile that answers with a dozen ECMP addresses spent nearly the
        // whole allowance before the other two were reached - one internet target survived, so the
        // site could see its access cloud in detail and could not tell whether anything it
        // actually reaches was up. Each bucket answers a different question: an access hop says
        // whether the ISP's own first mile is at fault, a transit hop says which upstream is, and
        // a path endpoint says whether any of it is reaching the things people use. A budget that
        // buys depth in one of them measures a fraction of the path.
        // Candidates the reachability gate already rejected are not in the running. This runs
        // AFTER that gate, and switching one back on because it happened to fall inside the
        // budget hands the operator a target that is known not to answer - and spends one of the
        // few slots a metered WAN gets doing it. Only reachable candidates are considered, and
        // the rejected ones keep the Enabled=false the gate gave them.
        var buckets = new List<Action<bool>>[]
        {
            state.AccessHops.Where(h => !h.Unreachable).OrderBy(h => h.HopNumber)
                .Select(h => (Action<bool>)(on => h.Enabled = on)).ToList(),
            state.TransitAsns.Where(t => t.Method != DiscoveryMethod.PathProxy && !t.Unreachable)
                .Select(t => (Action<bool>)(on => t.Enabled = on)).ToList(),
            state.TransitAsns.Where(t => t.Method == DiscoveryMethod.PathProxy && !t.Unreachable)
                .Select(t => (Action<bool>)(on => t.Enabled = on)).ToList(),
        };

        var cursors = new int[buckets.Length];
        var remaining = max;
        bool tookAny;
        do
        {
            tookAny = false;
            for (var b = 0; b < buckets.Length && remaining > 0; b++)
            {
                if (cursors[b] >= buckets[b].Count) continue;
                buckets[b][cursors[b]++](true);
                remaining--;
                tookAny = true;
            }
        }
        while (tookAny && remaining > 0);

        // Whatever the rotation did not reach is left off - within a bucket that is its own order,
        // so the nearest access hops and the first-listed endpoints are the ones kept.
        for (var b = 0; b < buckets.Length; b++)
            for (var i = cursors[b]; i < buckets[b].Count; i++)
                buckets[b][i](false);
    }

    public async Task CommitResultsAsync(CancellationToken ct = default)
    {
        if (State.Step != TracerStep.ReviewingResults) return;

        await using var db = await CreateDbAsync(ct);

        // Scope all writes to the WAN this discovery ran against. Multi-WAN setups
        // get one row in MonitoringTargets per (target, wan) and one row in
        // WanDiscoveryContexts per WAN.
        var wanInterface = _binding?.WanInterface ?? State.WanInterface ?? "wan";
        // A context run's targets carry both keys: the WAN says where the data belongs, the
        // context says who probes them. Setting them together is what closes the gap where a
        // context's targets had a context but no WAN, so no per-WAN reader could find them.
        var wanContextId = _binding?.WanContextId;
        // Unpinned rows are the unbound run's alone - see OwnsTargetRow.
        var isUnboundRun = _binding == null;

        // What this WAN's probing costs. Targets are created at the plan's cadence, and on a
        // metered WAN the ones already here are slowed to match - a link that has just been
        // declared metered is exactly the one whose existing targets are the problem.
        var probePlan = await ResolveProbePlanAsync(db, wanInterface, ct);

        // A confirmed provider change resets the connection's upstream monitoring wholesale:
        // pause every enabled access/transit/path target - auto-discovered and hand-added alike
        // (manual rows pinned to another WAN survive) - and wipe the WAN's off-path evidence,
        // so the freshly discovered candidates upserted below become the new baseline. Runs
        // before the upserts so the fresh targets come back enabled. A decline records the new
        // ASN so the same change doesn't re-prompt every run.
        if (State.IspChange is { Confirmed: true } confirmedChange)
        {
            var paused = await UpstreamRediscoveryService.ApplyIspChangeResetAsync(db, wanInterface, ct);
            _logger.LogInformation("Commit: ISP change AS{Old} -> AS{New} confirmed; paused {Count} upstream target(s) on {Wan} for a fresh baseline",
                confirmedChange.OldAsnNumber, confirmedChange.NewAsnNumber, paused, wanInterface);
        }
        else if (State.IspChange is { Confirmed: false } declinedChange)
        {
            await UpstreamRediscoveryService.RecordDeclinedIspChangeAsync(db, wanInterface, declinedChange.NewAsnNumber, ct);
            _logger.LogInformation("Commit: ISP change AS{Old} -> AS{New} declined; keeping existing targets",
                declinedChange.OldAsnNumber, declinedChange.NewAsnNumber);
        }

        foreach (var hop in State.AccessHops.Where(h => h.Enabled))
        {
            _logger.LogDebug("Commit access hop: id={TargetId} label='{Label}' addr={Address}", hop.TargetId, hop.Label, hop.Address);
            await UpsertTargetAsync(db, hop, wanInterface, wanContextId, ct, probePlan.PollIntervalSeconds, isUnboundRun);
        }
        foreach (var hop in State.AccessHops.Where(h => !h.Enabled))
        {
            // With per-WAN twin rows an address can have several rows; pause only the one this
            // WAN owns (another WAN's row - and its measuring - is that WAN's to manage).
            var existing = (await db.MonitoringTargets.Where(t => t.Address == hop.Address).ToListAsync(ct))
                .FirstOrDefault(t => OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun));
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
            await UpsertTransitTargetAsync(db, transit, wanInterface, wanContextId, ct,
                pollIntervalSeconds: probePlan.PollIntervalSeconds, isUnboundRun: isUnboundRun);
        }
        foreach (var transit in State.TransitAsns.Where(t => !t.Enabled))
        {
            // Path-end Internet targets (AWS regionals, CDN/DNS) the user left unchecked are saved as
            // PAUSED rows - so the decline sticks (re-discovery reconciles against the disabled row
            // instead of re-suggesting them checked) and they appear in the target list to enable
            // later. Transit ASNs stay on their off-path / miss-counter mechanism (update-only).
            if (transit.Method == DiscoveryMethod.PathProxy)
            {
                await UpsertTransitTargetAsync(db, transit, wanInterface, wanContextId, ct, enabled: false,
                    pollIntervalSeconds: probePlan.PollIntervalSeconds, isUnboundRun: isUnboundRun);
                continue;
            }
            var addr = transit.HopAddress ?? transit.PathProxyTarget;
            if (string.IsNullOrEmpty(addr)) continue;
            // Same per-WAN row selection as the access-hop pause above.
            var existing = (await db.MonitoringTargets.Where(t => t.Address == addr).ToListAsync(ct))
                .FirstOrDefault(t => OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun));
            if (existing != null)
            {
                existing.Enabled = false;
                existing.Name = transit.Label ?? transit.AsnName;
                if (!string.IsNullOrEmpty(transit.AsnName)) existing.AsnName = transit.AsnName;
            }
        }

        // Confirmed off-path transit ASNs the user didn't re-check: pause every enabled Transit
        // target in the ASN - auto-discovered AND hand-added - since the ISP no longer routes
        // through it, so they're false targets skewing ISP Health. Paused (Enabled=false), never
        // deleted. Auto targets are scoped to this WAN; UserProvided ones are WAN-agnostic.
        if (State.RemovedTransitAsns.Count > 0)
        {
            foreach (var removed in State.RemovedTransitAsns.Where(r => !r.Keep))
            {
                var stale = await db.MonitoringTargets
                    .Where(t => t.TargetType == MonitoringTargetType.Transit && t.Enabled
                        && t.AsnNumber == removed.AsnNumber
                        && (t.DiscoveryMethod == DiscoveryMethod.UserProvided || t.WanInterface == wanInterface))
                    .ToListAsync(ct);
                foreach (var t in stale) t.Enabled = false;
                if (stale.Count > 0)
                    _logger.LogInformation("Commit: paused {Count} off-path transit target(s) for AS{Asn} ({Name})",
                        stale.Count, removed.AsnNumber, removed.AsnName);
            }

            // Clear the surfaced ASNs' miss counters - kept AND paused. A kept ASN would otherwise
            // still sit at the confirm threshold and re-flag review on the next daily recheck;
            // clearing makes its off-path evidence re-accumulate from zero instead.
            await UpstreamRediscoveryService.ClearMissCountKeysAsync(db, wanInterface,
                State.RemovedTransitAsns.Select(r => "transit:as" + r.AsnNumber), ct);
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
        // Never write Unknown over a saved technology: the ISP Health selector writes
        // to this row too, and a run that started without the tech hydrated would
        // otherwise wipe the user's setting (discovery only ever proposes, per the
        // SetAccessTechnologyAsync contract).
        if (State.AccessTechnology != AccessTechnology.Unknown)
            ctxRow.AccessTechnology = State.AccessTechnology;
        ctxRow.LastDiscoveryAt = DateTime.UtcNow;
        ctxRow.NeedsReview = false;
        ctxRow.UpdatedAt = DateTime.UtcNow;

        // MonitoringSettings holds the LEGACY single-WAN timestamp and review flag, which the
        // primary run owns. A context run must leave them alone: clearing the review flag here
        // would dismiss a pending review of the primary WAN that nobody has looked at.
        var settings = _binding == null ? await db.MonitoringSettings.FirstOrDefaultAsync(ct) : null;
        if (settings != null)
        {
            settings.LastUpstreamDiscoveryAt = DateTime.UtcNow;
            settings.UpstreamDiscoveryNeedsReview = false;
            settings.UpdatedAt = DateTime.UtcNow;
        }


        // Slow what is already here to match. A WAN only reaches a rung by being declared metered
        // or by its technology, and in both cases the targets already probing it are the cost -
        // creating new ones at the right cadence while the old ones keep running at 10s would fix
        // nothing. Fabric targets never leave the WAN, so they are left alone; so is anything
        // already slower than the plan, which is a deliberate choice of the operator's.
        if (probePlan.Rung > 0)
        {
            var candidates = await db.MonitoringTargets
                .Where(t => t.TargetType != MonitoringTargetType.Fabric
                    && t.PollIntervalSeconds < probePlan.PollIntervalSeconds)
                .ToListAsync(ct);
            var repaced = SelectTargetsToRepace(
                candidates, probePlan.PollIntervalSeconds, wanInterface, isUnboundRun);
            foreach (var target in repaced) target.PollIntervalSeconds = probePlan.PollIntervalSeconds;
            // Logged even at zero: the count is how an operator confirms this WAN reached only its
            // own rows, which is the whole of the fix. Silence would look the same as not running.
            _logger.LogInformation(
                "Metered WAN {Wan} ({Unbound}): slowed {Count} of {Considered} faster target(s) to {Interval}s",
                wanInterface, isUnboundRun ? "unbound run, owns unpinned rows" : "bound run, its own rows only",
                repaced.Count, candidates.Count, probePlan.PollIntervalSeconds);
        }

        await db.SaveChangesAsync(ct);

        // Persist same-path hop ordering so ISP Health can confirm a farther transit
        // cluster routes through a nearer one before assimilating its jitter.
        await PersistHopOrderAsync(db, wanInterface, ct);

        // Drop the ISP Health cache so the "re-run discovery" banner clears on the next tab
        // view without a manual refresh - the freshly committed ancestry is now in the DB.
        //
        // Every WAN of the site: this run commits targets for the WAN it was bound to, which is
        // usually NOT the primary, and the injected instance always is. Invalidating that alone
        // left the report for the very WAN just discovered showing its pre-discovery state.
        _ispHealthRegistry.InvalidateSite(_siteSlug);

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
        // AWS regionals are persisted by hostname but were traced by IP, so the trace's AWS-endpoint
        // hop (an IP) isn't recognized as monitored above. Register the traced IPs so the ancestry
        // pass records the access/transit hops upstream of each AWS region; the persist loop below
        // resolves each AWS target's ancestry via that IP.
        foreach (var r in _awsRegionals)
            if (!string.IsNullOrEmpty(r.Ip)) monitoredAddrs.Add(r.Ip);

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
            // AWS regionals: resolve ancestry/hop distance via the IP we actually traced this run,
            // not the persisted hostname (which the trace never saw).
            var lookupAddr = _awsRegionals.FirstOrDefault(r => string.Equals(r.Hostname, t.Address, StringComparison.OrdinalIgnoreCase))?.Ip
                ?? t.Address;
            var ancestors = ancestorsByIp.TryGetValue(lookupAddr, out var anc)
                ? anc.OrderBy(a => a).ToList()
                : new List<string>();
            db.UpstreamDiscoveries.Add(new UpstreamDiscovery
            {
                MonitoringTargetId = t.Id,
                AsnNumber = t.AsnNumber!.Value,
                AsnName = t.AsnName,
                HopIp = t.Address,
                HopNumber = minTtlByIp.TryGetValue(lookupAddr, out var ttl) ? ttl : 0,
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

    /// <summary>
    /// Whether this run may write to an existing target row. A row already homed on a DIFFERENT
    /// WAN belongs to that WAN's discovery: letting each run re-home it would have the two
    /// trading it back and forth every cycle - and would let one WAN's run pause a target the
    /// other WAN is measuring. A run that finds an address claimed by another WAN creates its
    /// OWN row for it instead (see <see cref="WanQualifiedTargetId"/>), so the same host is
    /// probed from every WAN that discovers it and each WAN's series stay separable. A row with
    /// no WAN yet is unclaimed and adoptable, which is how every pre-existing row behaves on a
    /// single-WAN install: there, this is always true and nothing changes.
    /// </summary>
    /// <param name="rowWanInterface">The WAN currently stamped on the row, if any.</param>
    /// <param name="wanInterface">The WAN this discovery run is committing.</param>
    /// <summary>
    /// The discovery-context row a tracer rehydrates from. A bound (context) tracer takes
    /// exactly its own WAN's row. The primary tracer takes the CONFIGURED primary's row when
    /// the console answered (primary is a role - any wanN group can hold it); with no console
    /// answer it falls back to a documented GUESS: the conventional "wan" row first, then the
    /// most recently discovered. That guess is wrong exactly on an offline site whose
    /// configured primary is not the "wan" group - acceptable only because there is nothing
    /// better to ask, and the next connected rehydrate corrects it. Keys normalized
    /// ("wan1" == "wan").
    /// </summary>
    internal static WanDiscoveryContext? PickRehydrateContext(
        IReadOnlyList<WanDiscoveryContext> contexts, string? boundWanInterface, string? configuredPrimaryKey)
    {
        static string Norm(string? k) => string.IsNullOrEmpty(k)
            ? "" : NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(k);
        if (!string.IsNullOrEmpty(boundWanInterface))
            return contexts.FirstOrDefault(c => Norm(c.WanInterface) == Norm(boundWanInterface));
        if (!string.IsNullOrEmpty(configuredPrimaryKey))
        {
            var configured = contexts.FirstOrDefault(c => Norm(c.WanInterface) == Norm(configuredPrimaryKey));
            if (configured != null) return configured;
        }
        return contexts
            .OrderBy(c => Norm(c.WanInterface) == "wan" ? 0 : 1)
            .ThenByDescending(c => c.LastDiscoveryAt ?? c.UpdatedAt)
            .FirstOrDefault();
    }

    /// <summary>
    /// The targets a metered WAN's commit slows: rows it owns that still probe faster than the
    /// plan. Cadence is not indexed by WAN, so the read is site-wide and ownership is the only
    /// thing keeping a metered WAN off other WANs' targets - and off LAN ones, which never touch
    /// its data plan. Fabric never leaves the WAN; anything already slower is the operator's call.
    /// </summary>
    internal static List<MonitoringTarget> SelectTargetsToRepace(
        IEnumerable<MonitoringTarget> candidates, int pollIntervalSeconds, string wanInterface, bool isUnboundRun) =>
        candidates
            .Where(t => t.TargetType != MonitoringTargetType.Fabric
                && t.PollIntervalSeconds < pollIntervalSeconds
                && OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun))
            .ToList();

    /// <summary>
    /// Whether a target row belongs to the WAN this discovery run is committing for. Reading an
    /// unpinned row as "owned by whichever WAN is committing" let a metered secondary adopt the
    /// site's hand-added targets and slow every one of them.
    /// </summary>
    /// <param name="rowWanInterface">The row's WAN, or unpinned.</param>
    /// <param name="wanInterface">The WAN this run is committing for.</param>
    /// <param name="isUnboundRun">
    /// Whether this run has no <see cref="WanProbeBinding"/>, which is the only thing that owns
    /// unpinned rows. Identity, not inference: a secondary is only ever traced through a context,
    /// so a bound run is never the unpinned probes'. Deliberately avoids resolving a primary WAN
    /// key, which no console-free path can know and which is not "wan" - any group holds the role.
    /// </param>
    internal static bool OwnsTargetRow(string? rowWanInterface, string wanInterface, bool isUnboundRun = true)
    {
        if (MonitoringTarget.IsUnpinned(rowWanInterface)) return isUnboundRun;
        // Normalized ("wan1" == "wan"): legacy rows stamped with the wan1 alias are the SAME
        // WAN as a "wan" run, not a rival - unnormalized, every re-run on such an install
        // would twin its own targets.
        return string.Equals(
            NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(rowWanInterface!),
            NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The WAN-qualified target id for this WAN's twin of a host another WAN's discovery already
    /// claimed. MonitoringTarget.TargetId is unique (and is the Influx target_id tag), so a host
    /// reached from several WANs - a core resolver, a shared ISP hop - gets one row PER WAN: the
    /// first WAN keeps the base id (existing installs and their history unchanged), every later
    /// WAN gets "{baseId}@{wanKey}". Distinct ids keep result routing and the per-target Influx
    /// series unambiguous with zero read-side cost; cross-WAN "same host" linkage for comparison
    /// views is by <see cref="MonitoringTarget.Address"/> (twins share it).
    /// </summary>
    internal static string WanQualifiedTargetId(string baseTargetId, string wanInterface)
        => $"{baseTargetId}@{NetworkOptimizer.UniFi.GatewayWanHelper.WanInterfaceKeyFromKey(wanInterface)}";

    /// <summary>
    /// Creates or re-validates the monitoring target for a discovered access hop, stamped with
    /// the WAN it was discovered on and - on a context run - the context whose agent probes it.
    /// Test-visible (internal, see InternalsVisibleTo) because the double stamping and the
    /// leave-another-WAN's-row-alone rule are the whole of per-WAN discovery's write side.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="hop">The discovered hop.</param>
    /// <param name="wanInterface">WAN this discovery ran against.</param>
    /// <param name="wanContextId">Context this run belongs to, or null for the primary run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="pollIntervalSeconds">Cadence new rows are created at.</param>
    /// <param name="isUnboundRun">Whether this is the unbound (primary) run - see OwnsTargetRow.</param>
    internal static async Task UpsertTargetAsync(NetworkOptimizerDbContext db, AccessHopCandidate hop, string wanInterface, int? wanContextId, CancellationToken ct, int pollIntervalSeconds = MeteredProbePolicy.DefaultIntervalSeconds, bool isUnboundRun = true)
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

        // This WAN's own row for the hop: the base id where this WAN owns it (or it is
        // unclaimed), this WAN's twin, or any row for the address this WAN owns. When the
        // address is claimed by ANOTHER WAN, this run creates its own WAN-qualified twin so
        // the host is probed from both WANs with separable series (see WanQualifiedTargetId).
        var twinId = WanQualifiedTargetId(hop.TargetId, wanInterface);
        var rows = await db.MonitoringTargets
            .Where(t => t.TargetId == hop.TargetId || t.TargetId == twinId || t.Address == hop.Address)
            .ToListAsync(ct);
        var existing = rows.FirstOrDefault(t => t.TargetId == hop.TargetId && OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun))
            ?? rows.FirstOrDefault(t => t.TargetId == twinId)
            ?? rows.FirstOrDefault(t => OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun));
        var claimedByOtherWan = existing == null && rows.Count > 0;
        if (existing == null)
        {
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = claimedByOtherWan ? twinId : hop.TargetId,
                Name = hop.Label,
                Address = hop.Address,
                ProbeMode = hop.RespondedTo,
                DiscoveredProbeMode = hop.RespondedTo,
                TargetType = MonitoringTargetType.AccessIsp,
                AsnNumber = hop.AsnNumber,
                AsnName = CleanAsnName(hop.AsnName),
                VantagePoint = "server",
                PollIntervalSeconds = pollIntervalSeconds,
                PingCount = 5,
                Enabled = true,
                AutoDiscovered = true,
                DiscoveryMethod = hop.Method,
                WanInterface = wanInterface,
                WanContextId = wanContextId,
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
            // Written, never cleared: a target the user assigned to a context by hand keeps
            // that assignment when the primary run re-verifies it.
            if (wanContextId != null) existing.WanContextId = wanContextId;
            existing.Name = hop.Label;
            if (hop.AsnNumber.HasValue) existing.AsnNumber = hop.AsnNumber;
            if (!string.IsNullOrEmpty(hop.AsnName)) existing.AsnName = CleanAsnName(hop.AsnName);
            if (!string.IsNullOrEmpty(hop.PtrHostname)) existing.PtrHostname = hop.PtrHostname;
            existing.LastVerified = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Creates or re-validates the monitoring target for a discovered transit ASN hop or path-end
    /// host, with the same WAN + context stamping and same-WAN ownership rule as
    /// <see cref="UpsertTargetAsync"/>. Test-visible for the same reason.
    /// </summary>
    /// <param name="db">The site's database.</param>
    /// <param name="transit">The discovered transit candidate.</param>
    /// <param name="wanInterface">WAN this discovery ran against.</param>
    /// <param name="wanContextId">Context this run belongs to, or null for the primary run.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="enabled">Whether the target is committed enabled (a declined path-end is saved paused).</param>
    /// <param name="pollIntervalSeconds">Cadence new rows are created at.</param>
    /// <param name="isUnboundRun">Whether this is the unbound (primary) run - see OwnsTargetRow.</param>
    internal static async Task UpsertTransitTargetAsync(NetworkOptimizerDbContext db, TransitAsnCandidate transit, string wanInterface, int? wanContextId, CancellationToken ct, bool enabled = true, int pollIntervalSeconds = MeteredProbePolicy.DefaultIntervalSeconds, bool isUnboundRun = true)
    {
        if (transit.Method == DiscoveryMethod.Unresolved || string.IsNullOrEmpty(transit.TargetId)) return;

        var targetType = transit.Method == DiscoveryMethod.PathProxy
            ? MonitoringTargetType.InternetService
            : MonitoringTargetType.Transit;

        var address = transit.HopAddress ?? transit.PathProxyTarget;
        // Same twin rule as UpsertTargetAsync: a host another WAN's discovery already claimed
        // gets this WAN's own WAN-qualified row, so both WANs probe it with separable series.
        var twinId = WanQualifiedTargetId(transit.TargetId, wanInterface);
        var rows = await db.MonitoringTargets
            .Where(t => t.TargetId == transit.TargetId || t.TargetId == twinId
                || (address != null && t.Address == address))
            .ToListAsync(ct);
        var existing = rows.FirstOrDefault(t => t.TargetId == transit.TargetId && OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun))
            ?? rows.FirstOrDefault(t => t.TargetId == twinId)
            ?? rows.FirstOrDefault(t => OwnsTargetRow(t.WanInterface, wanInterface, isUnboundRun));
        var claimedByOtherWan = existing == null && rows.Count > 0;
        if (existing == null)
        {
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = claimedByOtherWan ? twinId : transit.TargetId,
                Name = transit.Label ?? transit.AsnName,
                Address = transit.HopAddress ?? transit.PathProxyTarget ?? "0.0.0.0",
                ProbeMode = transit.RespondedTo ?? NetworkOptimizer.Core.Enums.ProbeMode.Icmp,
                DiscoveredProbeMode = transit.RespondedTo,
                TargetType = targetType,
                AsnNumber = transit.AsnNumber,
                AsnName = transit.AsnName,
                VantagePoint = "server",
                PollIntervalSeconds = pollIntervalSeconds,
                PingCount = 5,
                Enabled = enabled,
                PtrHostname = transit.HopHostname,
                AutoDiscovered = true,
                DiscoveryMethod = transit.Method,
                WanInterface = wanInterface,
                WanContextId = wanContextId,
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            existing.Enabled = enabled;
            existing.Name = transit.Label ?? transit.AsnName;
            existing.Address = transit.HopAddress ?? transit.PathProxyTarget ?? existing.Address;
            existing.ProbeMode = transit.RespondedTo ?? existing.ProbeMode;
            if (!string.IsNullOrEmpty(transit.HopHostname)) existing.PtrHostname = transit.HopHostname;
            existing.DiscoveryMethod = transit.Method;
            existing.WanInterface = wanInterface;
            if (wanContextId != null) existing.WanContextId = wanContextId;
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

    /// <summary>Hop address → ASN for every ASN-attributed hop in the last sweep.</summary>
    private Dictionary<string, int> BuildAsnByIpMap()
    {
        var asnByIp = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in _mergedHops)
            if (h.Asn != null) asnByIp.TryAdd(h.Address, h.Asn.Asn);
        return asnByIp;
    }

    /// <summary>Per-trace responding hop addresses in hop order, from the last sweep.</summary>
    private List<IReadOnlyList<string>> BuildTraceSequences() =>
        _lastTraces
            .Select(t => (IReadOnlyList<string>)t.Hops
                .Where(hp => hp.Responded && !string.IsNullOrEmpty(hp.Address))
                .OrderBy(hp => hp.HopNumber)
                .Select(hp => hp.Address!)
                .ToList())
            .ToList();

    /// <summary>
    /// Resolves the ASN + org name of every non-transit-probe endpoint in the rotation
    /// into <see cref="_destinationAsns"/> / <see cref="_destinationOrgs"/>. Called once
    /// per run from the access step; the transit step reuses the sets.
    /// </summary>
    private async Task ResolveDestinationAsnsAsync(CancellationToken ct)
    {
        _destinationAsns.Clear();
        _destinationOrgs.Clear();
        foreach (var endpoint in _traceEndpoints)
        {
            if (endpoint.IsTransitProbe) continue;
            var destAsn = await _asnResolution.ResolveAsync(endpoint.Address, ct);
            if (destAsn == null) continue;
            _destinationAsns.Add(destAsn.Asn);
            if (!string.IsNullOrWhiteSpace(destAsn.Name)) _destinationOrgs.Add(destAsn.Name.Trim());
        }
    }

    /// <summary>
    /// Picks the access ISP's ASN from the discovery sweep. The naive "first hop with a
    /// resolvable ASN" heuristic fails when the ISP's entire first mile is unattributable:
    /// Bell Canada (#984) runs its PPPoE aggregation on RFC1918 plus public /16s it
    /// deliberately does not announce in BGP, so the first attributable hop was the probed
    /// CDN's own edge and Cloudflare got crowned as the access ISP.
    ///
    /// Two signals replace it:
    ///  1. The WAN IP's own ASN (the ISP's customer allocation) wins whenever it is
    ///     resolvable AND corroborated - it appears on some hop, or no per-trace votes
    ///     exist at all (fully silent first mile: keep it for labeling and the curated
    ///     fallback endpoints).
    ///  2. Otherwise per-trace voting: each trace nominates its first ASN-attributed hop,
    ///     skipping destination ASNs - a destination's edge only appears on traces toward
    ///     itself, while the true access ISP fronts essentially every trace. Most votes
    ///     wins; ties break toward the ASN seen at the shallowest hop, then lowest ASN.
    /// A destination ASN can still win via signal 1: an access ISP could legitimately be
    /// one of the orgs we probe.
    /// </summary>
    internal static int? DetermineAccessAsn(
        IEnumerable<IReadOnlyList<string>> traceAddressSequences,
        IReadOnlyDictionary<string, int> asnByIp,
        int? wanIpAsn,
        IReadOnlySet<int> destinationAsns)
    {
        var votes = new Dictionary<int, (int Count, int BestDepth)>();
        foreach (var trace in traceAddressSequences)
        {
            var depth = 0;
            foreach (var address in trace)
            {
                depth++;
                if (!asnByIp.TryGetValue(address, out var asn)) continue;
                if (destinationAsns.Contains(asn) && asn != wanIpAsn) continue;
                votes[asn] = votes.TryGetValue(asn, out var v)
                    ? (v.Count + 1, Math.Min(v.BestDepth, depth))
                    : (1, depth);
                break;
            }
        }

        if (wanIpAsn is int wan
            && (votes.ContainsKey(wan) || votes.Count == 0 || asnByIp.Values.Contains(wan)))
            return wan;

        if (votes.Count == 0) return null;
        return votes
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Value.BestDepth)
            .ThenBy(kv => kv.Key)
            .First().Key;
    }

    /// <summary>
    /// Positional attribution for an ISP's unannounced first mile. Collects hop addresses
    /// that appear BEFORE the first access-ASN hop on their own trace and carry no BGP
    /// attribution but sit in public or shared/CGNAT (RFC 6598) space - Bell's 142.124.x
    /// aggregation hops (#984). Being upstream of us and downstream of the ISP's announced
    /// border makes them the ISP's access infrastructure even though no ASN maps to them.
    /// RFC1918 hops count too: an ISP whose access network is numbered out of private space (a CMTS
    /// or BNG on 10/8) leaves no other trace of its first mile, and dropping those hops is what left
    /// such sites with no access targets at all. Our own gateway is excluded by address; nothing
    /// else is, because there is no reliable way to tell a bridged CPE from the ISP's first device
    /// here - the sequences carry only hops that RESPONDED, so position is not TTL distance, and a
    /// probe running ON the gateway has no gateway hop to count from at all. A wrong one is
    /// proposed, not applied: discovery review is where the operator unticks it.
    /// Traces whose first attributed hop is NOT the access ASN (e.g. a trace
    /// that only ever surfaces the destination's edge) contribute nothing - we can't prove
    /// their prefix hops sit below the access border. Dedupes across traces, preserves
    /// first-seen order.
    /// </summary>
    /// <summary>
    /// A private hop this close is on our own side of the WAN - the gateway itself, a bridged CPE,
    /// a middlebox. The ISP's first-mile gear is a WAN crossing away and answers in milliseconds,
    /// not fractions of one, so distance separates the two where position cannot: non-responding
    /// hops make position unreliable, and a probe running on the gateway has no gateway hop at all.
    /// </summary>
    internal const double LocalHopRttMs = 1.2;

    internal static List<string> CollectUnannouncedAccessAddresses(
        IEnumerable<IReadOnlyList<string>> traceAddressSequences,
        IReadOnlyDictionary<string, int> asnByIp,
        int accessAsn,
        IReadOnlyCollection<string>? gatewayIps = null,
        IReadOnlyDictionary<string, double>? minRttByIp = null)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trace in traceAddressSequences)
        {
            var prefix = new List<string>();
            foreach (var address in trace)
            {
                if (gatewayIps != null && gatewayIps.Contains(address)) continue;
                if (asnByIp.TryGetValue(address, out var asn))
                {
                    if (asn == accessAsn)
                        foreach (var p in prefix)
                            if (seen.Add(p)) result.Add(p);
                    break;
                }
                // Only private space is judged on distance: public and CGNAT hops are carrier space
                // whatever they measure. An address with no timing is kept - silence is not evidence.
                if (minRttByIp != null
                    && NetworkUtilities.ClassifyPublicAddress(address)
                        is not (PublicAddressClass.PublicIPv4 or PublicAddressClass.Cgnat)
                    && minRttByIp.TryGetValue(address, out var hopRtt)
                    && hopRtt < LocalHopRttMs)
                    continue;
                prefix.Add(address);
            }
        }
        return result;
    }

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
        var label = string.Join('.', parts.Take(parts.Length - 2));
        return PlaceholderPtrLabels.Contains(label) ? null : label;
    }

    /// <summary>
    /// PTR labels that name nothing. Starlink answers most of its network with
    /// "undefined.hostname.localhost", which parses as a perfectly good label and produced a row
    /// called "&lt;Org&gt; undefined" - repeated for every hop, so they were not even distinguishable
    /// from each other. A placeholder is treated as no PTR at all.
    /// </summary>
    private static readonly HashSet<string> PlaceholderPtrLabels =
        new(StringComparer.OrdinalIgnoreCase) { "undefined", "unknown", "none", "null", "localhost" };

    /// <summary>
    /// Access technology implied by the access ISP itself. Only satellite is safe to read this
    /// way: a terrestrial ISP's AS carries fiber, cable and DSL customers behind the same number,
    /// while SpaceX's carries one medium. Matched on the number AND the org name so a secondary
    /// ASN, or a registry rename, still lands.
    /// </summary>
    internal static AccessTechnology? TechnologyFromAccessAsn(int? asn, string? orgName)
        => IsStarlinkAsn(asn, orgName) ? AccessTechnology.Satellite : null;

    /// <summary>
    /// Whether an ASN is SpaceX's. Matched on the number AND the org name so a secondary ASN, or a
    /// registry rename, still lands.
    /// </summary>
    internal static bool IsStarlinkAsn(int? asn, string? orgName)
    {
        if (asn == 14593) return true;
        if (string.IsNullOrWhiteSpace(orgName)) return false;
        return orgName.Contains("starlink", StringComparison.OrdinalIgnoreCase)
            || orgName.Contains("space exploration", StringComparison.OrdinalIgnoreCase)
            || orgName.Contains("spacex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether a PTR answers with a placeholder rather than a name - "undefined.hostname.localhost"
    /// and the like. Distinct from having no PTR at all, which is a different and less telling
    /// thing: a host that answers with a placeholder is saying something about what it is.
    /// </summary>
    internal static bool IsPlaceholderPtrHostname(string? hostname)
    {
        if (string.IsNullOrEmpty(hostname)) return false;
        var first = hostname.Split('.')[0];
        return PlaceholderPtrLabels.Contains(first);
    }

    /// <summary>
    /// Re-applies the metered probe budget to the candidates on screen. The budget reads the
    /// access technology, which is often only right once someone sets it in the review - a
    /// satellite WAN identified after the run would otherwise keep the pre-selection an unmetered
    /// run made, which is the whole allowance it was meant to save.
    /// </summary>
    public async Task ReapplyProbeBudgetAsync(CancellationToken ct = default)
    {
        try
        {
            await using var db = await CreateDbAsync(ct);
            var plan = await ResolveProbePlanAsync(
                db, _binding?.WanInterface ?? State.WanInterface ?? "wan", ct);
            ApplyAutoEnableBudget(State, plan.MaxAutoEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Re-applying the probe budget after an access technology change failed");
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
