using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi.Models;
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
    private readonly ILogger<UpstreamTracerService> _logger;

    private readonly SemaphoreSlim _stateLock = new(1, 1);
    private Task? _runningTask;

    public UpstreamTracerState State { get; private set; } = new();

    public UpstreamTracerService(
        UniFiConnectionService connectionService,
        IGatewaySshService gatewaySsh,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        AsnResolutionService asnResolution,
        ILogger<UpstreamTracerService> logger)
    {
        _connectionService = connectionService;
        _gatewaySsh = gatewaySsh;
        _dbFactory = dbFactory;
        _asnResolution = asnResolution;
        _logger = logger;
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

        List<UniFiDeviceResponse> devices;
        try
        {
            devices = (await _connectionService.Client.GetDevicesAsync(ct))?.ToList()
                      ?? new List<UniFiDeviceResponse>();
        }
        catch (Exception ex)
        {
            return Fail($"Couldn't fetch UniFi devices: {ex.Message}");
        }

        var gateway = devices.FirstOrDefault(d =>
            d.DeviceType == NetworkOptimizer.Core.Enums.DeviceType.Gateway);
        if (gateway?.PortTable == null)
        {
            return Fail("No gateway found in topology.");
        }

        // Primary WAN port: first IsUplink port with a network_name starting with "wan",
        // else any uplink port.
        var wanPort = gateway.PortTable.FirstOrDefault(p =>
            p.IsUplink &&
            (p.NetworkName?.StartsWith("wan", StringComparison.OrdinalIgnoreCase) ?? false));
        wanPort ??= gateway.PortTable.FirstOrDefault(p => p.IsUplink);
        if (wanPort == null)
        {
            return Fail("Couldn't identify the WAN port on the gateway.");
        }

        State.WanInterface = wanPort.NetworkName ?? "wan";

        // The port_table IP is the WAN public IP in our experience (or RFC1918 for
        // double-NAT, CGNAT for cgnat, etc.). NetworkUtilities.ClassifyPublicAddress
        // does the bracket detection.
        State.WanIpAddress = wanPort.Ip;
        State.WanIpClass = NetworkUtilities.ClassifyPublicAddress(wanPort.Ip);

        switch (State.WanIpClass)
        {
            case PublicAddressClass.PublicIPv4:
                // happy path
                break;

            case PublicAddressClass.Cgnat:
                State.IsCgnat = true;
                _logger.LogInformation("Tracer: WAN IP is CGNAT ({Ip}); proceeding with discovery", wanPort.Ip);
                break;

            case PublicAddressClass.DoubleNat:
                // Per locked Gate 2 decision 8: proceed anyway, traceroute will still
                // reveal the upstream ISP. Surface a small "double-NAT detected" badge.
                State.IsDoubleNat = true;
                _logger.LogInformation("Tracer: WAN IP is RFC1918 ({Ip}); proceeding (double-NAT)", wanPort.Ip);
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
        State.CurrentActivity = "Reading WAN L2 neighbor (the first-mile device)...";

        // We need the gateway's actual WAN interface device name (eth0, eth4, etc.)
        // for `ip neigh`. The UniFi port_table's network_name ("wan") isn't the OS
        // device name, but the port_idx + UniFi device-naming convention usually maps
        // cleanly. For iteration 1 we try the common candidates; iteration 2 can read
        // the gateway's network config more precisely if needed.
        var candidates = new[] { "eth0", "eth1", "eth4", "eth5", "wan", "wan0" };

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
            State.CurrentActivity = "Couldn't identify L2 neighbor MAC. Proceeding anyway; access cloud labels will fall back to PTR / position.";
            return true;
        }

        State.WanNeighborMac = neighborMac;

        // OUI lookup via the OuiVendors table. Tracer's slow-tier seed (iteration 2)
        // will populate this from the bundled IEEE OUI dataset; for now, attempt the
        // lookup and accept a null vendor (labels will fall back gracefully).
        try
        {
            var ouiPrefix = neighborMac.Replace(":", "").Substring(0, 6).ToUpperInvariant();
            ouiPrefix = $"{ouiPrefix.Substring(0, 2)}:{ouiPrefix.Substring(2, 2)}:{ouiPrefix.Substring(4, 2)}";
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var vendor = await db.OuiVendors.AsNoTracking()
                .Where(o => o.OuiPrefix == ouiPrefix)
                .Select(o => o.VendorName)
                .FirstOrDefaultAsync(ct);
            State.WanNeighborOuiVendor = vendor;
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

    // ---- Step 3: Trace the access ISP path (iteration 2) ----

    private Task TraceAccessIspAsync(CancellationToken ct)
    {
        State.Step = TracerStep.TracingAccessIsp;
        State.CurrentActivity = "Tracing access ISP path... (iteration 2 will run parallel ICMP+UDP traceroutes to the CDN rotation)";
        // Iteration 2: run traceroute -I + traceroute (UDP) in parallel against the
        // 5-CDN rotation (Cloudflare, Google, Akamai, Meta, Apple), merge hop sets,
        // attribute hops to ASNs via _asnResolution, pick the first 1-3 hops that
        // belong to the user's access ISP ASN, label them per the priority order
        // (PTR > role inference > bare IP), populate State.AccessHops.
        return Task.CompletedTask;
    }

    // ---- Step 4: Trace transit ASNs (iteration 2) ----

    private Task TraceTransitAsnsAsync(CancellationToken ct)
    {
        State.Step = TracerStep.TracingTransitAsns;
        State.CurrentActivity = "Tracing transit ASNs... (iteration 2 will walk the per-ASN fallback ladder)";
        // Iteration 2: dedupe transit ASNs across all CDN traces, walk the per-ASN
        // ladder (up to 3 hops in the ASN -> TCP/443 fallback -> CDN path-proxy ->
        // Tier D unresolved), populate State.TransitAsns with the chosen tier + target
        // for each. Per locked decision 9: if no transit ASNs found, render a "direct
        // peering" pseudo-cloud labeled with the destination ASN we successfully
        // traced to.
        return Task.CompletedTask;
    }

    // ---- Commit ----

    /// <summary>
    /// After the user reviews and edits labels, commit the proposed targets into
    /// the MonitoringTargets table. Becomes the live source the latency tier probes.
    /// </summary>
    public async Task CommitResultsAsync(CancellationToken ct = default)
    {
        if (State.Step != TracerStep.ReviewingResults) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var hop in State.AccessHops.Where(h => h.Enabled))
        {
            await UpsertTargetAsync(db, hop, ct);
        }
        foreach (var transit in State.TransitAsns.Where(t => t.Enabled))
        {
            await UpsertTransitTargetAsync(db, transit, ct);
        }

        await db.SaveChangesAsync(ct);
        State.Step = TracerStep.Done;
        State.CurrentActivity = "Targets committed. The agent will start probing on the next latency-tier cycle.";
    }

    private static async Task UpsertTargetAsync(NetworkOptimizerDbContext db, AccessHopCandidate hop, CancellationToken ct)
    {
        var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == hop.TargetId, ct);
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
                PtrHostname = hop.PtrHostname,
                AutoLabel = hop.Role.ToString(),
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            // Re-validation: keep target_id stable, update mode if it changed (history
            // preservation per locked decision 6b).
            existing.Address = hop.Address;
            existing.ProbeMode = hop.RespondedTo;
            existing.Name = string.IsNullOrEmpty(existing.Name) ? hop.Label : existing.Name; // don't stomp user-renamed labels
            existing.LastVerified = DateTime.UtcNow;
        }
    }

    private static async Task UpsertTransitTargetAsync(NetworkOptimizerDbContext db, TransitAsnCandidate transit, CancellationToken ct)
    {
        if (transit.Method == DiscoveryMethod.Unresolved || string.IsNullOrEmpty(transit.TargetId)) return;
        var existing = await db.MonitoringTargets.FirstOrDefaultAsync(t => t.TargetId == transit.TargetId, ct);
        if (existing == null)
        {
            db.MonitoringTargets.Add(new MonitoringTarget
            {
                TargetId = transit.TargetId,
                Name = transit.AsnName,
                Address = transit.HopAddress ?? transit.PathProxyTarget ?? "0.0.0.0",
                ProbeMode = transit.RespondedTo ?? NetworkOptimizer.Core.Enums.ProbeMode.Icmp,
                DiscoveredProbeMode = transit.RespondedTo,
                TargetType = MonitoringTargetType.Transit,
                AsnNumber = transit.AsnNumber,
                AsnName = transit.AsnName,
                VantagePoint = "server",
                PollIntervalSeconds = 15,
                PingCount = 5,
                Enabled = true,
                AutoDiscovered = true,
                DiscoveryMethod = transit.Method,
                CreatedAt = DateTime.UtcNow,
                LastVerified = DateTime.UtcNow
            });
        }
        else
        {
            existing.Address = transit.HopAddress ?? transit.PathProxyTarget ?? existing.Address;
            existing.ProbeMode = transit.RespondedTo ?? existing.ProbeMode;
            existing.DiscoveryMethod = transit.Method;
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
