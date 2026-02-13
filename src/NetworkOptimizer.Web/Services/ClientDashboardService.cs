using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NetworkOptimizer.Storage.Models;
using NetworkOptimizer.UniFi;
using NetworkOptimizer.UniFi.Models;
using NetworkOptimizer.Web.Models;

namespace NetworkOptimizer.Web.Services;

/// <summary>
/// Service for the Client Dashboard - identifies clients, polls signal quality,
/// manages signal logs, and provides history data.
/// </summary>
public class ClientDashboardService
{
    private readonly ILogger<ClientDashboardService> _logger;
    private readonly IDbContextFactory<NetworkOptimizerDbContext> _dbFactory;
    private readonly UniFiConnectionService _connectionService;
    private readonly INetworkPathAnalyzer _pathAnalyzer;
    private readonly ClientSpeedTestService _speedTestService;
    private readonly IConfiguration _configuration;

    // Track last trace hash per client MAC to detect changes
    private readonly Dictionary<string, string> _lastTraceHashes = new();
    private readonly object _traceHashLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ClientDashboardService(
        ILogger<ClientDashboardService> logger,
        IDbContextFactory<NetworkOptimizerDbContext> dbFactory,
        UniFiConnectionService connectionService,
        INetworkPathAnalyzer pathAnalyzer,
        ClientSpeedTestService speedTestService,
        IConfiguration configuration)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _connectionService = connectionService;
        _pathAnalyzer = pathAnalyzer;
        _speedTestService = speedTestService;
        _configuration = configuration;
    }

    /// <summary>
    /// Identify a client by its IP address using UniFi controller data.
    /// </summary>
    public async Task<ClientIdentity?> IdentifyClientAsync(string clientIp)
    {
        if (!_connectionService.IsConnected || _connectionService.Client == null)
            return null;

        try
        {
            var clients = await _connectionService.Client.GetClientsAsync();
            var client = clients?.FirstOrDefault(c => c.Ip == clientIp);

            if (client == null)
                return null;

            var identity = MapClientToIdentity(client);

            // Enrich with AP info
            await EnrichWithApInfoAsync(identity, client.ApMac);

            return identity;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to identify client {Ip}", clientIp);
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
        int? gpsAccuracy = null)
    {
        var identity = await IdentifyClientAsync(clientIp);
        if (identity == null)
            return null;

        var result = new SignalPollResult
        {
            Client = identity,
            Timestamp = DateTime.UtcNow
        };

        // Run L2 trace
        try
        {
            var serverIp = _configuration["HOST_IP"];
            var path = await _pathAnalyzer.CalculatePathAsync(
                clientIp, serverIp, retryOnFailure: false);

            if (path.IsValid)
            {
                var analysis = _pathAnalyzer.AnalyzeSpeedTest(path, 0, 0);
                result.PathAnalysis = analysis;

                // Compute trace hash for dedup
                var traceJson = JsonSerializer.Serialize(analysis, JsonOptions);
                result.TraceHash = ComputeTraceHash(traceJson);

                // Check if trace changed
                lock (_traceHashLock)
                {
                    if (_lastTraceHashes.TryGetValue(identity.Mac, out var lastHash))
                    {
                        result.TraceChanged = lastHash != result.TraceHash;
                    }
                    else
                    {
                        result.TraceChanged = true; // First poll
                    }
                    _lastTraceHashes[identity.Mac] = result.TraceHash;
                }

                // Store signal log
                await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
            }
            else
            {
                // Store without trace
                result.TraceChanged = false;
                await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Trace failed for {Ip}, storing signal-only log", clientIp);
            await StoreSignalLogAsync(identity, result, gpsLat, gpsLng, gpsAccuracy);
        }

        return result;
    }

    /// <summary>
    /// Get signal history for a client within a time range.
    /// Fills forward TraceJson for entries that didn't store it (dedup optimization).
    /// </summary>
    public async Task<List<SignalHistoryEntry>> GetSignalHistoryAsync(
        string mac, DateTime from, DateTime to)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var logs = await db.ClientSignalLogs
            .Where(l => l.ClientMac == mac && l.Timestamp >= from && l.Timestamp <= to)
            .OrderBy(l => l.Timestamp)
            .ToListAsync();

        return logs.Select(l => new SignalHistoryEntry
        {
            Timestamp = l.Timestamp,
            SignalDbm = l.SignalDbm,
            NoiseDbm = l.NoiseDbm,
            Channel = l.Channel,
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
    /// Get trace change events for a client (entries where TraceJson is stored).
    /// </summary>
    public async Task<List<TraceChangeEntry>> GetTraceHistoryAsync(
        string mac, DateTime from, DateTime to)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

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
        await using var db = await _dbFactory.CreateDbContextAsync();

        return await db.Iperf3Results
            .Where(r => (r.Direction == SpeedTestDirection.ClientToServer
                       || r.Direction == SpeedTestDirection.BrowserToServer)
                      && r.ClientMac == mac
                      && r.TestTime >= from
                      && r.TestTime <= to)
            .OrderByDescending(r => r.TestTime)
            .ToListAsync();
    }

    /// <summary>
    /// Update the most recent signal log entry with GPS coordinates.
    /// </summary>
    public async Task SubmitGpsAsync(string clientMac, double lat, double lng, int? accuracy)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

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
        await using var db = await _dbFactory.CreateDbContextAsync();

        var cutoff = DateTime.UtcNow.AddDays(-retentionDays);

        // Delete in batches to avoid long-running transactions
        int deleted;
        do
        {
            deleted = await db.ClientSignalLogs
                .Where(l => l.Timestamp < cutoff)
                .Take(1000)
                .ExecuteDeleteAsync();
        } while (deleted == 1000);

        if (deleted > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old signal log entries", deleted);
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
    }

    private async Task StoreSignalLogAsync(
        ClientIdentity identity,
        SignalPollResult poll,
        double? gpsLat,
        double? gpsLng,
        int? gpsAccuracy)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var log = new ClientSignalLog
            {
                Timestamp = poll.Timestamp,
                ClientMac = identity.Mac,
                ClientIp = identity.Ip,
                DeviceName = identity.DisplayName,
                SignalDbm = identity.SignalDbm,
                NoiseDbm = identity.NoiseDbm,
                Channel = identity.Channel,
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

    private ClientIdentity MapClientToIdentity(UniFiClientResponse client)
    {
        return new ClientIdentity
        {
            Mac = client.Mac,
            Name = !string.IsNullOrEmpty(client.Name) ? client.Name : null,
            Hostname = !string.IsNullOrEmpty(client.Hostname) ? client.Hostname : null,
            Ip = client.Ip,
            IsWired = client.IsWired,
            SignalDbm = client.Signal,
            NoiseDbm = client.Noise,
            Channel = client.Channel,
            Band = client.Radio,
            Protocol = client.RadioProto,
            TxRateKbps = client.TxRate,
            RxRateKbps = client.RxRate,
            IsMlo = client.IsMlo ?? false,
            MloLinks = client.MloDetails,
            ApMac = client.ApMac,
            Oui = client.Oui,
            NetworkName = client.Network,
            Essid = client.Essid,
            Satisfaction = client.Satisfaction
        };
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
                }
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

    private static string ComputeTraceHash(string traceJson)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(traceJson));
        return Convert.ToHexStringLower(bytes);
    }
}
