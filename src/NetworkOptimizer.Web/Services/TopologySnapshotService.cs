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
    Task CaptureSnapshotAsync(string clientIp);

    /// <summary>
    /// Gets the snapshot for a client IP, if it exists and hasn't expired.
    /// </summary>
    WirelessRateSnapshot? GetSnapshot(string clientIp);

    /// <summary>
    /// Removes the snapshot for a client IP.
    /// </summary>
    void RemoveSnapshot(string clientIp);
}

/// <summary>
/// Stores wireless rate snapshots captured during speed tests.
/// Snapshots are keyed by client IP and auto-expire after 2 minutes.
/// </summary>
public class TopologySnapshotService : ITopologySnapshotService
{
    private readonly IUniFiClientProvider _clientProvider;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<TopologySnapshotService> _logger;

    private readonly ConcurrentDictionary<string, SnapshotEntry> _snapshots = new();
    private static readonly TimeSpan SnapshotExpiration = TimeSpan.FromMinutes(2);

    public TopologySnapshotService(
        IUniFiClientProvider clientProvider,
        INetworkPathAnalyzer pathAnalyzer,
        ILoggerFactory loggerFactory,
        ILogger<TopologySnapshotService> logger)
    {
        _clientProvider = clientProvider;
        _pathAnalyzer = pathAnalyzer;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Captures a wireless rate snapshot for the given client IP.
    /// This invalidates the topology cache first to ensure fresh data.
    /// </summary>
    public async Task CaptureSnapshotAsync(string clientIp)
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

            var topology = await discovery.DiscoverTopologyAsync();
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

            // Also poll WiFiman for the target client's realtime rates
            var targetClient = topology.Clients.FirstOrDefault(c => c.IpAddress == clientIp);
            await EnrichWithWiFiManAsync(snapshot, clientIp, targetClient);

            // Store snapshot (overwrite any existing for this IP)
            _snapshots[clientIp] = new SnapshotEntry(snapshot, DateTime.UtcNow);

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
    public WirelessRateSnapshot? GetSnapshot(string clientIp)
    {
        if (_snapshots.TryGetValue(clientIp, out var entry))
        {
            // Check if expired
            if (DateTime.UtcNow - entry.CapturedAt > SnapshotExpiration)
            {
                _snapshots.TryRemove(clientIp, out _);
                return null;
            }
            return entry.Snapshot;
        }
        return null;
    }

    /// <summary>
    /// Removes the snapshot for a client IP.
    /// </summary>
    public void RemoveSnapshot(string clientIp)
    {
        if (_snapshots.TryRemove(clientIp, out _))
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
            snapshot.WiFiManData[clientIp] = new WiFiManClientInfo
            {
                TxKbps = wifiman.LinkDownloadRateKbps ?? 0,
                RxKbps = wifiman.LinkUploadRateKbps ?? 0,
                Band = wifiman.RadioCode,
                Channel = wifiman.Channel,
                ChannelWidth = wifiman.ChannelWidth
            };

            // Use higher of WiFiman vs stat/sta rates (AP perspective: Tx=download, Rx=upload)
            var wifimanTx = wifiman.LinkDownloadRateKbps ?? 0;
            var wifimanRx = wifiman.LinkUploadRateKbps ?? 0;

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
