using System.Collections.Concurrent;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Stores wireless rate snapshots captured during speed tests.
/// Snapshots are keyed by client IP and auto-expire after 2 minutes.
/// </summary>
public class TopologySnapshotService
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

            // Store snapshot (overwrite any existing for this IP)
            _snapshots[clientIp] = new SnapshotEntry(snapshot, DateTime.UtcNow);

            // Find the target client to log their specific rates
            var targetClient = topology.Clients.FirstOrDefault(c => c.IpAddress == clientIp);
            if (targetClient != null && !targetClient.IsWired && snapshot.ClientRates.TryGetValue(targetClient.Mac, out var targetRates))
            {
                _logger.LogDebug(
                    "Captured snapshot for {ClientIp} ({Name}): Tx={Tx}Kbps, Rx={Rx}Kbps ({Total} clients, {Mesh} mesh)",
                    clientIp, targetClient.Name ?? "Unknown", targetRates.TxKbps, targetRates.RxKbps,
                    snapshot.ClientRates.Count, snapshot.MeshUplinkRates.Count);
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

    /// <summary>Internal wrapper for snapshot with expiration tracking</summary>
    private record SnapshotEntry(WirelessRateSnapshot Snapshot, DateTime CapturedAt);
}
