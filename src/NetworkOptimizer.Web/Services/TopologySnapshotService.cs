using System.Collections.Concurrent;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Interface for capturing and retrieving wireless rate snapshots during speed tests.
/// </summary>
public interface ITopologySnapshotService
{
    /// <summary>
    /// Captures a wireless rate snapshot for the given client IP.
    /// This invalidates the topology cache first to ensure fresh data.
    /// </summary>
    Task CaptureSnapshotAsync(string siteSlug, string clientIp);

    /// <summary>
    /// Gets the snapshot for a client IP, if it exists and hasn't expired.
    /// </summary>
    WirelessRateSnapshot? GetSnapshot(string siteSlug, string clientIp);

    /// <summary>
    /// Removes the snapshot for a client IP.
    /// </summary>
    void RemoveSnapshot(string siteSlug, string clientIp);
}

/// <summary>
/// Stores wireless rate snapshots captured during speed tests.
/// Snapshots are keyed by client IP and auto-expire after 2 minutes.
/// </summary>
public class TopologySnapshotService : ITopologySnapshotService
{
    private readonly IUniFiClientProvider _clientProvider;
    private readonly MonitoringLiveStatsRegistry _liveStats;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TopologySnapshotService> _logger;

    private readonly ConcurrentDictionary<string, SnapshotEntry> _snapshots = new();

    /// <summary>
    /// Keyed by site as well as client. This is a singleton across every site, and client IPs repeat
    /// between them: two sites both running 192.168.1.x would otherwise read each other's snapshots.
    /// </summary>
    private static string Key(string siteSlug, string clientIp) => $"{siteSlug}|{clientIp}";
    private static readonly TimeSpan SnapshotExpiration = TimeSpan.FromMinutes(2);

    public TopologySnapshotService(
        IUniFiClientProvider clientProvider,
        MonitoringLiveStatsRegistry liveStats,
        INetworkPathAnalyzer pathAnalyzer,
        ILoggerFactory loggerFactory,
        ILogger<TopologySnapshotService> logger)
    {
        _clientProvider = clientProvider;
        _liveStats = liveStats;
        _pathAnalyzer = pathAnalyzer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>How recent a cached reading must be to displace a freshly fetched one.</summary>
    private static readonly TimeSpan LiveRateMaxAge = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Replaces console-sourced client rates with anything the site's live cache holds more recently,
    /// so a trace taken during a walk test carries the same numbers the page is showing.
    /// </summary>
    private void OverlayLiveRates(string siteSlug, WirelessRateSnapshot snapshot)
    {
        try
        {
            var live = _liveStats.GetFor(siteSlug);
            var now = DateTime.UtcNow;

            foreach (var mac in snapshot.ClientRates.Keys.ToList())
            {
                var snap = live.GetWifiClient(mac);
                if (snap == null) continue;

                // The capture above deliberately bypassed the topology cache to get current values,
                // so the overlay has to clear a real bar rather than simply existing. A Console
                // entry is that same wifi tier data on an independent clock, and a stale one can be
                // older still, so neither may displace what was just fetched.
                if (snap.Source == WifiClientSource.Console) continue;
                if (now - snap.LastUpdate > LiveRateMaxAge) continue;

                // Both directions or neither: taking one and writing 0 for the other replaces a
                // real console rate with silence.
                if (snap.TxRateKbps is not > 0 || snap.RxRateKbps is not > 0) continue;

                snapshot.ClientRates[mac] = (
                    (int)snap.TxRateKbps.Value,
                    (int)snap.RxRateKbps.Value,
                    string.IsNullOrEmpty(snap.ApMac) ? snapshot.ClientRates[mac].Item3 : snap.ApMac);
            }
        }
        catch (Exception ex)
        {
            // The console values are already in hand; a cache miss must not cost the snapshot.
            _logger.LogDebug(ex, "Could not overlay live rates onto the snapshot");
        }
    }

    /// <summary>
    /// Captures a wireless rate snapshot for the given client IP.
    /// This invalidates the topology cache first to ensure fresh data.
    /// </summary>
    public async Task CaptureSnapshotAsync(string siteSlug, string clientIp)
    {
        try
        {
            _logger.LogDebug("Capturing wireless rate snapshot for {ClientIp}", clientIp);

            // Invalidate cache to force fresh fetch
            _pathAnalyzer.InvalidateTopologyCache();

            // Check if connected
            if (!_clientProvider.IsConnected || _clientProvider.Client == null)
            {
                _logger.LogWarning("Cannot capture snapshot - not connected to UniFi controller");
                return;
            }

            // Fetch fresh topology
            var discovery = new UniFiDiscovery(
                _clientProvider.Client,
                _loggerFactory.CreateLogger<UniFiDiscovery>());

            var topology = await discovery.DiscoverTopologyAsync(useCache: false);
            if (topology == null)
            {
                _logger.LogWarning("Cannot capture snapshot - topology discovery failed");
                return;
            }

            // Extract wireless rates
            var snapshot = new WirelessRateSnapshot();

            // Extract wireless client rates (including AP MAC for roam detection)
            foreach (var client in topology.Clients.Where(c => !c.IsWired && !string.IsNullOrEmpty(c.Mac)))
            {
                if (client.TxRate > 0 || client.RxRate > 0)
                {
                    snapshot.ClientRates[client.Mac] = (client.TxRate, client.RxRate, client.ConnectedToDeviceMac);
                }
            }

            // Extract mesh device uplink rates
            foreach (var device in topology.Devices.Where(d =>
                !string.IsNullOrEmpty(d.Mac) &&
                d.UplinkType == "wireless" &&
                (d.UplinkTxRateKbps > 0 || d.UplinkRxRateKbps > 0)))
            {
                snapshot.MeshUplinkRates[device.Mac] = (device.UplinkTxRateKbps, device.UplinkRxRateKbps);
            }

            // Anything the site's live cache knows more recently wins. Client Performance polls the
            // walked client many times a second, so during a walk test the console's copy of that
            // client is the stalest number in the room.
            OverlayLiveRates(siteSlug, snapshot);

            // Also poll WiFiman for the target client's realtime rates
            var targetClient = topology.Clients.FirstOrDefault(c => c.IpAddress == clientIp);
            await EnrichWithWiFiManAsync(snapshot, clientIp, targetClient);

            // Store snapshot (overwrite any existing for this IP)
            _snapshots[Key(siteSlug, clientIp)] = new SnapshotEntry(snapshot, DateTime.UtcNow);

            if (targetClient != null && !targetClient.IsWired && snapshot.ClientRates.TryGetValue(targetClient.Mac, out var targetRates))
            {
                var wifimanNote = snapshot.WiFiManData.ContainsKey(clientIp) ? " (WiFiman enriched)" : "";
                _logger.LogDebug(
                    "Captured snapshot for {ClientIp} ({Name}): Tx={Tx}Kbps, Rx={Rx}Kbps ({Total} clients, {Mesh} mesh){WiFiMan}",
                    clientIp, targetClient.Name ?? "Unknown", targetRates.TxKbps, targetRates.RxKbps,
                    snapshot.ClientRates.Count, snapshot.MeshUplinkRates.Count, wifimanNote);
            }
            else
            {
                _logger.LogDebug(
                    "Captured snapshot for {ClientIp}: {ClientCount} wireless clients, {MeshCount} mesh devices",
                    clientIp, snapshot.ClientRates.Count, snapshot.MeshUplinkRates.Count);
            }

            // Cleanup expired snapshots (lazy cleanup)
            CleanupExpiredSnapshots();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error capturing wireless rate snapshot for {ClientIp}", clientIp);
        }
    }

    /// <summary>
    /// Gets the snapshot for a client IP, if it exists and hasn't expired.
    /// </summary>
    public WirelessRateSnapshot? GetSnapshot(string siteSlug, string clientIp)
    {
        if (_snapshots.TryGetValue(Key(siteSlug, clientIp), out var entry))
        {
            // Check if expired
            if (DateTime.UtcNow - entry.CapturedAt > SnapshotExpiration)
            {
                _snapshots.TryRemove(Key(siteSlug, clientIp), out _);
                return null;
            }
            return entry.Snapshot;
        }
        return null;
    }

    /// <summary>
    /// Removes the snapshot for a client IP.
    /// </summary>
    public void RemoveSnapshot(string siteSlug, string clientIp)
    {
        if (_snapshots.TryRemove(Key(siteSlug, clientIp), out _))
        {
            _logger.LogDebug("Removed snapshot for {ClientIp}", clientIp);
        }
    }

    private void CleanupExpiredSnapshots()
    {
        var cutoff = DateTime.UtcNow - SnapshotExpiration;
        var expiredKeys = _snapshots
            .Where(kvp => kvp.Value.CapturedAt < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _snapshots.TryRemove(key, out _);
        }

        if (expiredKeys.Count > 0)
        {
            _logger.LogDebug("Cleaned up {Count} expired snapshots", expiredKeys.Count);
        }
    }

    /// <summary>
    /// Poll the WiFiman endpoint for the target client and enrich the snapshot.
    /// Uses the higher of WiFiman vs stat/sta rates for the target client.
    /// Also stores band/channel info from WiFiman.
    /// </summary>
    private async Task EnrichWithWiFiManAsync(
        WirelessRateSnapshot snapshot,
        string clientIp,
        DiscoveredClient? targetClient)
    {
        if (_clientProvider.Client == null || targetClient == null || targetClient.IsWired)
            return;

        try
        {
            var wifiman = await _clientProvider.Client.GetWiFiManClientAsync(clientIp);
            if (wifiman == null)
                return;

            // Store WiFiman band/channel data
            // WiFiman reports from client perspective; our snapshot uses AP perspective
            // Client upload = AP RX (FromDevice), Client download = AP TX (ToDevice)
            var wifimanTx = wifiman.LinkUploadRateKbps ?? 0;
            var wifimanRx = wifiman.LinkDownloadRateKbps ?? 0;

            snapshot.WiFiManData[clientIp] = new WiFiManClientInfo
            {
                TxKbps = wifimanTx,
                RxKbps = wifimanRx,
                Band = wifiman.RadioCode,
                Channel = wifiman.Channel,
                ChannelWidth = wifiman.ChannelWidth
            };

            if (!string.IsNullOrEmpty(targetClient.Mac) &&
                snapshot.ClientRates.TryGetValue(targetClient.Mac, out var existing))
            {
                var bestTx = Math.Max(existing.TxKbps, wifimanTx);
                var bestRx = Math.Max(existing.RxKbps, wifimanRx);
                snapshot.ClientRates[targetClient.Mac] = (bestTx, bestRx, existing.ApMac);

                _logger.LogDebug(
                    "WiFiman enriched snapshot for {ClientIp}: stat/sta Tx={StaTx}Kbps Rx={StaRx}Kbps, WiFiman Tx={WmTx}Kbps Rx={WmRx}Kbps, best Tx={BestTx}Kbps Rx={BestRx}Kbps",
                    clientIp, existing.TxKbps, existing.RxKbps, wifimanTx, wifimanRx, bestTx, bestRx);
            }
            else if (!string.IsNullOrEmpty(targetClient.Mac) && (wifimanTx > 0 || wifimanRx > 0))
            {
                // stat/sta didn't have rates but WiFiman does
                snapshot.ClientRates[targetClient.Mac] = (wifimanTx, wifimanRx, targetClient.ConnectedToDeviceMac);

                _logger.LogDebug(
                    "WiFiman provided snapshot rates for {ClientIp} (no stat/sta rates): Tx={Tx}Kbps Rx={Rx}Kbps",
                    clientIp, wifimanTx, wifimanRx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WiFiman enrichment failed for snapshot {ClientIp}", clientIp);
        }
    }

    /// <summary>Internal wrapper for snapshot with expiration tracking</summary>
    private record SnapshotEntry(WirelessRateSnapshot Snapshot, DateTime CapturedAt);
}
