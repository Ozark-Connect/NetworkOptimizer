using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetworkOptimizer.Core.Helpers;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Models;
using NetworkOptimizer.Web.Services.ApAgent;
using NetworkOptimizer.WiFi;
using NetworkOptimizer.WiFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for the Client Dashboard - identifies clients, polls signal quality,
/// manages signal logs, and provides history data.
/// Scoped per site: the injected UniFiConnectionService and ClientSpeedTestService forward to
/// (or are resolved for) the current site, and all signal/speed history reads and writes go
/// through that site's own database.
/// </summary>
public class ClientDashboardService
{
    private readonly ILogger<ClientDashboardService> _logger;
    private readonly NetworkOptimizer.Storage.Services.SiteDbContextFactory _siteDbFactory;
    private readonly SiteContextService _siteContext;
    private readonly UniFiConnectionService _connectionService;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly ClientSpeedTestService _speedTestService;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;

    // Track last trace hash per client MAC to detect changes
    private readonly ConcurrentDictionary<string, string> _lastTraceHashes = new();
    private bool _traceHashesSeeded;

    // Cleanup tracking
    private DateTime _lastCleanup = DateTime.MinValue;

    // Cache offline identities to avoid hitting the history API every poll
    private readonly ConcurrentDictionary<string, ClientIdentity> _offlineIdentityCache = new();

    // Cache IP->MAC mapping after first identification so subsequent polls use GetClientAsync(mac)
    private readonly ConcurrentDictionary<string, string> _ipToMacCache = new();

    // Which interfaces carry a device's console port number, resolved once per watched port.
    private readonly ConcurrentDictionary<(string Mac, int Port), List<string>> _portIfNames = new();

    // AP Agent live polling. Optional accelerator: absent on every site without AP Agents, and the
    // WiFiman and stat/sta paths below are untouched and remain what everything falls back to.
    private readonly ApAgentClientLiveService? _apAgentLive;
    private readonly ApAgentTelemetryRegistry? _apAgentTelemetry;
    private readonly MonitoringLiveStatsRegistry _liveStats;

    /// <summary>Roam-follow state per client. One page follows one client, so the cap is a guard.</summary>
    private const int MaxTrackedFollowers = 8;
    private readonly Dictionary<string, ApAgentRoamFollower> _followers = new(StringComparer.OrdinalIgnoreCase);

    // Where each client was last known to be, so a roam is recognized as a change of access point.
    private readonly ConcurrentDictionary<string, string> _lastApMacByClient = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClientDashboardService(
        ILogger<ClientDashboardService> logger,
        NetworkOptimizer.Storage.Services.SiteDbContextFactory siteDbFactory,
        UniFiConnectionService connectionService,
        SpeedTestServiceRegistry speedTestRegistry,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        SiteContextService siteContext,
        MonitoringLiveStatsRegistry liveStats,
        ApAgentClientLiveService? apAgentLive = null,
        ApAgentTelemetryRegistry? apAgentTelemetry = null,
        Microsoft.Extensions.Caching.Memory.IMemoryCache? cache = null)
    {
        _logger = logger;
        _cache = cache;
        _siteDbFactory = siteDbFactory;
        _siteContext = siteContext;
        _liveStats = liveStats;
        _connectionService = connectionService;
        // The path analyzer must be this site's, not the main-pinned singleton, so L2 traces
        // resolve against the current site's topology.
        var siteServices = speedTestRegistry.GetFor(_siteContext.Slug);
        _pathAnalyzer = siteServices.PathAnalyzer;
        _speedTestService = siteServices.ClientSpeedTest;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _apAgentLive = apAgentLive;
        _apAgentTelemetry = apAgentTelemetry;
    }

    /// <summary>Context for the database holding this instance's site data.</summary>
    private NetworkOptimizerDbContext CreateSiteDb() => _siteDbFactory.CreateForSite(_siteContext.Slug, _siteContext.IsDefault);

    /// <summary>A device choice for the Client Performance page's manual picker.</summary>
    public sealed record SelectableClient(string Ip, string Name, bool IsWired, string? Mac = null, bool IsOnline = true);

    /// <summary>
    /// This site's online clients as picker choices for the Client Performance page. Used on
    /// managed (non-default) sites when the viewer's LAN address can't be discovered - the
    /// browser reaches this central server over the WAN, and the on-site agent's whoami probe
    /// may be unavailable (agent offline, or its certificate untrusted by the browser).
    /// </summary>
    public async Task<List<SelectableClient>> GetSelectableClientsAsync()
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return new List<SelectableClient>();
        try
        {
            var clients = await _connectionService.Client.GetClientsAsync();
            // Overlay UniFi's friendly display name (v2 active-clients, cached 5 min) so the
            // picker matches the name shown on the page and in Client Stats.
            var displayNames = await ClientDisplayNameCache.GetAsync(_connectionService.Client);
            var online = (clients ?? new List<UniFiClientResponse>())
                .Where(c => !string.IsNullOrEmpty(c.BestIp))
                .Select(c => new SelectableClient(
                    c.BestIp!,
                    displayNames.TryGetValue(c.Mac.ToLowerInvariant(), out var dn) ? dn
                        : !string.IsNullOrWhiteSpace(c.Name) ? c.Name
                        : c.UnifiDeviceInfoFromUcore?.Name is { Length: > 0 } ucore ? ucore
                        : !string.IsNullOrWhiteSpace(c.Hostname) ? c.Hostname : c.BestIp!,
                    c.IsWired,
                    c.Mac,
                    IsOnline: true))
                .ToList();

            // Two days of departed clients too, so a device that dropped yesterday is still pickable.
            // Not Client Stats' thirty days: in a picker that is noise.
            var seen = new HashSet<string>(online.Select(c => c.Ip), StringComparer.OrdinalIgnoreCase);
            try
            {
                var history = await _connectionService.Client.GetClientHistoryAsync(withinHours: 48);
                foreach (var h in history ?? new List<UniFiClientDetailResponse>())
                {
                    if (string.IsNullOrEmpty(h.BestIp) || !seen.Add(h.BestIp)) continue;
                    online.Add(new SelectableClient(
                        h.BestIp!,
                        !string.IsNullOrWhiteSpace(h.DisplayName) ? h.DisplayName
                            : !string.IsNullOrWhiteSpace(h.Name) ? h.Name
                            : !string.IsNullOrWhiteSpace(h.Hostname) ? h.Hostname : h.BestIp!,
                        h.IsWired,
                        h.Mac,
                        IsOnline: false));
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Client history unavailable for the picker; showing connected clients only");
            }

            // Wireless first: the page is about Wi-Fi performance, so those are what a viewer came
            // to pick. Connected before departed within each group, then alphabetical.
            return online
                .OrderBy(c => c.IsWired)
                .ThenByDescending(c => c.IsOnline)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load selectable clients for the device picker");
            return new List<SelectableClient>();
        }
    }

    /// <summary>
    /// Identify a client by its IP address using UniFi controller data.
    /// After first identification, uses the single-client endpoint (stat/sta/{mac})
    /// instead of fetching all clients. Falls back to client history for offline devices.
    /// </summary>
    public async Task<ClientIdentity?> IdentifyClientAsync(string clientIp)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        // Guard the direct-call path: an IPv4 client on a dual-stack socket is ::ffff:a.b.c.d,
        // which never equals the console's plain-IPv4 BestIp. Sources normalize already; this
        // keeps the match correct for any caller. Real IPv6 is left untouched.
        clientIp = NetworkUtilities.NormalizeToIPv4String(clientIp) ?? clientIp;

        try
        {
            UniFiClientResponse? client = null;

            // Fast path: if we already know the MAC, fetch just this client
            if (_ipToMacCache.TryGetValue(clientIp, out var knownMac))
            {
                _logger.LogTrace("Identify {Ip}: fast path via stat/sta/{Mac}", clientIp, knownMac);
                client = await _connectionService.Client.GetClientAsync(knownMac);

                // Verify the IP still matches - if another device took this IP
                // (DHCP reassignment), the MAC lookup returns the wrong device.
                // Match on BestIp so fixed/reservation devices (empty live ip) still match.
                if (client != null && client.BestIp != clientIp)
                {
                    _logger.LogTrace("Identify {Ip}: IP mismatch (device now at {NewIp}), invalidating cache", clientIp, client.Ip);
                    client = null;
                }

                // If lookup failed or IP changed, invalidate and fall through to full list
                if (client == null)
                {
                    _logger.LogTrace("Identify {Ip}: fast path miss, falling back to full client list", clientIp);
                    _ipToMacCache.TryRemove(clientIp, out _);
                }
            }

            // Slow path: fetch all clients and match by IP
            if (client == null)
            {
                _logger.LogTrace("Identify {Ip}: slow path via stat/sta (all clients)", clientIp);
                var clients = await _connectionService.Client.GetClientsAsync();
                client = clients?.FirstOrDefault(c => c.BestIp == clientIp);
            }

            if (client != null)
            {
                _offlineIdentityCache.TryRemove(clientIp, out _);
                _ipToMacCache[clientIp] = client.Mac;

                // UniFi's friendly display name is only on the v2 active-clients endpoint, not
                // stat/sta; pull it (cached 5 min, labels only) so an unnamed device shows the
                // same name here as in Client Stats instead of a raw MAC.
                var displayNames = await ClientDisplayNameCache.GetAsync(_connectionService.Client);
                displayNames.TryGetValue(client.Mac.ToLowerInvariant(), out var displayName);
                var identity = MapClientToIdentity(client, displayName);

                // Try WiFiman endpoint for more-realtime signal data, overlay on top of stat/sta
                await OverlayWiFiManDataAsync(identity, clientIp);

                // Then the access point's own agent, where there is one. Last overlay wins because
                // it is the only source that measured the client rather than reporting on it.
                await OverlayApAgentDataAsync(identity);

                // The AP is read off the identity, not the console record, so a client the agent
                // followed through a roam is enriched from where it is now.
                await EnrichWithApInfoAsync(identity, identity.ApMac);

                await EnrichWithSwitchNameAsync(identity);

                // The console keeps a departed WIRELESS client in its active list for minutes.
                // Where an agent covers the access point its verdict is the fresher answer, so the
                // page shows offline now rather than when the console catches up. Wired clients are
                // exempt: an agent only ever lists stations, so its silence about one says nothing.
                if (!identity.IsWired
                    && _apAgentTelemetry?.GetFor(_siteContext.Slug)
                        .PresenceFor(identity.ApMac, identity.Mac)
                    == NetworkOptimizer.Core.Helpers.AgentClientPresence.Absent)
                {
                    identity.IsOffline = true;
                }

                return identity;
            }

            // Not in the console's active list. Before concluding offline, ask the AP Agents: on a
            // covered access point the agent knows the client and its IP from association, seconds
            // before the console lists it. The console's active answer is never overridden - this
            // runs only when it had none.
            var fromAgent = await IdentifyFromApAgentAsync(clientIp);
            if (fromAgent != null)
                return fromAgent;

            // Device not in active list - check offline cache
            if (_offlineIdentityCache.TryGetValue(clientIp, out var cached))
                return cached;

            // Try client history API (includes offline devices)
            var history = await _connectionService.Client.GetClientHistoryAsync(withinHours: 720);
            var histClient = history?.FirstOrDefault(c => c.BestIp == clientIp);

            if (histClient != null)
            {
                var offlineIdentity = new ClientIdentity
                {
                    Mac = histClient.Mac,
                    Name = histClient.DisplayName ?? histClient.Name,
                    Hostname = histClient.Hostname,
                    Ip = clientIp,
                    IsWired = histClient.IsWired,
                    Oui = histClient.Oui,
                    IsOffline = true
                };

                _offlineIdentityCache[clientIp] = offlineIdentity;
                _logger.LogDebug("Identified offline client {Ip} as {Name} ({Mac})",
                    clientIp, offlineIdentity.DisplayName, offlineIdentity.Mac);
                return offlineIdentity;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to identify client {Ip}", clientIp);
            return null;
        }
    }

    /// <summary>
    /// Builds an online identity for a client an agent-covered access point currently holds but
    /// the console does not yet list as active. Null everywhere the agent path cannot vouch, which
    /// leaves the offline-history and VPN fallbacks exactly as they were.
    /// </summary>
    private async Task<ClientIdentity?> IdentifyFromApAgentAsync(string clientIp)
    {
        var known = _apAgentTelemetry?.GetFor(_siteContext.Slug).FindClientByIp(clientIp);
        if (known == null) return null;

        var identity = new ClientIdentity
        {
            Mac = known.ClientMac,
            Name = known.Hostname,
            Hostname = known.Hostname,
            Ip = clientIp,
            IsWired = false,
            ApMac = known.ApMac,
        };

        _offlineIdentityCache.TryRemove(clientIp, out _);
        _ipToMacCache[clientIp] = known.ClientMac;
        _logger.LogDebug("Identified client {Ip} as {Mac} from its access point's agent", clientIp, known.ClientMac);

        // The same enrichment shape as the console path, so the page renders identically.
        await OverlayApAgentDataAsync(identity);
        await EnrichWithApInfoAsync(identity, identity.ApMac);
        return identity;
    }

    /// <summary>
    /// Identify a client that reaches the dashboard through a VPN (Tailscale, Teleport,
    /// or a UniFi remote-user VPN). These clients never appear in the UniFi client list,
    /// so <see cref="IdentifyClientAsync"/> returns null for them. Returns a synthetic
    /// identity carrying the VPN type and IP for the simplified dashboard view, or null
    /// when the IP is not VPN-sourced (or the console is unreachable).
    /// </summary>
    public async Task<ClientIdentity?> IdentifyVpnClientAsync(string clientIp)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        try
        {
            var vpnType = await _pathAnalyzer.ClassifyVpnClientAsync(clientIp);
            if (vpnType == null)
                return null;

            var name = vpnType switch
            {
                HopType.Tailscale => "Tailscale Client",
                HopType.Teleport => "Teleport Client",
                _ => "VPN Client"
            };

            _logger.LogDebug("Identified VPN client {Ip} as {VpnType}", clientIp, vpnType);
            return new ClientIdentity
            {
                Mac = "",
                Name = name,
                Ip = clientIp,
                IsWired = false,
                VpnType = vpnType
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to classify VPN client {Ip}", clientIp);
            return null;
        }
    }

    /// <summary>
    /// Poll current signal quality for a client, run a trace, store the result, and return live data.
    /// </summary>
    public async Task<SignalPollResult?> PollSignalAsync(
        string clientIp,
        double? gpsLat = null,
        double? gpsLng = null,
        int? gpsAccuracy = null,
        bool persist = true)
    {
        var pollSw = System.Diagnostics.Stopwatch.StartNew();

        // Seed trace hashes from DB on first use (survives restarts)
        if (!_traceHashesSeeded)
        {
            await SeedTraceHashesAsync();
        }

        var identity = await IdentifyClientAsync(clientIp);
        var identifyMs = pollSw.ElapsedMilliseconds;
        if (identity == null)
            return null;

        var result = new SignalPollResult
        {
            Client = identity,
            Timestamp = DateTime.UtcNow
        };

        // Offline devices: no trace or signal to poll, just return identity
        if (identity.IsOffline)
        {
            _logger.LogTrace("Poll for {Ip}: offline, identify={IdentifyMs}ms", clientIp, identifyMs);
            return result;
        }

        // Run L2 trace
        try
        {
            var path = await _pathAnalyzer.CalculatePathToGatewayAsync(clientIp);

            if (path.IsValid)
            {
                var analysis = _pathAnalyzer.AnalyzeSpeedTest(path, 0, 0);
                result.PathAnalysis = analysis;

                // For wired clients, populate ApName/ApModel from the first hop (switch/gateway)
                if (identity.IsWired && string.IsNullOrEmpty(identity.ApName) && path.Hops.Count > 0)
                {
                    var firstHop = path.Hops[0];
                    if (!string.IsNullOrEmpty(firstHop.DeviceName))
                        identity.ApName = firstHop.DeviceName;
                    if (!string.IsNullOrEmpty(firstHop.DeviceModel))
                        identity.ApModel = firstHop.DeviceModel;
                }

                // Compute trace hash for dedup (structural path only, not dynamic data)
                result.TraceHash = ComputeTraceHash(path);

                // Check if trace changed
                if (_lastTraceHashes.TryGetValue(identity.Mac, out var lastHash))
                    result.TraceChanged = lastHash != result.TraceHash;
                else
                    result.TraceChanged = true; // First poll for this client
                _lastTraceHashes[identity.Mac] = result.TraceHash;

                // The path is what a port resolution was read against, so a changed one retires it.
                if (result.TraceChanged)
                    _portIfNames.Clear();

                // Trace changes always store immediately (with full trace data).
                // Regular polls buffer signal values and flush the mean every 5 seconds.
                if (result.TraceChanged)
                {
                    await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
                }
                else if (persist)
                {
                    await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
                }
            }
            else
            {
                // Store without trace
                result.TraceChanged = false;
                if (persist)
                    await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trace failed for {Ip}, storing signal-only log", clientIp);
            if (persist)
                await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
        }

        _logger.LogTrace("Poll for {Ip}: identify={IdentifyMs}ms, total={TotalMs}ms",
            clientIp, identifyMs, pollSw.ElapsedMilliseconds);

        return result;
    }

    /// <summary>
    /// Get signal history for a client within a time range.
    /// Fills forward TraceJson for entries that didn't store it (dedup optimization).
    /// </summary>
    public async Task<List<SignalHistoryEntry>> GetSignalHistoryAsync(
        string mac, DateTime from, DateTime to, int skip = 0, int take = 500)
    {
        await using var db = CreateSiteDb();

        var query = db.ClientSignalLogs
            .Where(l => l.ClientMac == mac && l.Timestamp >= from && l.Timestamp <= to)
            .OrderBy(l => l.Timestamp);

        var logs = await query
            .Skip(skip)
            .Take(take)
            .ToListAsync();

        return logs.Select(l => new SignalHistoryEntry
        {
            Timestamp = l.Timestamp,
            SignalDbm = l.SignalDbm,
            NoiseDbm = l.NoiseDbm,
            Channel = l.Channel,
            ChannelWidth = l.ChannelWidth,
            Band = l.Band,
            Protocol = l.Protocol,
            TxRateKbps = l.TxRateKbps,
            RxRateKbps = l.RxRateKbps,
            ApMac = l.ApMac,
            ApName = l.ApName,
            HopCount = l.HopCount,
            BottleneckLinkSpeedMbps = l.BottleneckLinkSpeedMbps,
            Latitude = l.Latitude,
            Longitude = l.Longitude,
            DataSource = SignalDataSource.Local
        }).ToList();
    }

    /// <summary>
    /// Get GPS-located signal measurements as map points, deduplicating consecutive
    /// entries where AP, band, channel, signal, and position are unchanged.
    /// If mac is null, returns points for all clients.
    /// </summary>
    public async Task<List<SignalMapPoint>> GetSignalMapPointsAsync(
        string? mac, DateTime from, DateTime to)
    {
        await using var db = CreateSiteDb();

        var query = db.ClientSignalLogs
            .Where(l => l.Timestamp >= from && l.Timestamp < to
                     && l.Latitude != null && l.Longitude != null
                     && l.SignalDbm != null);

        if (!string.IsNullOrEmpty(mac))
            query = query.Where(l => l.ClientMac == mac);

        // Sort by client then timestamp so dedup works per-client
        var logs = await query
            .OrderBy(l => l.ClientMac)
            .ThenBy(l => l.Timestamp)
            .ToListAsync();

        // Deduplicate consecutive entries with same AP/band/channel/signal/position
        var result = new List<SignalMapPoint>();
        SignalMapPoint? prev = null;
        string? prevMac = null;

        foreach (var l in logs)
        {
            var point = new SignalMapPoint
            {
                Latitude = l.Latitude!.Value,
                Longitude = l.Longitude!.Value,
                SignalDbm = l.SignalDbm!.Value,
                Timestamp = l.Timestamp,
                Band = l.Band,
                Channel = l.Channel,
                ApMac = l.ApMac,
                ApName = l.ApName,
                ClientMac = l.ClientMac,
                ClientIp = l.ClientIp,
                DeviceName = l.DeviceName
            };

            // Reset dedup when switching to a different client
            if (l.ClientMac != prevMac)
            {
                prev = null;
                prevMac = l.ClientMac;
            }

            if (prev != null
                && prev.ApName == point.ApName
                && prev.Band == point.Band
                && prev.Channel == point.Channel
                && prev.SignalDbm == point.SignalDbm
                && prev.Latitude == point.Latitude
                && prev.Longitude == point.Longitude)
            {
                continue; // identical to previous, skip
            }

            result.Add(point);
            prev = point;
        }

        return result;
    }

    /// <summary>
    /// Get trace change events for a client (entries where TraceJson is stored).
    /// </summary>
    public async Task<List<TraceChangeEntry>> GetTraceHistoryAsync(
        string mac, DateTime from, DateTime to)
    {
        await using var db = CreateSiteDb();

        var logs = await db.ClientSignalLogs
            .Where(l => l.ClientMac == mac
                     && l.Timestamp >= from
                     && l.Timestamp <= to
                     && l.TraceJson != null)
            .OrderByDescending(l => l.Timestamp)
            .ToListAsync();

        return logs.Select(l =>
        {
            PathAnalysisResult? analysis = null;
            if (!string.IsNullOrEmpty(l.TraceJson))
            {
                try
                {
                    analysis = JsonSerializer.Deserialize<PathAnalysisResult>(l.TraceJson, JsonOptions);
                }
                catch { /* ignore deserialization errors */ }
            }

            return new TraceChangeEntry
            {
                Timestamp = l.Timestamp,
                TraceHash = l.TraceHash,
                TraceJson = l.TraceJson,
                HopCount = l.HopCount,
                BottleneckLinkSpeedMbps = l.BottleneckLinkSpeedMbps,
                PathAnalysis = analysis
            };
        }).ToList();
    }

    /// <summary>
    /// Get speed test results for a client by MAC, within a time range.
    /// </summary>
    public async Task<List<Iperf3Result>> GetSpeedResultsAsync(
        string mac, DateTime from, DateTime to)
    {
        await using var db = CreateSiteDb();

        // Include every LAN test direction for this device: server-initiated
        // (we SSH to the device and run iperf3), client-initiated, and browser-based.
        // WAN directions (Cloudflare / UWN / OpenSpeedTest WAN) are excluded - this is
        // the client's LAN throughput history, not its internet speed.
        return await db.Iperf3Results
            .Where(r => (r.Direction == SpeedTestDirection.ServerToDevice
                       || r.Direction == SpeedTestDirection.ClientToServer
                       || r.Direction == SpeedTestDirection.BrowserToServer)
                      && r.ClientMac == mac
                      && r.TestTime >= from
                      && r.TestTime <= to)
            .OrderByDescending(r => r.TestTime)
            .ToListAsync();
    }

    /// <summary>
    /// Get speed test results for a client by its source host/IP, within a time range.
    /// Used for VPN clients (Tailscale/Teleport/remote-user VPN), which have no UniFi MAC:
    /// their browser/iperf3 results store <c>DeviceHost</c> = the VPN IP and a null MAC,
    /// so the MAC-keyed <see cref="GetSpeedResultsAsync"/> can't find them.
    /// </summary>
    public async Task<List<Iperf3Result>> GetSpeedResultsByHostAsync(
        string host, DateTime from, DateTime to)
    {
        await using var db = CreateSiteDb();

        // Same LAN directions as the MAC-keyed query - the client's LAN throughput
        // history, not its internet speed.
        return await db.Iperf3Results
            .Where(r => (r.Direction == SpeedTestDirection.ServerToDevice
                       || r.Direction == SpeedTestDirection.ClientToServer
                       || r.Direction == SpeedTestDirection.BrowserToServer)
                      && r.DeviceHost == host
                      && r.TestTime >= from
                      && r.TestTime <= to)
            .OrderByDescending(r => r.TestTime)
            .ToListAsync();
    }

    /// <summary>
    /// Get merged signal history: local high-res data augmented with UniFi controller metrics
    /// for time ranges where local data is sparse.
    /// </summary>
    public async Task<List<SignalHistoryEntry>> GetMergedSignalHistoryAsync(
        string mac, DateTime from, DateTime to)
    {
        // Scale the fetch limit to the time range. At 1s poll intervals:
        // 1h=3600, 6h=21600, 24h=86400. Cap at 90k to cover 24h of 1s polling;
        // the UI downsamples for display anyway.
        var spanHours = (to - from).TotalHours;
        var take = Math.Min((int)(spanHours * 3600) + 100, 90_000);

        // Get local data first (high resolution, 5s intervals)
        var localEntries = await GetSignalHistoryAsync(mac, from, to, take: take);

        // Try to augment with UniFi controller metrics (5-minute resolution)
        try
        {
            // Pin the fresh scope to this service's already-resolved site rather than
            // re-resolving from the ambient HTTP context, which is not guaranteed here.
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
            var wifiService = scope.ServiceProvider.GetRequiredService<WiFiOptimizerService>();

            var granularity = (to - from).TotalHours > 48
                ? MetricGranularity.Hourly
                : MetricGranularity.FiveMinutes;

            var unifiMetrics = await wifiService.GetClientMetricsAsync(
                mac,
                new DateTimeOffset(from, TimeSpan.Zero),
                new DateTimeOffset(to, TimeSpan.Zero),
                granularity);

            if (unifiMetrics.Count == 0)
                return localEntries;

            // Build a set of local timestamps (rounded to minute) for dedup
            var localTimestamps = new HashSet<long>(
                localEntries.Select(e => e.Timestamp.Ticks / TimeSpan.TicksPerMinute));

            // Resolve AP names from device list for UniFi entries
            Dictionary<string, string>? apNameCache = null;
            try
            {
                var devices = await _connectionService.GetDiscoveredDevicesAsync();
                apNameCache = devices
                    .Where(d => !string.IsNullOrEmpty(d.Name))
                    .ToDictionary(d => d.Mac.ToLowerInvariant(), d => d.Name, StringComparer.OrdinalIgnoreCase);
            }
            catch { /* Best-effort AP name resolution */ }

            // Add UniFi entries that don't overlap with local data
            foreach (var m in unifiMetrics)
            {
                var ts = m.Timestamp.UtcDateTime;
                var minuteKey = ts.Ticks / TimeSpan.TicksPerMinute;

                if (!localTimestamps.Contains(minuteKey) && m.Signal.HasValue)
                {
                    var bandStr = m.Band switch
                    {
                        RadioBand.Band2_4GHz => "ng",
                        RadioBand.Band5GHz => "na",
                        RadioBand.Band6GHz => "6e",
                        _ => null
                    };

                    string? apName = null;
                    if (m.ApMac != null && apNameCache != null)
                        apNameCache.TryGetValue(m.ApMac, out apName);

                    localEntries.Add(new SignalHistoryEntry
                    {
                        Timestamp = ts,
                        SignalDbm = m.Signal,
                        Channel = m.Channel,
                        // ChannelWidth intentionally omitted - historic API returns AP width, not client's negotiated width
                        Band = bandStr,
                        Protocol = m.Protocol,
                        TxRateKbps = m.TxRateKbps,
                        RxRateKbps = m.RxRateKbps,
                        ApMac = m.ApMac,
                        ApName = apName,
                        DataSource = SignalDataSource.UniFiController
                    });
                }
            }

            // Re-sort by timestamp
            localEntries.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to augment signal history with UniFi data for {Mac}", mac);
        }

        return localEntries;
    }

    /// <summary>
    /// Get client connection events (connects, disconnects, roams) from UniFi controller.
    /// </summary>
    public async Task<List<ClientConnectionEvent>> GetConnectionEventsAsync(
        string mac, int limit = 200)
    {
        try
        {
            // Pinned like GetSignalHistoryWithUniFiAsync: don't re-resolve the site
            // from the ambient HTTP context in a fresh scope.
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
            var wifiService = scope.ServiceProvider.GetRequiredService<WiFiOptimizerService>();
            return await wifiService.GetClientConnectionEventsAsync(mac, limit);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get connection events for {Mac}", mac);
            return new List<ClientConnectionEvent>();
        }
    }

    /// <summary>
    /// Run daily cleanup if needed (called from polling timer).
    /// </summary>
    public async Task TryCleanupAsync()
    {
        if ((DateTime.UtcNow - _lastCleanup).TotalHours < 24)
            return;

        _lastCleanup = DateTime.UtcNow;
        await CleanupOldLogsAsync();
    }

    /// <summary>
    /// Update the most recent signal log entry with GPS coordinates.
    /// </summary>
    public async Task SubmitGpsAsync(string clientMac, double lat, double lng, int? accuracy)
    {
        await using var db = CreateSiteDb();

        var recent = await db.ClientSignalLogs
            .Where(l => l.ClientMac == clientMac && l.Latitude == null)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();

        if (recent != null)
        {
            recent.Latitude = lat;
            recent.Longitude = lng;
            recent.LocationAccuracyMeters = accuracy;
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Clean up old signal log entries beyond the retention period.
    /// </summary>
    public async Task CleanupOldLogsAsync(int retentionDays = 90)
    {
        await using var db = CreateSiteDb();

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // Delete in batches to avoid long-running transactions
        int totalDeleted = 0;
        int deleted;
        do
        {
            deleted = await db.ClientSignalLogs
                .Where(l => l.Timestamp < cutoff)
                .Take(1000)
                .ExecuteDeleteAsync();
            totalDeleted += deleted;
        } while (deleted == 1000);

        if (totalDeleted > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old signal log entries", totalDeleted);
        }

        // Downsample entries older than 24h to ~1/minute
        var downsampleCutoff = DateTime.UtcNow.AddHours(-24);
        var oldEntries = await db.ClientSignalLogs
            .Where(l => l.Timestamp < downsampleCutoff && l.Timestamp >= cutoff)
            .OrderBy(l => l.ClientMac)
            .ThenBy(l => l.Timestamp)
            .ToListAsync();

        if (oldEntries.Count == 0)
            return;

        var toDelete = new List<ClientSignalLog>();
        string? currentMac = null;
        DateTime lastKept = DateTime.MinValue;

        foreach (var entry in oldEntries)
        {
            if (entry.ClientMac != currentMac)
            {
                currentMac = entry.ClientMac;
                lastKept = entry.Timestamp;
                continue; // Keep first entry per MAC
            }

            // Keep entries with trace changes (TraceJson != null)
            if (entry.TraceJson != null)
            {
                lastKept = entry.Timestamp;
                continue;
            }

            // Keep at most one per minute
            if ((entry.Timestamp - lastKept).TotalSeconds < 55)
            {
                toDelete.Add(entry);
            }
            else
            {
                lastKept = entry.Timestamp;
            }
        }

        if (toDelete.Count > 0)
        {
            db.ClientSignalLogs.RemoveRange(toDelete);
            await db.SaveChangesAsync();
            _logger.LogInformation("Downsampled {Count} signal log entries older than 24h", toDelete.Count);
        }

        // Deduplicate trace JSON: keep only the first entry per consecutive TraceHash group
        await DeduplicateTraceJsonAsync(db);
    }

    /// <summary>
    /// Remove duplicate TraceJson entries where consecutive polls have the same TraceHash.
    /// Keeps only the first entry per consecutive hash group.
    /// </summary>
    private async Task DeduplicateTraceJsonAsync(NetworkOptimizerDbContext db)
    {
        var traceEntries = await db.ClientSignalLogs
            .Where(l => l.TraceJson != null)
            .OrderBy(l => l.ClientMac)
            .ThenBy(l => l.Timestamp)
            .Select(l => new { l.Id, l.ClientMac, l.TraceHash })
            .ToListAsync();

        if (traceEntries.Count == 0) return;

        var idsToNullify = new List<int>();
        string? prevMac = null;
        string? prevHash = null;

        foreach (var entry in traceEntries)
        {
            if (entry.ClientMac != prevMac)
            {
                // New client - keep this entry as the first trace
                prevMac = entry.ClientMac;
                prevHash = entry.TraceHash;
                continue;
            }

            if (entry.TraceHash == prevHash)
            {
                // Same hash as previous - this is a duplicate
                idsToNullify.Add(entry.Id);
            }
            else
            {
                // Hash changed - keep this entry
                prevHash = entry.TraceHash;
            }
        }

        if (idsToNullify.Count > 0)
        {
            // Null out TraceJson in batches
            foreach (var batch in idsToNullify.Chunk(500))
            {
                await db.ClientSignalLogs
                    .Where(l => batch.Contains(l.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.TraceJson, (string?)null));
            }
            _logger.LogInformation("Deduplicated {Count} trace entries with same consecutive hash", idsToNullify.Count);
        }
    }

    private async Task StoreSignalLogAsync(
        ClientIdentity identity,
        SignalPollResult poll,
        double? gpsLat,
        double? gpsLng,
        int? gpsAccuracy)
    {
        // Skip wired clients unless the trace changed (no Wi-Fi signal to record)
        if (identity.IsWired && !poll.TraceChanged) return;

        try
        {
            await using var db = CreateSiteDb();

            var log = new ClientSignalLog
            {
                Timestamp = poll.Timestamp,
                ClientMac = identity.Mac,
                ClientIp = identity.Ip,
                DeviceName = identity.DisplayName,
                SignalDbm = identity.SignalDbm,
                NoiseDbm = identity.NoiseDbm,
                Channel = identity.Channel,
                ChannelWidth = identity.ChannelWidth,
                Band = identity.Band,
                Protocol = identity.Protocol,
                TxRateKbps = identity.TxRateKbps,
                RxRateKbps = identity.RxRateKbps,
                IsMlo = identity.IsMlo,
                MloLinksJson = identity.MloLinks != null
                    ? JsonSerializer.Serialize(identity.MloLinks, JsonOptions) : null,
                ApMac = identity.ApMac,
                ApName = identity.ApName,
                ApModel = identity.ApModel,
                ApChannel = identity.ApChannel,
                ApTxPower = identity.ApTxPower,
                ApClientCount = identity.ApClientCount,
                ApRadioBand = identity.ApRadioBand,
                Latitude = gpsLat,
                Longitude = gpsLng,
                LocationAccuracyMeters = gpsAccuracy,
                TraceHash = poll.TraceHash,
                // Only store full trace JSON when the trace changed
                TraceJson = poll.TraceChanged && poll.PathAnalysis != null
                    ? JsonSerializer.Serialize(poll.PathAnalysis, JsonOptions) : null,
                HopCount = poll.PathAnalysis?.Path?.Hops?.Count,
                BottleneckLinkSpeedMbps = poll.PathAnalysis?.Path?.RealisticMaxMbps
            };

            db.ClientSignalLogs.Add(log);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store signal log for {Mac}", identity.Mac);
        }
    }

    /// <summary>
    /// Live poll for the page's fast tick. Prefers the access point's own AP Agent, which keeps
    /// reporting across a roam because the access point that just took the client is the authority
    /// on holding it, and falls back to <see cref="PollWiFiManOnlyAsync"/> - today's path, byte for
    /// byte - whenever no agent can answer. Returns null when neither source has anything.
    /// </summary>
    public async Task<ClientIdentity?> PollLiveClientAsync(string clientIp)
    {
        var fromAgent = await PollApAgentAsync(clientIp);
        return fromAgent ?? await PollWiFiManOnlyAsync(clientIp);
    }

    /// <summary>
    /// One AP Agent poll for the client at this IP, or null when the agent path cannot answer:
    /// no agents on the site, this access point not enrolled, the agent unreachable, or a roam
    /// still in flight. Every one of those is a fall-through to the console path, never an error.
    /// </summary>
    private async Task<ClientIdentity?> PollApAgentAsync(string clientIp)
    {
        if (_apAgentLive == null) return null;
        if (!_ipToMacCache.TryGetValue(clientIp, out var mac)) return null;
        if (_offlineIdentityCache.ContainsKey(clientIp)) return null;

        try
        {
            _lastApMacByClient.TryGetValue(mac, out var lastAp);
            var live = await _apAgentLive.PollAsync(
                _siteContext.Slug, mac, lastAp, FollowerFor(mac), DateTime.UtcNow);
            if (live == null) return null;

            var update = ApAgentClientIdentityMapper.ToLiveIdentity(live.Client, live.ApMac);
            if (update == null) return null;

            update.Ip = clientIp;
            // Only a roam needs the access point resolved again, so the steady state costs one
            // request to one access point and nothing else.
            if (!string.Equals(lastAp, live.ApMac, StringComparison.OrdinalIgnoreCase))
            {
                await EnrichWithApInfoAsync(update, live.ApMac);
                _lastApMacByClient[mac] = live.ApMac;
            }
            return update;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent live poll failed for {Ip}, using the console path", clientIp);
            return null;
        }
    }

    /// <summary>
    /// Overlays AP Agent data onto a full-poll identity, on top of whatever WiFiman supplied. Only
    /// fields the access point reported are replaced, so a value it does not carry keeps its
    /// console-sourced reading.
    /// </summary>
    private async Task OverlayApAgentDataAsync(ClientIdentity identity)
    {
        if (_apAgentLive == null || identity.IsWired || identity.IsOffline) return;
        if (string.IsNullOrEmpty(identity.Mac)) return;

        if (!string.IsNullOrEmpty(identity.ApMac))
            _lastApMacByClient.TryAdd(identity.Mac, identity.ApMac);

        try
        {
            _lastApMacByClient.TryGetValue(identity.Mac, out var lastAp);
            var live = await _apAgentLive.PollAsync(
                _siteContext.Slug, identity.Mac, lastAp ?? identity.ApMac, FollowerFor(identity.Mac), DateTime.UtcNow);
            if (live == null) return;

            var update = ApAgentClientIdentityMapper.ToLiveIdentity(live.Client, live.ApMac);
            if (update == null) return;

            ApplyLiveFields(identity, update);
            _lastApMacByClient[identity.Mac] = live.ApMac;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AP Agent overlay failed for {Mac}, using the console path", identity.Mac);
        }
    }

    /// <summary>
    /// Copies the live fields an AP Agent reported onto an identity, leaving absent ones alone.
    /// Shared by the full poll and the page's merge so both agree on what the agent owns.
    /// </summary>
    public static void ApplyLiveFields(ClientIdentity target, ClientIdentity update)
    {
        if (update.SignalDbm.HasValue) target.SignalDbm = update.SignalDbm;
        if (update.NoiseDbm.HasValue) target.NoiseDbm = update.NoiseDbm;
        if (update.Channel.HasValue) target.Channel = update.Channel;
        if (update.ChannelWidth.HasValue) target.ChannelWidth = update.ChannelWidth;
        if (!string.IsNullOrEmpty(update.Band)) target.Band = update.Band;
        if (!string.IsNullOrEmpty(update.Protocol)) target.Protocol = update.Protocol;
        if (update.TxRateKbps.HasValue) target.TxRateKbps = update.TxRateKbps;
        if (update.RxRateKbps.HasValue) target.RxRateKbps = update.RxRateKbps;
        if (update.Satisfaction.HasValue) target.Satisfaction = update.Satisfaction;

        target.IsMlo = update.IsMlo;
        if (update.MloLinks is { Count: > 0 }) target.MloLinks = update.MloLinks;
        else if (!update.IsMlo) target.MloLinks = null;

        if (!string.IsNullOrEmpty(update.ApMac)) target.ApMac = update.ApMac;
        if (!string.IsNullOrEmpty(update.ApName)) target.ApName = update.ApName;
        if (!string.IsNullOrEmpty(update.ApModel)) target.ApModel = update.ApModel;
        if (update.ApChannel.HasValue) target.ApChannel = update.ApChannel;
        if (update.ApTxPower.HasValue) target.ApTxPower = update.ApTxPower;
        if (update.ApEirp.HasValue) target.ApEirp = update.ApEirp;
        if (update.ApClientCount.HasValue) target.ApClientCount = update.ApClientCount;
        if (!string.IsNullOrEmpty(update.ApRadioBand)) target.ApRadioBand = update.ApRadioBand;

        target.HasApAgentData = true;
    }

    /// <summary>This client's roam-follow state, capped so a long-lived circuit cannot grow it.</summary>
    private ApAgentRoamFollower FollowerFor(string clientMac)
    {
        lock (_followers)
        {
            if (_followers.TryGetValue(clientMac, out var existing)) return existing;
            if (_followers.Count >= MaxTrackedFollowers) _followers.Clear();
            var follower = new ApAgentRoamFollower();
            _followers[clientMac] = follower;
            return follower;
        }
    }

    /// <summary>
    /// Lightweight 1s poll: only hits the WiFiman endpoint to refresh signal/channel/band/rates
    /// on an existing identity. No stat/sta, no trace, no storage. Returns null if WiFiman
    /// is unavailable or identity is unknown.
    /// </summary>
    public async Task<ClientIdentity?> PollWiFiManOnlyAsync(string clientIp)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        // Need a known MAC to have an existing identity
        if (!_ipToMacCache.TryGetValue(clientIp, out var clientMac))
            return null;

        // Fetch WiFiman data only
        try
        {
            var wifiman = await _connectionService.Client.GetWiFiManClientAsync(clientIp);
            if (wifiman?.Signal == null)
                return null;

            // Get the last known identity from the offline cache or return a minimal one
            // We don't call stat/sta here — just overlay WiFiman onto whatever we last knew
            if (_offlineIdentityCache.TryGetValue(clientIp, out var cached) && cached.IsOffline)
                return null;

            // Anything asking about this client between console polls should see the walk test's
            // numbers, not a value up to 30 seconds old.
            PublishWiFiManLive(clientMac, wifiman);

            // Build a lightweight update (caller merges into their existing _client)
            return new ClientIdentity
            {
                SignalDbm = wifiman.Signal,
                NoiseDbm = wifiman.Noise,
                Channel = wifiman.Channel,
                ChannelWidth = wifiman.ChannelWidth,
                Band = wifiman.RadioCode,
                Protocol = wifiman.RadioProtocol,
                TxRateKbps = wifiman.LinkUploadRateKbps,
                RxRateKbps = wifiman.LinkDownloadRateKbps,
                Satisfaction = wifiman.WiFiExperience,
                HasWiFiManData = true
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Publishes a WiFiman reading into the site's live client cache. WiFiman is the 1 Hz source on
    /// a site with no AP Agent; the AP Agent replaces it where it runs, so the two never race.
    /// </summary>
    private void PublishWiFiManLive(string clientMac, WiFiManClientResponse wifiman)
    {
        try
        {
            var live = _liveStats.GetFor(_siteContext.Slug);
            var prior = live.GetWifiClient(clientMac);

            // WiFiman reports the link, not who is serving it and not how much it is carrying.
            // Those come from whichever poller last knew, so publishing must not claim them as zero.
            live.RecordWifiClient(new WifiClientLiveSnapshot
            {
                ClientMac = clientMac,
                ApMac = _lastApMacByClient.TryGetValue(clientMac, out var ap) ? ap : prior?.ApMac ?? string.Empty,
                Band = wifiman.RadioCode ?? prior?.Band ?? string.Empty,
                Channel = wifiman.Channel ?? prior?.Channel,
                ChannelWidth = wifiman.ChannelWidth ?? prior?.ChannelWidth,
                SignalDbm = wifiman.Signal,
                NoiseDbm = wifiman.Noise,
                TxRateKbps = wifiman.LinkUploadRateKbps,
                RxRateKbps = wifiman.LinkDownloadRateKbps,
                TxThroughputBps = prior?.TxThroughputBps,
                RxThroughputBps = prior?.RxThroughputBps,
                Satisfaction = wifiman.WiFiExperience ?? prior?.Satisfaction,
                Rssi = prior?.Rssi,
                IsMlo = prior?.IsMlo ?? false,
                Source = WifiClientSource.WiFiMan,
                LastUpdate = DateTime.UtcNow,
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not publish WiFiman reading to the live cache");
        }
    }

    private ClientIdentity MapClientToIdentity(UniFiClientResponse client, string? displayName = null)
    {
        // Bridged UniFi ecosystem devices (e.g. a Protect camera on a UniFi Device Bridge) have
        // no user Name/display_name but expose a friendly ucore name like "[Camera] Front Door".
        var ucoreName = client.UnifiDeviceInfoFromUcore?.Name;
        return new ClientIdentity
        {
            Mac = client.Mac,
            // Prefer UniFi's system-selected display name (v2 active-clients, e.g. a
            // fingerprint-derived "Apple TV") over the raw stat/sta name, then the ucore device
            // name, matching Client Stats/the map so an unnamed device never shows as a MAC.
            Name = !string.IsNullOrEmpty(displayName) ? displayName
                 : !string.IsNullOrEmpty(client.Name) ? client.Name
                 : !string.IsNullOrEmpty(ucoreName) ? ucoreName : null,
            Hostname = !string.IsNullOrEmpty(client.Hostname) ? client.Hostname : null,
            Ip = client.Ip,
            IsWired = client.IsWired,
            SignalDbm = client.Signal,
            NoiseDbm = client.Noise,
            Channel = client.Channel,
            ChannelWidth = client.ChannelWidth,
            Band = client.Radio,
            Protocol = client.RadioProto,
            TxRateKbps = client.TxRate,
            RxRateKbps = client.RxRate,
            IsMlo = client.IsMlo ?? false,
            MloLinks = client.MloDetails,
            ApMac = client.ApMac,
            FixedApEnabled = client.FixedApEnabled == true,
            FixedApMac = client.FixedApMac,
            Oui = client.Oui,
            NetworkName = client.Network,
            Essid = client.Essid,
            Satisfaction = client.Satisfaction,
            SwitchMac = client.SwMac,
            SwitchPort = client.SwPort
        };
    }

    /// <summary>
    /// The latest counters for the port a wired client is plugged into. Null whenever the answer
    /// would be a guess: a wireless client, an unknown port, monitoring or InfluxDB not configured,
    /// or nothing polled that port. The page then shows what it always did.
    /// </summary>
    public async Task<WiredPortStats?> GetWiredPortStatsAsync(ClientIdentity? client)
    {
        if (client is not { IsWired: true } || string.IsNullOrEmpty(client.SwitchMac) || client.SwitchPort is not int port)
            return null;

        var live = _liveStats.GetFor(_siteContext.Slug);

        try
        {
            // The console's port number reaches the counters through InterfaceNameMaps, exactly as
            // the Port Statistics table resolves it. The port_id tag is the SNMP side's own id and
            // is not this number.
            //
            // Held because this runs on the live tick. Retired when the path trace changes, which is
            // what a client moving ports or switches shows up as.
            var ifNames = await PortIfNamesAsync(client.SwitchMac, port);

            if (ifNames.Count == 0)
            {
                _logger.LogDebug("No interface mapped to port {Port} on {Mac}", port, client.SwitchMac);
                return null;
            }

            // The live cache holds the same per-port snapshot the collector last wrote, so the page
            // and Live View agree and neither waits on a query. InfluxDB is the fallback for a site
            // whose collection runs elsewhere and publishes nothing here.
            var row = Match(live.GetPortStatsSnapshot(new[] { client.SwitchMac }));
            if (row == null)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
                var influx = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.MonitoringInfluxClient>();
                row = Match(await influx.QueryPortStatsAsync(new[] { client.SwitchMac }, at: null));
            }

            if (row == null)
            {
                _logger.LogDebug("No counters for {Mac} port {Port} ({IfNames})",
                    client.SwitchMac, port, string.Join(",", ifNames));
                return null;
            }

            return ToWiredPortStats(row, client, port, live);

            // The physical port, never a VLAN sub-interface sitting on it.
            NetworkOptimizer.Storage.Services.MonitoringInfluxClient.PortStatsPoint? Match(
                IReadOnlyList<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.PortStatsPoint> rows) =>
                rows.FirstOrDefault(r => ifNames.Contains(r.IfName, StringComparer.OrdinalIgnoreCase)
                                         && !r.IfName.Contains('.'));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Port stats unavailable for {Mac} port {Port}", client.SwitchMac, port);
            return null;
        }
    }

    /// <summary>
    /// One port reading in the client's terms: the port's inbound is what the client sent, so each
    /// pair is flipped. The port's SNMP rate leads, because the client-level figure standing in
    /// behind it only moves on the console poll.
    /// </summary>
    private static WiredPortStats ToWiredPortStats(
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient.PortStatsPoint row,
        ClientIdentity client,
        int port,
        MonitoringLiveStats live)
    {
        var own = live.GetWiredClient(client.Mac);
        return new WiredPortStats
        {
            SwitchName = client.SwitchName,
            Port = port,
            LinkUp = row.OperStatus.HasValue ? row.OperStatus == 1 : null,
            LinkSpeedBps = row.SpeedBps,
            DownloadBps = row.RateOutBps ?? own?.TxThroughputBps,
            UploadBps = row.RateInBps ?? own?.RxThroughputBps,
            ErrorsToClient = row.ErrorsOut,
            ErrorsFromClient = row.ErrorsIn,
            DropsToClient = row.DiscardsOut,
            DropsFromClient = row.DiscardsIn,
            PacketsToClient = row.UcastPktsOut,
            PacketsFromClient = row.UcastPktsIn,
            At = row.Time,
        };
    }

    /// <summary>The SNMP interface names behind a console port number, cached per port.</summary>
    private async Task<List<string>> PortIfNamesAsync(string switchMac, int port)
    {
        var mac = switchMac.ToLowerInvariant();
        if (_portIfNames.TryGetValue((mac, port), out var ifNames)) return ifNames;
        await using var db = CreateSiteDb();
        ifNames = await db.InterfaceNameMaps.AsNoTracking()
            .Where(m => m.DeviceMac.ToLower() == mac && m.PortNumber == port)
            .Select(m => m.IfName)
            .ToListAsync();
        _portIfNames[(mac, port)] = ifNames;
        return ifNames;
    }

    /// <summary>
    /// The client's throughput over a window, in the same terms and from the same counters as the
    /// live figure: the port's stored rates for a wired client (its own series when the port has
    /// none), the client's own points for a wireless one. A point with no rate is an idle client,
    /// not a gap.
    /// </summary>
    public async Task<IReadOnlyList<ThroughputSample>> GetThroughputHistoryAsync(ClientIdentity client, DateTime from, DateTime to)
    {
        if (string.IsNullOrEmpty(client.Mac)) return Array.Empty<ThroughputSample>();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
            var influx = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.MonitoringInfluxClient>();

            if (client.IsWired)
            {
                if (!string.IsNullOrEmpty(client.SwitchMac) && client.SwitchPort is int port)
                {
                    var ifNames = await PortIfNamesAsync(client.SwitchMac, port);
                    if (ifNames.Count > 0)
                    {
                        var rows = await influx.QueryInterfaceRatesRawAsync(client.SwitchMac, from, to);
                        var portRows = rows
                            .Where(r => ifNames.Contains(r.IfName, StringComparer.OrdinalIgnoreCase) && !r.IfName.Contains('.'))
                            .Select(r => new ThroughputSample(r.Time, r.RateOutBps, r.RateInBps))
                            .ToList();
                        if (portRows.Count > 0) return portRows;
                    }
                }
                var own = await influx.QueryClientThroughputAsync("wired_client", client.Mac, from, to);
                return own.Select(r => new ThroughputSample(r.Time, r.TxThroughputBps ?? 0, r.RxThroughputBps ?? 0)).ToList();
            }

            var wifi = await influx.QueryClientThroughputAsync("wifi_client", client.Mac, from, to);
            return wifi.Select(r => new ThroughputSample(r.Time, r.TxThroughputBps ?? 0, r.RxThroughputBps ?? 0)).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Throughput history unavailable for {Mac}", client.Mac);
            return Array.Empty<ThroughputSample>();
        }
    }

    /// <summary>How far back usage is read when the page asks for everything.</summary>
    private static readonly TimeSpan MaxUsageWindow = TimeSpan.FromDays(30);

    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _cache;

    /// <summary>
    /// The site-wide DPI response is one call for every client on the site, and its totals move
    /// by the minute at most - so one fetch serves every Client Performance page and Bandwidth
    /// Hogs card for this long. Graded by window length: a short window refreshes every minute,
    /// but a week or month of DPI is a multi-second console call, and an open long-window view
    /// re-asking it every minute is the console hammering this cache exists to prevent.
    /// </summary>
    private static TimeSpan TrafficCacheLife(TimeSpan window) =>
        window <= TimeSpan.FromHours(6) ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);

    /// <summary>The DPI category UniFi Network files traffic it could not identify under.</summary>
    private const int DpiUnidentifiedCategory = 255;

    /// <summary>The least time between two DPI fetches for one site, whoever asks.</summary>
    private static readonly TimeSpan TrafficFetchSpacing = TimeSpan.FromSeconds(2);

    private sealed class SiteTrafficGate
    {
        public readonly SemaphoreSlim Lock = new(1, 1);
        public DateTime LastFetch = DateTime.MinValue;
    }

    // Static because the service is scoped: every circuit on a site must share one gate.
    private static readonly ConcurrentDictionary<string, SiteTrafficGate> TrafficGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A client's data usage over a window, WAN and LAN side by side. WAN comes from UniFi Network's
    /// DPI tally (gateway-side, so WAN-only for wired and Wi-Fi alike) summed into the window's
    /// bucket; LAN from our own counters at the same bucket - the switch port's for a wired client
    /// (its own series when the port is unmapped), the access point's for wireless - so the two
    /// charts line up bar for bar.
    /// </summary>
    public async Task<ClientDataUsage> GetDataUsageAsync(ClientIdentity client, DateTime from, DateTime to)
    {
        if (from < to - MaxUsageWindow) from = to - MaxUsageWindow;
        var span = to - from;
        // Raw counters (and their 5-minute buckets) only up to the rollup top-up's own reach: a
        // counter query reads every point in the window (client identity is a field, not a tag),
        // so longer windows read the hourly rollup and chart hourly.
        var bucket = span <= TimeSpan.FromHours(2) ? TimeSpan.FromMinutes(5)
            : span <= TimeSpan.FromHours(48) ? TimeSpan.FromHours(1)
            : TimeSpan.FromDays(1);
        var usage = new ClientDataUsage { From = from, To = to, Bucket = bucket, LanIsPortTotal = client.IsWired };
        if (string.IsNullOrEmpty(client.Mac)) return usage;

        try
        {
            // The DPI tally, the same one the Applications list below is drawn from, so the two
            // agree. stat/report/*.user is not usable here: for a Wi-Fi client it is the access
            // point's count, LAN + WAN, which put a NAS speed test in the WAN column.
            if (_connectionService.IsConnected && _connectionService.Client != null)
            {
                var rate = await _connectionService.Client.GetClientTrafficRateAsync(client.Mac, from, to);
                usage.Wan = BucketTrafficRate(rate, bucket);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WAN usage unavailable for {Mac}", client.Mac);
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
            var influx = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.MonitoringInfluxClient>();
            List<string>? ifNames = null;
            if (client.IsWired && !string.IsNullOrEmpty(client.SwitchMac) && client.SwitchPort is int port)
                ifNames = (await PortIfNamesAsync(client.SwitchMac, port)).Where(n => !n.Contains('.')).ToList();

            var points = bucket < TimeSpan.FromHours(1)
                ? await LanUsageFromCountersAsync(influx, client, ifNames, from, to, bucket)
                : await LanUsageFromRollupAsync(influx, client, ifNames, from, to);
            // No rollup yet, or one that does not reach the window's start (a rebuild rolls
            // newest first, and partial coverage reads silently low): the counters still answer,
            // up to a week - measured at ~2 s for a wireless client's 7 days, and a wired port's
            // series filter pushes down to storage. Past a week the rollup is the only answer,
            // and it fills in behind on its own.
            if (bucket >= TimeSpan.FromHours(1) && span <= TimeSpan.FromDays(7)
                && (points.Count == 0 || points[0].Time > from.AddHours(1)))
                points = await LanUsageFromCountersAsync(influx, client, ifNames, from, to, TimeSpan.FromHours(1));

            var lan = points.Select(p => new UsageBucket(p.Time, p.ToClientBytes, p.FromClientBytes)).ToList();
            usage.Lan = bucket >= TimeSpan.FromDays(1) ? SumToDays(lan, usage.Wan) : lan;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LAN usage unavailable for {Mac}", client.Mac);
        }

        return usage;
    }

    /// <summary>The live counters, differenced on read; fine for hours, expensive for days.</summary>
    private static Task<IReadOnlyList<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.ByteUsagePoint>> LanUsageFromCountersAsync(
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient influx, ClientIdentity client, List<string>? ifNames,
        DateTime from, DateTime to, TimeSpan bucket)
    {
        if (client.IsWired)
        {
            return ifNames is { Count: > 0 }
                ? influx.QueryPortByteUsageAsync(client.SwitchMac!, ifNames, from, to, bucket)
                : Task.FromResult<IReadOnlyList<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.ByteUsagePoint>>(
                    Array.Empty<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.ByteUsagePoint>());
        }
        return influx.QueryWifiClientByteUsageAsync(client.Mac, from, to, bucket);
    }

    /// <summary>
    /// The hourly rollup for the complete hours, topped up from the live counters for the hour in
    /// progress so the newest bar is never empty.
    /// </summary>
    private static async Task<IReadOnlyList<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.ByteUsagePoint>> LanUsageFromRollupAsync(
        NetworkOptimizer.Storage.Services.MonitoringInfluxClient influx, ClientIdentity client, List<string>? ifNames,
        DateTime from, DateTime to)
    {
        var hourStart = new DateTime(to.Year, to.Month, to.Day, to.Hour, 0, 0, DateTimeKind.Utc);
        var rolled = client.IsWired
            ? (ifNames is { Count: > 0 }
                ? await influx.QueryPortUsageRollupAsync(client.SwitchMac!, ifNames, from, hourStart)
                : Array.Empty<NetworkOptimizer.Storage.Services.MonitoringInfluxClient.ByteUsagePoint>())
            : await influx.QueryWifiClientUsageRollupAsync(client.Mac, from, hourStart);
        if (rolled.Count == 0) return rolled;
        // From the first hour the rollup has not written, so the hour just ended is not a gap for
        // the minutes until its rollup lands - never more than two hours back, which is the scan
        // the rollup exists to avoid.
        var topUpFrom = rolled[^1].Time.AddHours(1);
        if (topUpFrom < hourStart.AddHours(-2)) topUpFrom = hourStart.AddHours(-2);
        var tail = await LanUsageFromCountersAsync(influx, client, ifNames, topUpFrom, to, TimeSpan.FromHours(1));
        return rolled.Concat(tail).ToList();
    }

    /// <summary>
    /// Hourly buckets summed into days on the same day boundaries UniFi's daily report used, so
    /// the two charts line up bar for bar; UTC days when there is no report to follow.
    /// </summary>
    private static List<UsageBucket> SumToDays(List<UsageBucket> hourly, IReadOnlyList<UsageBucket> wanDays)
    {
        var edges = wanDays.Select(w => w.Time).OrderBy(t => t).ToList();
        DateTime EdgeFor(DateTime t)
        {
            if (edges.Count == 0) return t.Date;
            var idx = edges.FindLastIndex(e => e <= t);
            if (idx >= 0) return edges[idx];
            // Before the report's first day: the same day boundary, a day earlier.
            var first = edges[0];
            while (first > t) first = first.AddDays(-1);
            return first;
        }
        return hourly
            .GroupBy(h => EdgeFor(h.Time))
            .Select(g => new UsageBucket(g.Key, g.Sum(h => h.DownloadBytes), g.Sum(h => h.UploadBytes)))
            .OrderBy(b => b.Time)
            .ToList();
    }

    /// <summary>
    /// The DPI report for a window if a reader has already fetched it and it is still cached; never
    /// asks the console. For callers on a latency-sensitive path (the map's topology rebuild) that
    /// can do without the answer this once.
    /// </summary>
    public UniFiClientTrafficResponse? PeekSiteTraffic(DateTime from, DateTime to)
    {
        if (from < to - MaxUsageWindow) from = to - MaxUsageWindow;
        return _cache != null && _cache.TryGetValue(TrafficCacheKey(from, to), out UniFiClientTrafficResponse? cached) ? cached : null;
    }

    // Keyed on the window rounded to the cache life, so a page reloading every 30 s reuses the
    // same response until the window itself has moved on.
    private string TrafficCacheKey(DateTime from, DateTime to)
    {
        var slot = (long)(to - DateTime.UnixEpoch).TotalMinutes / (long)TrafficCacheLife(to - from).TotalMinutes;
        return $"client-traffic:{_siteContext.Slug}:{(long)(to - from).TotalMinutes}:{slot}";
    }

    /// <summary>
    /// UniFi Network's DPI report for every client on the site over a window: one console call,
    /// shared by every reader for <see cref="TrafficCacheFor"/>. Null when the console cannot answer.
    /// </summary>
    public async Task<UniFiClientTrafficResponse?> GetSiteTrafficAsync(DateTime from, DateTime to, CancellationToken ct = default)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null) return null;
        if (from < to - MaxUsageWindow) from = to - MaxUsageWindow;
        var key = TrafficCacheKey(from, to);
        if (_cache != null && _cache.TryGetValue(key, out UniFiClientTrafficResponse? cached) && cached != null)
            return cached;

        // One fetch at a time per site, at most one every TrafficFetchSpacing: the callers are
        // every open Client Performance page and Bandwidth Hogs card, and a burst of misses (a
        // playhead crossing several windows) must not become a burst of console calls.
        var gate = TrafficGates.GetOrAdd(_siteContext.Slug, _ => new SiteTrafficGate());
        await gate.Lock.WaitAsync(ct);
        try
        {
            if (_cache != null && _cache.TryGetValue(key, out cached) && cached != null)
                return cached;
            var wait = gate.LastFetch + TrafficFetchSpacing - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            gate.LastFetch = DateTime.UtcNow;
            var traffic = await _connectionService.Client.GetClientTrafficByAppAsync(from, to, ct);
            // A window that ended a while ago will not change; playback re-asks for it far more
            // often than a live page asks for the present.
            var life = to < DateTime.UtcNow - TimeSpan.FromMinutes(10) ? TimeSpan.FromHours(1) : TrafficCacheLife(to - from);
            if (traffic != null && _cache != null) _cache.Set(key, traffic, life);
            return traffic;
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>
    /// The client's WAN traffic by application over a window, from UniFi Network's DPI, named through
    /// the embedded catalog. Largest first. Two rows are not applications and say so: what UniFi
    /// Network could not identify at all (category 255, its own "Unidentified"), and an application
    /// it did identify but our catalog has no name for, shown by id so the gap reads as ours.
    /// </summary>
    public async Task<IReadOnlyList<AppUsageRow>> GetAppUsageAsync(ClientIdentity client, DateTime from, DateTime to)
    {
        if (string.IsNullOrEmpty(client.Mac)) return Array.Empty<AppUsageRow>();
        try
        {
            var traffic = await GetSiteTrafficAsync(from, to);
            var mine = traffic?.ClientUsageByApp.FirstOrDefault(c => string.Equals(c.Client?.Mac, client.Mac, StringComparison.OrdinalIgnoreCase));
            if (mine == null) return Array.Empty<AppUsageRow>();
            return BuildAppRows(mine.UsageByApp);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "App usage unavailable for {Mac}", client.Mac);
            return Array.Empty<AppUsageRow>();
        }
    }

    /// <summary>The Applications rows for one client's DPI usage, largest first; see <see cref="GetAppUsageAsync"/>.</summary>
    public static List<AppUsageRow> BuildAppRows(IEnumerable<UniFiAppUsage> usage) => usage
        .Where(u => u.BytesReceived > 0 || u.BytesTransmitted > 0)
        .Select(u => u.Category == DpiUnidentifiedCategory
            ? new AppUsageRow("Unidentified", "", null, DpiCatalog.IconClass(u.Category, u.Application),
                u.BytesReceived, u.BytesTransmitted, u.ActivitySeconds,
                Note: "UniFi Network could not identify this traffic")
            : DpiCatalog.AppName(u.Category, u.Application) is { } name
                ? new AppUsageRow(name, DpiCatalog.CategoryName(u.Category) ?? "",
                    DpiCatalog.IconDomain(u.Category, u.Application), DpiCatalog.IconClass(u.Category, u.Application),
                    u.BytesReceived, u.BytesTransmitted, u.ActivitySeconds)
                : new AppUsageRow($"Application {u.Application}", DpiCatalog.CategoryName(u.Category) ?? "",
                    null, DpiCatalog.IconClass(u.Category, u.Application),
                    u.BytesReceived, u.BytesTransmitted, u.ActivitySeconds,
                    Note: "UniFi Network knows this application; our catalog has no name for it yet"))
        .OrderByDescending(r => r.TotalBytes)
        .ToList();

    /// <summary>
    /// The console's 5-minute WAN buckets summed into the buckets the window is drawn in, in time
    /// order. The console stamps a bucket with its END (a 12:28 test lands in the one labeled
    /// 12:30), while our LAN buckets are stamped with their start, so each is filed by its start
    /// to line the two charts up. A bucket's bytes are its rate times its length.
    /// </summary>
    public static List<UsageBucket> BucketTrafficRate(IEnumerable<UniFiTrafficRateBucket> rate, TimeSpan bucket)
    {
        var ticks = bucket.Ticks;
        return rate
            .Select(b => (Start: b.Time.AddSeconds(-b.IntervalSeconds), Bucket: b))
            .GroupBy(x => new DateTime(x.Start.Ticks - x.Start.Ticks % ticks, DateTimeKind.Utc), x => x.Bucket)
            .Select(g => new UsageBucket(g.Key, g.Sum(b => b.DownloadBytes), g.Sum(b => b.UploadBytes)))
            .OrderBy(b => b.Time)
            .ToList();
    }

    /// <summary>
    /// Throughput for a wireless client, as download and upload from the client's point of view.
    ///
    /// The live cache first: that reading is resolved from the client's own byte counters on the
    /// same poll that produced the PHY rates beside it, so the two move together. InfluxDB only
    /// when nothing has published one, where the newest stored sample can be a write interval old.
    /// </summary>
    public async Task<(double? DownloadBps, double? UploadBps, DateTime At)?> GetWifiThroughputAsync(ClientIdentity? client)
    {
        if (client is not { IsWired: false, IsOffline: false } || string.IsNullOrEmpty(client.Mac))
            return null;

        // The access point transmits what the client downloads, so each pair is flipped on the way out.
        var snapshot = _liveStats.GetFor(_siteContext.Slug).GetWifiClient(client.Mac);
        if (snapshot is { } s && (s.TxThroughputBps.HasValue || s.RxThroughputBps.HasValue))
            return (s.TxThroughputBps, s.RxThroughputBps, s.LastUpdate);

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<SiteContextService>().OverrideSite(_siteContext.Slug);
            var influx = scope.ServiceProvider.GetRequiredService<NetworkOptimizer.Storage.Services.MonitoringInfluxClient>();

            var now = DateTime.UtcNow;
            var rows = await influx.QueryClientThroughputAsync("wifi_client", client.Mac, now.AddMinutes(-3), now);
            var last = rows.LastOrDefault(r => r.TxThroughputBps.HasValue || r.RxThroughputBps.HasValue);
            if (last == null)
                return null;

            return (last.TxThroughputBps, last.RxThroughputBps, last.Time);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Wi-Fi throughput unavailable for {Mac}", client.Mac);
            return null;
        }
    }

    /// <summary>Names the switch or gateway a wired client is plugged into.</summary>
    private async Task EnrichWithSwitchNameAsync(ClientIdentity identity)
    {
        if (!identity.IsWired || string.IsNullOrEmpty(identity.SwitchMac))
            return;

        try
        {
            var devices = await _connectionService.GetDiscoveredDevicesAsync();
            var sw = devices.FirstOrDefault(d =>
                string.Equals(d.Mac, identity.SwitchMac, StringComparison.OrdinalIgnoreCase));
            identity.SwitchName = !string.IsNullOrWhiteSpace(sw?.Name) ? sw!.Name : sw?.Model;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not name the switch for {Mac}", identity.SwitchMac);
        }
    }

    /// <summary>
    /// Overlay WiFiman realtime data onto an existing ClientIdentity.
    /// WiFiman provides more-realtime signal/channel/band/rate data than stat/sta.
    /// Falls back silently if the endpoint is unavailable (wired clients, older firmware, etc.).
    /// </summary>
    private async Task OverlayWiFiManDataAsync(ClientIdentity identity, string clientIp)
    {
        if (identity.IsWired || _connectionService.Client == null)
            return;

        try
        {
            var wifiman = await _connectionService.Client.GetWiFiManClientAsync(clientIp);
            if (wifiman == null)
                return;

            // Overlay signal fields - WiFiman values take priority over stat/sta
            if (wifiman.Signal.HasValue)
                identity.SignalDbm = wifiman.Signal;
            if (wifiman.Noise.HasValue)
                identity.NoiseDbm = wifiman.Noise;
            if (wifiman.Channel.HasValue)
                identity.Channel = wifiman.Channel;
            if (wifiman.ChannelWidth.HasValue)
                identity.ChannelWidth = wifiman.ChannelWidth;
            if (!string.IsNullOrEmpty(wifiman.RadioCode))
                identity.Band = wifiman.RadioCode;
            if (!string.IsNullOrEmpty(wifiman.RadioProtocol))
                identity.Protocol = wifiman.RadioProtocol;
            if (wifiman.WiFiExperience.HasValue)
                identity.Satisfaction = wifiman.WiFiExperience;

            // WiFiman reports from client perspective: download = client RX, upload = client TX
            // Our TxRateKbps/RxRateKbps are from AP perspective: Tx = AP→client, Rx = client→AP
            if (wifiman.LinkUploadRateKbps.HasValue)
                identity.TxRateKbps = wifiman.LinkUploadRateKbps;
            if (wifiman.LinkDownloadRateKbps.HasValue)
                identity.RxRateKbps = wifiman.LinkDownloadRateKbps;

            identity.HasWiFiManData = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WiFiman overlay failed for {Ip}, using stat/sta data", clientIp);
        }
    }

    private async Task EnrichWithApInfoAsync(ClientIdentity identity, string? apMac)
    {
        if (string.IsNullOrEmpty(apMac) || !_connectionService.IsConnected)
            return;

        try
        {
            var devices = await _connectionService.GetDiscoveredDevicesAsync();
            var ap = devices.FirstOrDefault(d =>
                d.Mac.Equals(apMac, StringComparison.OrdinalIgnoreCase));

            if (ap == null)
                return;

            identity.ApName = ap.Name;
            identity.ApModel = ap.FriendlyModelName;

            // Find the radio matching the client's band
            if (ap.RadioTable != null && !string.IsNullOrEmpty(identity.Band))
            {
                var radio = ap.RadioTable.FirstOrDefault(r =>
                    r.Radio.Equals(identity.Band, StringComparison.OrdinalIgnoreCase));

                if (radio != null)
                {
                    identity.ApRadioBand = radio.Radio;
                    if (radio.Channel is int ch)
                        identity.ApChannel = ch;
                    else if (radio.Channel is long chL)
                        identity.ApChannel = (int)chL;

                    // Compute EIRP from radio config antenna gain + stats TX power
                    if (radio.AntennaGain.HasValue)
                    {
                        var radioStats = ap.RadioTableStats?.FirstOrDefault(r =>
                            r.Radio != null && r.Radio.Equals(identity.Band, StringComparison.OrdinalIgnoreCase));
                        if (radioStats?.TxPower != null)
                            identity.ApEirp = radioStats.TxPower.Value + radio.AntennaGain.Value;
                    }
                }
            }

            // Resolve fixed AP name
            if (identity.FixedApEnabled && !string.IsNullOrEmpty(identity.FixedApMac))
            {
                var fixedAp = devices.FirstOrDefault(d =>
                    d.Mac.Equals(identity.FixedApMac, StringComparison.OrdinalIgnoreCase));
                identity.FixedApName = fixedAp?.Name;
            }

            // Get TX power and client count from radio stats
            if (ap.RadioTableStats != null && !string.IsNullOrEmpty(identity.Band))
            {
                var radioStats = ap.RadioTableStats.FirstOrDefault(r =>
                    r.Radio != null && r.Radio.Equals(identity.Band, StringComparison.OrdinalIgnoreCase));

                if (radioStats != null)
                {
                    identity.ApTxPower = radioStats.TxPower;
                    identity.ApClientCount = radioStats.NumSta;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enrich AP info for {ApMac}", apMac);
        }
    }

    /// <summary>
    /// Seed the in-memory trace hash dictionary from the DB so restarts don't
    /// cause false "path changed" entries.
    /// </summary>
    private async Task SeedTraceHashesAsync()
    {
        try
        {
            await using var db = CreateSiteDb();
            // Seed from entries that have TraceJson stored (not just a hash).
            // Entries with a hash but no TraceJson were written without a snapshot,
            // so seeding from them would prevent the next poll from storing one.
            var latestHashes = await db.ClientSignalLogs
                .Where(l => l.TraceHash != null && l.TraceJson != null)
                .GroupBy(l => l.ClientMac)
                .Select(g => new
                {
                    Mac = g.Key,
                    TraceHash = g.OrderByDescending(l => l.Timestamp).First().TraceHash
                })
                .ToListAsync();

            foreach (var entry in latestHashes)
            {
                if (entry.TraceHash != null)
                    _lastTraceHashes.TryAdd(entry.Mac, entry.TraceHash);
            }
            _traceHashesSeeded = true;

            _logger.LogDebug("Seeded trace hashes for {Count} clients from DB", latestHashes.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to seed trace hashes from DB");
            _traceHashesSeeded = true; // Don't retry on failure
        }
    }

    /// <summary>
    /// Compute a hash of the structural path identity (device order, MACs, types, ports).
    /// Excludes dynamic data like signal strength, TX/RX rates, timestamps, and firmware.
    /// </summary>
    private static string ComputeTraceHash(NetworkPath path)
    {
        var sb = new StringBuilder();
        sb.Append(path.SourceMac).Append('|').Append(path.DestinationMac).Append('|');
        sb.Append(path.RequiresRouting).Append('|');
        foreach (var hop in path.Hops)
        {
            sb.Append(hop.Order).Append(',');
            sb.Append(hop.Type).Append(',');
            sb.Append(hop.DeviceMac).Append(',');
            sb.Append(hop.IngressPort).Append(',');
            sb.Append(hop.EgressPort).Append(',');
            sb.Append(hop.IsWirelessIngress).Append(',');
            sb.Append(hop.IsWirelessEgress).Append('|');
        }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }
}
